using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Posts Target Scheduler's plan preview for tonight to Discord as the SkyShare
    /// altitude chart, with each scheduled target's completion before and after the
    /// night. Put it before the Target Scheduler container (not inside it).
    ///
    /// Never fails the sequence.
    /// </summary>
    [ExportMetadata("Name", "SkyShare: Scheduler Preview")]
    [ExportMetadata("Description", "Posts Target Scheduler's plan preview for tonight to Discord: altitude chart, target order and project completion. Place before the Target Scheduler container.")]
    [ExportMetadata("Icon", "SkyShareLogoSVG")]
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SchedulerPreview : SequenceItem {
        private readonly IProfileService profileService;

        [ImportingConstructor]
        public SchedulerPreview(IProfileService profileService) {
            this.profileService = profileService;
        }

        private SchedulerPreview(SchedulerPreview copyMe) : this(copyMe.profileService) {
            CopyMetaData(copyMe);
            SaveFolder = copyMe.SaveFolder;
        }

        private string saveFolder = "";
        /// <summary>Where to also save the chart PNG; empty = Discord only.</summary>
        [JsonProperty]
        public string SaveFolder { get => saveFolder; set { saveFolder = value ?? ""; RaisePropertyChanged(); } }

        private string lastSummary = "";
        public string LastSummary { get => lastSummary; private set { lastSummary = value; RaisePropertyChanged(); } }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            progress?.Report(new ApplicationStatus { Status = "Running Target Scheduler preview" });
            LastSummary = await SchedulerNightPreview.Publish(profileService, SaveFolder);
        }

        public override object Clone() => new SchedulerPreview(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(SchedulerPreview)}";
    }
}
