using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using SkyShare.Planner.Core;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Start-of-night health check, posted to Discord as one list. Put it after
    /// connecting equipment and cooling the camera, before the first target.
    /// By default it only reports; "Stop on failure" fails the instruction (and so
    /// follows the sequence's error behaviour) when any check is FAIL.
    /// </summary>
    [ExportMetadata("Name", "SkyShare: Health Check")]
    [ExportMetadata("Description", "Checks equipment, camera cooling, disk space, location, Dropbox, the Discord webhook and the PC clock, and posts the result to Discord.")]
    [ExportMetadata("Icon", "SkyShareLogoSVG")]
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SkyShareHealthCheck : SequenceItem {
        private readonly IProfileService profileService;
        private readonly HealthDevices devices;

        [ImportingConstructor]
        public SkyShareHealthCheck(IProfileService profileService, ICameraMediator camera, ITelescopeMediator mount,
                                   IGuiderMediator guider, IFocuserMediator focuser, IFilterWheelMediator filterWheel,
                                   IRotatorMediator rotator)
            : this(profileService, new HealthDevices(camera, mount, guider, focuser, filterWheel, rotator)) { }

        private SkyShareHealthCheck(IProfileService profileService, HealthDevices devices) {
            this.profileService = profileService;
            this.devices = devices;
        }

        private SkyShareHealthCheck(SkyShareHealthCheck copyMe) : this(copyMe.profileService, copyMe.devices) {
            CopyMetaData(copyMe);
            MinFreeGb = copyMe.MinFreeGb;
            CheckDropbox = copyMe.CheckDropbox;
            StopOnFailure = copyMe.StopOnFailure;
        }

        private double minFreeGb = 20;
        /// <summary>Spare disk space wanted on top of tonight's estimate.</summary>
        [JsonProperty]
        public double MinFreeGb { get => minFreeGb; set { minFreeGb = Math.Max(0, value); RaisePropertyChanged(); } }

        private bool checkDropbox = true;
        [JsonProperty]
        public bool CheckDropbox { get => checkDropbox; set { checkDropbox = value; RaisePropertyChanged(); } }

        private bool stopOnFailure;
        [JsonProperty]
        public bool StopOnFailure { get => stopOnFailure; set { stopOnFailure = value; RaisePropertyChanged(); } }

        private string lastResult = "";
        public string LastResult { get => lastResult; private set { lastResult = value; RaisePropertyChanged(); } }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            progress?.Report(new ApplicationStatus { Status = "Running health check" });
            var checks = await HealthChecker.Run(profileService, devices, MinFreeGb, CheckDropbox);
            var now = DateTime.Now;
            var summary = HealthReport.Summary(checks);
            var problems = checks.Where(c => c.Level != HealthLevel.Ok).ToList();
            LastResult = problems.Count == 0 ? summary
                : summary + ": " + string.Join("; ", problems.Select(c => $"{HealthReport.Label(c.Level)} {c.Name}: {c.Detail}"));

            var data = new {
                checks = checks.Select(c => new { name = c.Name, level = HealthReport.Label(c.Level), detail = c.Detail }),
            };
            var markdown = HealthReport.Markdown(checks, now, profileService.ActiveProfile.Name);
            var worst = HealthReport.Worst(checks);
            StatusReporter.ReportWithImage("health_check", summary, markdown, null, "", data);
            if (worst == HealthLevel.Fail)
                StatusReporter.Warn("health_check_failed", LastResult, null, postToDiscord: false);

            if (StopOnFailure && worst == HealthLevel.Fail)
                throw new Exception($"Health check failed: {string.Join("; ", problems.Where(c => c.Level == HealthLevel.Fail).Select(c => $"{c.Name}: {c.Detail}"))}");
        }

        public override object Clone() => new SkyShareHealthCheck(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(SkyShareHealthCheck)}";
    }
}
