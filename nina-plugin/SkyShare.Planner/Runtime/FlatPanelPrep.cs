using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// Gets the flat panel ready to cover the scope before calibration frames. Before the
    /// calibration sequence the panel is normally off and disconnected, so (per the plugin
    /// options): power it up on its switch port, connect it in NINA, close the cover, and
    /// optionally disconnect it again. Throws when the cover can't be confirmed closed,
    /// since darks taken with the cover open are useless.
    /// </summary>
    public static class FlatPanelPrep {
        private static readonly TimeSpan ConnectFor = TimeSpan.FromSeconds(90), RetryEvery = TimeSpan.FromSeconds(10);

        /// <summary>Port names of the connected switch, in NINA's order (empty when no switch is connected).</summary>
        public static IReadOnlyList<string> SwitchPorts(ISwitchMediator switches) {
            var info = switches.GetInfo();
            return info is { Connected: true, WritableSwitches: { } w } ? w.Select(s => s.Name).ToList() : Array.Empty<string>();
        }

        public static async Task Run(ISwitchMediator switches, IFlatDeviceMediator flat, SkyShareOptions o,
                                     IProgress<ApplicationStatus>? progress, CancellationToken token) {
            void Status(string s) => progress?.Report(new ApplicationStatus { Status = s });

            if (o.ConnectFlatPanelForCalibration) {
                if (!string.IsNullOrWhiteSpace(o.FlatPanelSwitch)) {
                    Status($"Powering the flat panel on ({o.FlatPanelSwitch})");
                    await PowerOn(switches, o.FlatPanelSwitch.Trim(), progress, token);
                }
                if (!flat.GetInfo().Connected) {
                    Status("Connecting the flat panel");
                    await Connect(flat, token);
                }
            }

            var info = flat.GetInfo();
            if (!info.Connected)
                throw new Exception("the flat panel is not connected, so its cover can't be closed " +
                                    "(turn on 'Connect flat panel before calibration' in the plugin options, or connect it earlier)");
            if (!info.SupportsOpenClose)
                StatusReporter.Warn("flat_cover_unsupported", "the flat panel has no cover to close; make sure the scope is covered for the darks.");
            else if (info.CoverState != CoverState.Closed) {
                Status("Closing the flat panel");
                await flat.CloseCover(progress, token);
                var state = flat.GetInfo().CoverState;
                if (state != CoverState.Closed)
                    throw new Exception($"the flat panel cover did not close (it reports {state})");
            }
            StatusReporter.Report("flat_cover_closed", "flat panel closed for calibration");

            if (o.DisconnectFlatPanelWhenClosed) {
                Status("Disconnecting the flat panel");
                await flat.Disconnect();
            }
        }

        private static async Task PowerOn(ISwitchMediator switches, string port, IProgress<ApplicationStatus>? progress, CancellationToken token) {
            if (!switches.GetInfo().Connected && !await switches.Connect())
                throw new Exception($"the switch is not connected, so the flat panel port '{port}' can't be turned on");
            var writable = switches.GetInfo().WritableSwitches?.ToList() ?? new();
            var index = writable.FindIndex(s => string.Equals(s.Name, port, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new Exception($"the switch has no port named '{port}' (ports: {string.Join(", ", writable.Select(s => s.Name))})");
            var sw = writable[index];
            if (sw.Value >= sw.Maximum) return; // already on
            await switches.SetSwitchValue((short)index, sw.Maximum, progress, token);
            await Task.Delay(TimeSpan.FromSeconds(5), token); // let the panel start before NINA talks to it
        }

        /// <summary>A freshly powered panel can take a while to answer: retry for up to 90 s.</summary>
        private static async Task Connect(IFlatDeviceMediator flat, CancellationToken token) {
            var until = DateTime.Now + ConnectFor;
            while (true) {
                try {
                    if (await flat.Connect() || flat.GetInfo().Connected) return;
                } catch (Exception) when (DateTime.Now < until) { /* still starting */ }
                if (DateTime.Now >= until)
                    throw new Exception("could not connect the flat panel within 90 s (is it selected in the NINA profile and powered?)");
                await Task.Delay(RetryEvery, token);
            }
        }
    }
}
