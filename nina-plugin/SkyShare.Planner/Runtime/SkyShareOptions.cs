using System;
using System.Runtime.CompilerServices;
using NINA.Core.Utility;
using NINA.Profile;
using NINA.Profile.Interfaces;
using SkyShare.Planner.Core;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// Settings that stay the same from one sequence to the next (plans folder, how
    /// targets run, estimate overheads, health check thresholds). Shown on NINA's
    /// Options > Plugins > SkyShare Planner page and stored in the active NINA profile,
    /// so a second rig profile can have its own values. Instructions read them when
    /// they run, so a change applies without editing the sequence.
    /// </summary>
    public sealed class SkyShareOptions : BaseINPC {
        /// <summary>Plugin GUID from AssemblyInfo: the key NINA stores these settings under.</summary>
        private static readonly Guid PluginGuid = Guid.Parse("20e41444-d40b-44e2-bd2f-d2f6cb0dd9df");

        private static SkyShareOptions? instance;
        private static readonly object Gate = new();

        /// <summary>The one shared instance (the options page and every instruction use the same object).</summary>
        public static SkyShareOptions Get(IProfileService profileService) {
            lock (Gate) return instance ??= new SkyShareOptions(profileService);
        }

        private readonly PluginOptionsAccessor store;

        private SkyShareOptions(IProfileService profileService) {
            store = new PluginOptionsAccessor(profileService, PluginGuid);
            // Values are per profile: refresh the page when the profile changes.
            profileService.ProfileChanged += (_, _) => RaiseAllPropertiesChanged();
        }

        private double D(double def, [CallerMemberName] string name = "") => store.GetValueDouble(name, def);
        private int I(int def, [CallerMemberName] string name = "") => store.GetValueInt32(name, def);
        private bool B(bool def, [CallerMemberName] string name = "") => store.GetValueBoolean(name, def);
        private string S(string def, [CallerMemberName] string name = "") => store.GetValueString(name, def);
        private void Set(double v, [CallerMemberName] string name = "") { store.SetValueDouble(name, v); RaisePropertyChanged(name); }
        private void Set(int v, [CallerMemberName] string name = "") { store.SetValueInt32(name, v); RaisePropertyChanged(name); }
        private void Set(bool v, [CallerMemberName] string name = "") { store.SetValueBoolean(name, v); RaisePropertyChanged(name); }
        private void Set(string v, [CallerMemberName] string name = "") { store.SetValueString(name, v); RaisePropertyChanged(name); }

        // ---- plan ---------------------------------------------------------------

        /// <summary>Dropbox folder the bot / dashboard write *.plan.json into.</summary>
        public string PlansDirectory { get => S(@"D:\Users\SkyShare\Dropbox\SkyShare Data\Plans"); set => Set((value ?? "").Trim()); }

        /// <summary>Before this local hour we are still in last evening's night.</summary>
        public int RolloverHour { get => I(12); set => Set(Math.Clamp(value, 0, 23)); }

        /// <summary>If the plan hasn't synced yet, keep checking this long before failing.</summary>
        public int WaitForPlanMinutes { get => I(10); set => Set(Math.Max(0, value)); }

        // ---- how generated target blocks run (see TargetBuilder) -----------------

        /// <summary>A target's exposures stop when it drops below this altitude (degrees).</summary>
        public double MinAltitude { get => D(20); set => Set(Math.Clamp(value, 0, 90)); }

        /// <summary>Run autofocus after centering each target.</summary>
        public bool AutofocusAtStart { get => B(true); set => Set(value); }

        /// <summary>Fixed-count targets: dither after this many frames of each filter (0 = never). Cycle targets dither once per cycle.</summary>
        public int DitherEvery { get => I(1); set => Set(Math.Max(0, value)); }

        /// <summary>Center After Drift on every target: re-center beyond this drift (arcminutes).</summary>
        public double CenterDriftThreshold { get => D(5); set => Set(Math.Clamp(value, 0.1, 600)); }

        // ---- night preview (the overheads only affect the estimate) --------------

        public bool SendNightPreview { get => B(true); set => Set(value); }

        /// <summary>Attach a sky-survey cutout per target with the camera frame at its rotation.</summary>
        public bool SendFramingPreview { get => B(true); set => Set(value); }

        /// <summary>Slew, plate solve, center and rotate, per target.</summary>
        public double SlewCenterMinutes { get => D(3); set => Set(Math.Clamp(value, 0, 60)); }
        public double AutofocusMinutes { get => D(4); set => Set(Math.Clamp(value, 0, 60)); }

        /// <summary>How often the template's autofocus triggers fire on average (0 = only at target start).</summary>
        public double AutofocusEveryMinutes { get => D(60); set => Set(Math.Clamp(value, 0, 1440)); }

        /// <summary>Dither plus guiding settle.</summary>
        public double DitherSeconds { get => D(30); set => Set(Math.Clamp(value, 0, 600)); }

        /// <summary>Download and save, per frame.</summary>
        public double FrameOverheadSeconds { get => D(5); set => Set(Math.Clamp(value, 0, 120)); }
        public double MeridianFlipMinutes { get => D(5); set => Set(Math.Clamp(value, 0, 60)); }

        // ---- camera -------------------------------------------------------------------

        /// <summary>Set Temperature's set point when the plan has no camera_temperature (°C).</summary>
        public double DefaultCameraTemperature { get => D(0); set => Set(Math.Clamp(value, -60, 40)); }

        // ---- calibration nights (SkyShareAstro Session) --------------------------------

        /// <summary>Name of the switch port that powers the flat panel ("" = no switch: the panel is always powered).</summary>
        public string FlatPanelSwitch { get => S(""); set => Set((value ?? "").Trim()); }

        /// <summary>Before the calibration sequence: power the panel on, connect it in NINA, and close its cover.</summary>
        public bool ConnectFlatPanelForCalibration { get => B(true); set => Set(value); }

        /// <summary>Disconnect the panel in NINA once its cover is closed.</summary>
        public bool DisconnectFlatPanelWhenClosed { get => B(true); set => Set(value); }

        // ---- updates ----------------------------------------------------------------

        /// <summary>Look for a newer release on GitHub after NINA starts and every 12 hours (never installs by itself).</summary>
        public bool CheckForUpdates { get => B(true); set => Set(value); }

        // ---- health check ---------------------------------------------------------

        /// <summary>Spare disk space wanted on top of tonight's estimate.</summary>
        public double MinFreeGb { get => D(20); set => Set(Math.Max(0, value)); }
        public bool CheckDropbox { get => B(true); set => Set(value); }

        // ---- derived ----------------------------------------------------------------

        public EstimateSettings EstimateSettings => new(
            MinAltitude, AutofocusAtStart, DitherEvery,
            SlewCenterSeconds: SlewCenterMinutes * 60, AutofocusSeconds: AutofocusMinutes * 60,
            AutofocusEveryMinutes: AutofocusEveryMinutes, DitherSeconds: DitherSeconds,
            FrameOverheadSeconds: FrameOverheadSeconds, MeridianFlipSeconds: MeridianFlipMinutes * 60);

        public BuildOptions BuildOptions => new(MinAltitude, AutofocusAtStart, DitherEvery, CenterDriftThreshold);
    }
}
