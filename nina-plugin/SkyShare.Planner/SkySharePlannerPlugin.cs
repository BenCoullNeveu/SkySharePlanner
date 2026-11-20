using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.MyMessageBox;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using SkyShare.Planner.Core;
using SkyShare.Planner.Runtime;

namespace SkyShare.Planner {

    /// <summary>One row of the options page's health check list.</summary>
    public sealed record HealthRow(string Level, string Name, string Detail, string Color);

    /// <summary>
    /// Plugin entry point (NINA discovers it via MEF) and the view model of its page in
    /// Options > Plugins: the plugin-wide settings (see SkyShareOptions) and a health
    /// check you can run any time. The sequencer items under Sequencer/ are exported
    /// individually.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class SkySharePlannerPlugin : PluginBase, INotifyPropertyChanged {
        private readonly IProfileService profileService;
        private readonly HealthDevices devices;
        private readonly ISequenceMediator sequenceMediator;

        [ImportingConstructor]
        public SkySharePlannerPlugin(IProfileService profileService, ISequenceMediator sequenceMediator,
                                     ICameraMediator camera, ITelescopeMediator mount,
                                     IGuiderMediator guider, IFocuserMediator focuser, IFilterWheelMediator filterWheel,
                                     IRotatorMediator rotator) {
            this.profileService = profileService;
            this.sequenceMediator = sequenceMediator;
            RestartCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(Restart);
            devices = new HealthDevices(camera, mount, guider, focuser, filterWheel, rotator);
            RunHealthCheckCommand = new AsyncRelayCommand(() => RunHealthCheck(post: false));
            PostHealthCheckCommand = new AsyncRelayCommand(() => RunHealthCheck(post: true));
        }

        /// <summary>Bound by the options page (Resources/DataTemplates.xaml, "SkyShare Planner_Options").</summary>
        public SkyShareOptions Settings => SkyShareOptions.Get(profileService);

        /// <summary>A newer version installed while NINA runs (waiting in NINA's staging folder): banner on the options page.</summary>
        public PendingUpdate Update { get; } = new();

        // ---- restart N.I.N.A. (to load a staged update) ---------------------------------

        public IRelayCommand RestartCommand { get; }

        /// <summary>Same as NINA's own restart after a plugin update, but never during a running sequence.</summary>
        private void Restart() {
            try {
                if (sequenceMediator.IsAdvancedSequenceRunning()) {
                    Notification.ShowWarning("A sequence is running. Stop it before restarting N.I.N.A.");
                    return;
                }
                var what = Update.IsPending ? $"to load SkyShare Planner {Update.StagedVersion}" : "now";
                if (MyMessageBox.Show($"Restart N.I.N.A. {what}?\n\nEquipment will disconnect. Save any sequence changes first.",
                                      "SkyShare Planner", MessageBoxButton.YesNo, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
                if (!PendingUpdate.ClearPluginFolderForStaged())
                    Notification.ShowWarning("Some plugin files could not be moved; if the update doesn't load, restart N.I.N.A. once more.");
                Logger.Info("[SkyShare] restarting N.I.N.A." + (Update.IsPending ? $" to load {Update.StagedVersion}" : ""));
                profileService.Release();
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
            } catch (Exception e) {
                Logger.Error($"[SkyShare] restart failed: {e}");
                Notification.ShowError($"Could not restart N.I.N.A.: {e.Message}");
            }
        }

        public override Task Teardown() {
            Update.Dispose();
            return base.Teardown();
        }

        // ---- health check on the options page --------------------------------------

        public IAsyncRelayCommand RunHealthCheckCommand { get; }
        public IAsyncRelayCommand PostHealthCheckCommand { get; }

        private IReadOnlyList<HealthRow> healthRows = Array.Empty<HealthRow>();
        public IReadOnlyList<HealthRow> HealthRows { get => healthRows; private set { healthRows = value; Raise(); } }

        private string healthSummary = "Not run yet. Connect your equipment, then run the checks.";
        public string HealthSummary { get => healthSummary; private set { healthSummary = value; Raise(); } }

        private bool healthRunning;
        public bool HealthRunning { get => healthRunning; private set { healthRunning = value; Raise(); } }

        private async Task RunHealthCheck(bool post) {
            if (HealthRunning) return;
            HealthRunning = true;
            HealthSummary = "Checking…";
            try {
                var checks = await HealthChecker.Run(profileService, devices, Settings);
                // Problems first, like the Discord post
                HealthRows = checks.OrderByDescending(c => c.Level).Select(c => new HealthRow(
                    HealthReport.Label(c.Level), c.Name, c.Detail,
                    c.Level switch { HealthLevel.Fail => "#E57373", HealthLevel.Warn => "#FFB74D", _ => "#81C784" })).ToList();
                HealthSummary = $"{HealthReport.Summary(checks)} at {DateTime.Now:HH:mm:ss}" + (post ? ", posted to Discord" : "");
                if (post) HealthChecker.Post(checks, profileService.ActiveProfile.Name);
            } catch (Exception e) {
                HealthSummary = $"Health check failed: {e.Message}";
            } finally {
                HealthRunning = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
