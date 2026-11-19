using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// Reports progress back to the people who made the plan.
    ///
    /// 1. Appends JSON lines to "&lt;plan&gt;.status.jsonl" next to the plan file.
    ///    Dropbox syncs it to the compute server, where the Discord bot / SkyStack
    ///    can tail it. No network setup needed.
    /// 2. Optionally posts to a Discord webhook if the environment variable
    ///    SKYSHARE_DISCORD_WEBHOOK is set on the acquisition PC (kept out of the
    ///    sequence file and out of Dropbox on purpose).
    ///
    /// Warnings and errors also go to NINA itself: its log at the matching level
    /// and a notification in the NINA window, so you can see what happened without
    /// opening the status file.
    ///
    /// Never throws: reporting must not break imaging.
    /// </summary>
    public static class StatusReporter {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly object FileGate = new();

        /// <summary>Progress: NINA log (info) + status file + webhook.</summary>
        public static void Report(string @event, string message, object? data = null) {
            Logger.Info($"[SkyShare] {@event}: {message}");
            Send("info", @event, message, data);
        }

        /// <summary>Something was skipped or looks wrong, but the night continues.</summary>
        public static void Warn(string @event, string message, object? data = null) {
            Logger.Warning($"[SkyShare] {@event}: {message}");
            Notify(() => Notification.ShowWarning($"SkyShare: {message}"));
            Send("warning", @event, message, data);
        }

        /// <summary>Something failed.</summary>
        public static void Error(string @event, string message, object? data = null) {
            Logger.Error($"[SkyShare] {@event}: {message}");
            Notify(() => Notification.ShowError($"SkyShare: {message}"));
            Send("error", @event, message, data);
        }

        private static void Send(string level, string @event, string message, object? data) {
            WriteStatusLine(level, @event, message, data);
            var prefix = level switch { "error" => "ERROR ", "warning" => "WARNING ", _ => "" };
            _ = PostWebhookAsync($"{prefix}**{@event}** - {message}");
        }

        private static void Notify(Action show) {
            try { show(); } catch (Exception e) { Logger.Warning($"[SkyShare] could not show notification: {e.Message}"); }
        }

        private static void WriteStatusLine(string level, string @event, string message, object? data) {
            try {
                var plan = PlanStore.PlanPath;
                if (plan is null) return;
                var path = Path.ChangeExtension(plan, null) + ".status.jsonl"; // x.plan.json -> x.plan.status.jsonl
                var line = JsonSerializer.Serialize(new Dictionary<string, object?> {
                    ["utc"] = DateTime.UtcNow.ToString("o"),
                    ["level"] = level,
                    ["event"] = @event,
                    ["message"] = message,
                    ["data"] = data,
                });
                lock (FileGate) File.AppendAllText(path, line + "\n", Encoding.UTF8);
            } catch (Exception e) {
                Logger.Warning($"[SkyShare] could not write status file: {e.Message}");
            }
        }

        /// <summary>
        /// Progress with a picture: status file (data only) + webhook message with the
        /// image attached. The message is posted as-is (it is already formatted).
        /// </summary>
        public static void ReportWithImage(string @event, string summary, string markdown, byte[]? png, string fileName, object? data = null) {
            Logger.Info($"[SkyShare] {@event}: {summary}");
            WriteStatusLine("info", @event, summary, data);
            _ = PostWebhookAsync(markdown, png, fileName);
        }

        private static async Task PostWebhookAsync(string content, byte[]? file = null, string fileName = "image.png") {
            try {
                var url = Environment.GetEnvironmentVariable("SKYSHARE_DISCORD_WEBHOOK");
                if (string.IsNullOrWhiteSpace(url)) return;
                var body = JsonSerializer.Serialize(new { content });
                HttpContent request;
                if (file is null) {
                    request = new StringContent(body, Encoding.UTF8, "application/json");
                } else {
                    // Discord: JSON in "payload_json", attachments as files[n]
                    var form = new MultipartFormDataContent {
                        { new StringContent(body, Encoding.UTF8, "application/json"), "payload_json" },
                    };
                    var image = new ByteArrayContent(file);
                    image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                    form.Add(image, "files[0]", fileName);
                    request = form;
                }
                using (request)
                using (var resp = await Http.PostAsync(url, request))
                    if (!resp.IsSuccessStatusCode)
                        Logger.Warning($"[SkyShare] webhook returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
            } catch (Exception e) {
                Logger.Warning($"[SkyShare] webhook failed: {e.Message}");
            }
        }
    }
}
