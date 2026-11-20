using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using SkyShare.Planner.Core;

namespace SkyShare.Planner.Runtime {

    /// <summary>Equipment mediators the health check reads. All optional.</summary>
    public sealed record HealthDevices(
        ICameraMediator Camera, ITelescopeMediator Mount, IGuiderMediator Guider,
        IFocuserMediator Focuser, IFilterWheelMediator FilterWheel, IRotatorMediator Rotator);

    /// <summary>
    /// Start-of-night checks: equipment the profile uses is connected, camera cooling,
    /// disk space for tonight's frames, observatory location, Dropbox running, the
    /// Discord webhook answers, and the PC clock agrees with internet time.
    /// Each check is independent and never throws.
    /// </summary>
    public static class HealthChecker {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

        public static async Task<IReadOnlyList<HealthCheck>> Run(IProfileService profileService, HealthDevices d,
                                                                 double minFreeGb, bool checkDropbox) {
            var p = profileService.ActiveProfile;
            var checks = new List<HealthCheck>();
            void Add(Func<HealthCheck> check, string name) {
                try { checks.Add(check()); } catch (Exception e) { checks.Add(new HealthCheck(name, HealthLevel.Warn, $"could not check: {e.Message}")); }
            }

            // Equipment: only devices the profile actually uses.
            Add(() => Device("Camera", p.CameraSettings.Id, d.Camera.GetInfo().Connected), "Camera");
            Add(() => Device("Mount", p.TelescopeSettings.Id, d.Mount.GetInfo().Connected), "Mount");
            Add(() => Device("Guider", p.GuiderSettings.GuiderName, d.Guider.GetInfo().Connected), "Guider");
            Add(() => Device("Focuser", p.FocuserSettings.Id, d.Focuser.GetInfo().Connected), "Focuser");
            Add(() => Device("Filter wheel", p.FilterWheelSettings.Id, d.FilterWheel.GetInfo().Connected), "Filter wheel");
            Add(() => Device("Rotator", p.RotatorSettings.Id, d.Rotator.GetInfo().Connected), "Rotator");

            Add(() => {
                var cam = d.Camera.GetInfo();
                if (!cam.Connected) return new HealthCheck("Camera cooling", HealthLevel.Warn, "camera not connected");
                return HealthReport.Cooling(cam.CanSetTemperature, cam.CoolerOn, cam.Temperature, cam.TemperatureSetPoint);
            }, "Camera cooling");

            Add(() => {
                var a = p.AstrometrySettings;
                return a.Latitude == 0 && a.Longitude == 0
                    ? new HealthCheck("Location", HealthLevel.Fail, "observatory latitude/longitude not set in the profile")
                    : new HealthCheck("Location", HealthLevel.Ok, $"{a.Latitude:0.00}°, {a.Longitude:0.00}°");
            }, "Location");

            Add(() => DiskSpace(profileService, d, minFreeGb), "Disk space");

            if (checkDropbox)
                Add(() => Process.GetProcessesByName("Dropbox").Length > 0
                    ? new HealthCheck("Dropbox", HealthLevel.Ok, "running")
                    : new HealthCheck("Dropbox", HealthLevel.Warn, "not running: plans and status files won't sync"), "Dropbox");

            checks.Add(await Discord());
            checks.Add(HealthReport.Clock(await Task.Run(ClockOffsetSeconds)));
            return checks;
        }

        private static readonly string[] NoDevice = { "", "No_Device", "No_Guider", "None" };

        private static HealthCheck Device(string name, string? profileId, bool connected) {
            if (NoDevice.Contains(profileId ?? "", StringComparer.OrdinalIgnoreCase))
                return connected ? new HealthCheck(name, HealthLevel.Ok, "connected")
                                 : new HealthCheck(name, HealthLevel.Ok, "not used by this profile");
            return connected ? new HealthCheck(name, HealthLevel.Ok, "connected")
                             : new HealthCheck(name, HealthLevel.Fail, $"not connected ({profileId})");
        }

        private static HealthCheck DiskSpace(IProfileService profileService, HealthDevices d, double minFreeGb) {
            var folder = profileService.ActiveProfile.ImageFileSettings.FilePath;
            if (string.IsNullOrWhiteSpace(folder) || Path.GetPathRoot(Path.GetFullPath(folder)) is not { } root)
                return new HealthCheck("Disk space", HealthLevel.Warn, "no image folder set in the profile");
            var free = new DriveInfo(root).AvailableFreeSpace;

            // Tonight's need: estimated light frames (+25% for flats/darks) × 16-bit frame size.
            long need = 0;
            var cam = d.Camera.GetInfo();
            if (PlanStore.Plan is { } plan && cam.Connected && cam.XSize > 0) {
                var a = profileService.ActiveProfile.AstrometrySettings;
                var est = NightEstimator.Estimate(plan, new Site(a.Latitude, a.Longitude), new EstimateSettings(), notBefore: DateTime.Now);
                var frames = est.Targets.Sum(t => t.FrameCount) * 1.25;
                need = (long)(frames * cam.XSize * (long)cam.YSize * 2);
            }
            return HealthReport.Disk(folder, free, need, minFreeGb);
        }

        private static async Task<HealthCheck> Discord() {
            var url = StatusReporter.WebhookUrl();
            if (string.IsNullOrWhiteSpace(url))
                return new HealthCheck("Discord", HealthLevel.Warn, "no webhook set (Discord Alert plugin or SKYSHARE_DISCORD_WEBHOOK)");
            try {
                using var resp = await Http.GetAsync(url); // a webhook URL answers GET with its details
                return resp.IsSuccessStatusCode
                    ? new HealthCheck("Discord", HealthLevel.Ok, "webhook answers")
                    : new HealthCheck("Discord", HealthLevel.Fail, $"webhook returned {(int)resp.StatusCode} (deleted or wrong URL?)");
            } catch (Exception e) {
                return new HealthCheck("Discord", HealthLevel.Warn, $"could not reach Discord: {e.Message}");
            }
        }

        /// <summary>SNTP query to Windows' default time server; null if it can't be reached in 3 s.</summary>
        private static double? ClockOffsetSeconds() {
            try {
                var request = new byte[48];
                request[0] = 0x1B; // LI 0, version 3, client mode
                using var udp = new UdpClient();
                udp.Client.ReceiveTimeout = 3000;
                udp.Connect("time.windows.com", 123);
                var sent = DateTime.UtcNow;
                udp.Send(request, request.Length);
                System.Net.IPEndPoint? from = null;
                var reply = udp.Receive(ref from);
                var received = DateTime.UtcNow;
                if (reply.Length < 48) return null;
                static DateTime Stamp(byte[] b, int i) {
                    ulong sec = (ulong)b[i] << 24 | (ulong)b[i + 1] << 16 | (ulong)b[i + 2] << 8 | b[i + 3];
                    ulong frac = (ulong)b[i + 4] << 24 | (ulong)b[i + 5] << 16 | (ulong)b[i + 6] << 8 | b[i + 7];
                    return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(sec + frac / 4294967296.0);
                }
                var serverReceive = Stamp(reply, 32);
                var serverSend = Stamp(reply, 40);
                // offset = server - local: ((t1 - t0) + (t2 - t3)) / 2; we report local - server
                var offset = ((serverReceive - sent) + (serverSend - received)).TotalSeconds / 2;
                return -offset;
            } catch {
                return null;
            }
        }
    }
}
