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
            StopOnFailure = copyMe.StopOnFailure;
        }

        // Spare disk space and the Dropbox check are on Options > Plugins > SkyShare Planner.
        // Stop on failure stays here: it depends on where the instruction sits in the sequence.

        private bool stopOnFailure;
        [JsonProperty]
        public bool StopOnFailure { get => stopOnFailure; set { stopOnFailure = value; RaisePropertyChanged(); } }

        private string lastResult = "";
        public string LastResult { get => lastResult; private set { lastResult = value; RaisePropertyChanged(); } }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            progress?.Report(new ApplicationStatus { Status = "Running health check" });
            var checks = await HealthChecker.Run(profileService, devices, SkyShareOptions.Get(profileService));
            LastResult = HealthChecker.OneLine(checks);
            HealthChecker.Post(checks, profileService.ActiveProfile.Name);
            var worst = HealthReport.Worst(checks);
            var problems = checks.Where(c => c.Level != HealthLevel.Ok).ToList();

            if (StopOnFailure && worst == HealthLevel.Fail)
                throw new Exception($"Health check failed: {string.Join("; ", problems.Where(c => c.Level == HealthLevel.Fail).Select(c => $"{c.Name}: {c.Detail}"))}");
        }

        public override object Clone() => new SkyShareHealthCheck(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(SkyShareHealthCheck)}";
    }
}
