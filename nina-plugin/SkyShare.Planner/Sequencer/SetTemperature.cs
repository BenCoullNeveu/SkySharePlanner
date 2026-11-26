using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner.Sequencer {

    /// <summary>
    /// Turns the camera cooler on and brings the sensor to a set point, optionally ramping
    /// over a few minutes, and reports the result. The set point is the loaded plan's
    /// "camera_temperature" when it has one (and "Use the plan's temperature" is on),
    /// otherwise the instruction's fallback value (default 0 °C). A set point that
    /// isn't reached is a warning and the sequence continues.
    /// </summary>
    [ExportMetadata("Name", "SkyShare: Set Temperature")]
    [ExportMetadata("Description", "Cools the camera to the plan's camera_temperature, or this instruction's set point (default 0 °C), and reports the sensor temperature.")]
    [ExportMetadata("Icon", "SkyShareLogoSVG")]
    [ExportMetadata("Category", "SkyShare")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SetTemperature : SequenceItem, IValidatable {
        private readonly ICameraMediator camera;

        [ImportingConstructor]
        public SetTemperature(ICameraMediator camera) {
            this.camera = camera;
        }

        private SetTemperature(SetTemperature copyMe) : this(copyMe.camera) {
            CopyMetaData(copyMe);
            Temperature = copyMe.Temperature;
            UsePlanTemperature = copyMe.UsePlanTemperature;
            RampMinutes = copyMe.RampMinutes;
        }

        private double temperature;
        /// <summary>Set point in °C when the plan gives none (or "Use the plan's temperature" is off).</summary>
        [JsonProperty]
        public double Temperature { get => temperature; set { temperature = Math.Clamp(value, -60, 40); RaisePropertyChanged(); RefreshPlanInfo(); } }

        private double rampMinutes;
        /// <summary>Minimum duration of the cool-down (0 = straight to the set point).</summary>
        [JsonProperty]
        public double RampMinutes { get => rampMinutes; set { rampMinutes = Math.Clamp(value, 0, 120); RaisePropertyChanged(); } }

        private bool usePlanTemperature = true;
        /// <summary>Use the loaded plan's camera_temperature when it has one.</summary>
        [JsonProperty]
        public bool UsePlanTemperature { get => usePlanTemperature; set { usePlanTemperature = value; RaisePropertyChanged(); RefreshPlanInfo(); } }


        private string lastResult = "";
        public string LastResult { get => lastResult; private set { lastResult = value; RaisePropertyChanged(); } }

        private string planInfo = "";
        /// <summary>Shown on the instruction: the loaded plan's camera_temperature and whether it is used.</summary>
        public string PlanInfo { get => planInfo; private set { if (planInfo != value) { planInfo = value; RaisePropertyChanged(); } } }

        private void RefreshPlanInfo() {
            var plan = PlanStore.Plan;
            var (setPoint, _) = Resolve();
            PlanInfo = plan is null ? $"No plan loaded: uses the fallback, {setPoint:0.#} °C"
                : plan.CameraTemperature is not { } t ? $"Plan sets no temperature: uses the fallback, {setPoint:0.#} °C"
                : UsePlanTemperature ? $"Plan temperature: {t:0.#} °C"
                : $"Plan temperature: {t:0.#} °C (not used: fallback {setPoint:0.#} °C)";
        }

        /// <summary>Tonight's set point and where it came from.</summary>
        public (double SetPoint, string Source) Resolve() =>
            UsePlanTemperature && PlanStore.Plan?.CameraTemperature is { } t
                ? (t, "from the plan")
                : (Temperature, PlanStore.Plan is null || !UsePlanTemperature ? "fallback" : "plan has none, fallback");

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            RefreshPlanInfo();
            var (setPoint, source) = Resolve();
            progress?.Report(new ApplicationStatus { Status = $"Cooling camera to {setPoint:0.#} °C ({source})" });
            var reached = await camera.CoolCamera(setPoint, TimeSpan.FromMinutes(RampMinutes), progress, token);
            var info = camera.GetInfo();
            var now = info.Connected ? $"{info.Temperature:0.#} °C" : "unknown (camera not connected)";
            LastResult = reached ? $"At {setPoint:0.#} °C, {source} (sensor {now})"
                                 : $"Set point {setPoint:0.#} °C ({source}) not reached (sensor {now})";
            var data = new { set_point = setPoint, source, sensor = info.Connected ? info.Temperature : (double?)null, reached };
            if (reached) {
                StatusReporter.Report("camera_cooled", $"Camera at {setPoint:0.#} °C, {source} (sensor {now})", data);
            } else {
                StatusReporter.Warn("camera_not_cooled", $"Camera did not reach {setPoint:0.#} °C, {source} (sensor {now})", data);
            }
        }

        // ---- validation shown in the sequencer before start --------------------

        private IList<string> issues = new List<string>();
        public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

        public bool Validate() {
            RefreshPlanInfo(); // NINA validates every few seconds, so this follows Load Plan
            var i = new List<string>();
            var info = camera.GetInfo();
            if (!info.Connected) i.Add("Camera is not connected");
            else if (!info.CanSetTemperature) i.Add("This camera can't set its temperature");
            Issues = i;
            return i.Count == 0;
        }

        public override object Clone() => new SetTemperature(this);

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(SetTemperature)}, Temperature: {Temperature}, From plan: {UsePlanTemperature}, Min duration: {RampMinutes} min";
    }
}
