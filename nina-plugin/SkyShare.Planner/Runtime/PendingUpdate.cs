using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// Notices when a newer SkyShare Planner has been installed while NINA is running.
    /// The installer puts it in NINA's plugin staging folder, and NINA only swaps it in
    /// at its next start (PluginLoader.DeployFromStaging), so until then the options page
    /// shows a banner and NINA shows one notification.
    /// </summary>
    public sealed class PendingUpdate : INotifyPropertyChanged, IDisposable {
        private const string DllName = "SkyShare.Planner.dll";

        private static readonly string NinaFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA");

        /// <summary>Where the installer stages the plugin (same path as installer/SkySharePlanner.iss).</summary>
        public static readonly string StagedDll = Path.Combine(NinaFolder, "PluginStaging", "3.0.0", "SkyShare Planner", DllName);

        private readonly FileSystemWatcher? watcher;
        private readonly Timer debounce;
        private string? notifiedFor;

        public PendingUpdate() {
            RunningVersion = Short(typeof(PendingUpdate).Assembly.GetName().Version?.ToString());
            debounce = new Timer(_ => Refresh(notify: true));
            Refresh(notify: false);
            try {
                // Watch NINA's folder (PluginStaging may not exist yet), only for our DLL's name.
                watcher = new FileSystemWatcher(NinaFolder, DllName) {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                };
                FileSystemEventHandler changed = (_, e) => {
                    if (e.FullPath.Contains("PluginStaging", StringComparison.OrdinalIgnoreCase))
                        debounce.Change(1500, Timeout.Infinite); // the installer writes the file in pieces
                };
                watcher.Created += changed;
                watcher.Changed += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (s, e) => changed(s, e);
                watcher.EnableRaisingEvents = true;
            } catch (Exception e) {
                Logger.Warning($"[SkyShare] can't watch for staged updates: {e.Message}");
            }
        }

        public string RunningVersion { get; }

        private string? stagedVersion;
        /// <summary>Version waiting in NINA's staging folder, or null when there is none.</summary>
        public string? StagedVersion { get => stagedVersion; private set { stagedVersion = value; Raise(); Raise(nameof(IsPending)); Raise(nameof(Message)); } }

        public bool IsPending => StagedVersion != null;

        public string Message => StagedVersion is null ? "" :
            StagedVersion == RunningVersion
                ? $"SkyShare Planner {StagedVersion} was reinstalled. Restart N.I.N.A. to load it."
                : $"SkyShare Planner {StagedVersion} is installed and loads the next time N.I.N.A. starts (running {RunningVersion} now).";

        /// <summary>Re-reads the staging folder. Also called when the options page is opened.</summary>
        public void Refresh(bool notify) {
            string? version = null;
            try {
                if (File.Exists(StagedDll))
                    version = Short(FileVersionInfo.GetVersionInfo(StagedDll).FileVersion) ?? "new version";
            } catch (Exception e) {
                Logger.Warning($"[SkyShare] could not read the staged update: {e.Message}");
                version = "new version";
            }
            StagedVersion = version;
            if (notify && version != null && notifiedFor != version) {
                notifiedFor = version;
                Logger.Info($"[SkyShare] update staged: {Message}");
                Notification.ShowInformation(Message, TimeSpan.FromSeconds(30));
            }
        }

        /// <summary>
        /// Gets the plugin folder out of the way so NINA's next start can deploy the staged files.
        /// The new NINA starts before this one has exited, so our DLL is still loaded and NINA
        /// couldn't overwrite it (it would log an error and drop the update). Like NINA's own
        /// plugin updater, move the current files to its deletion folder (allowed for a loaded
        /// DLL); NINA empties that folder on start. Returns false if a file couldn't be moved.
        /// </summary>
        public static bool ClearPluginFolderForStaged() {
            if (!File.Exists(StagedDll)) return true;
            var plugin = Path.GetDirectoryName(typeof(PendingUpdate).Assembly.Location)!;
            var deletion = Path.Combine(NinaFolder, "PluginDeletion", "3.0.0");
            var ok = true;
            try {
                Directory.CreateDirectory(deletion);
                foreach (var file in Directory.GetFiles(plugin, "*", SearchOption.AllDirectories)) {
                    try {
                        File.Move(file, Path.Combine(deletion, Path.GetRandomFileName()));
                    } catch (Exception e) {
                        ok = false;
                        Logger.Warning($"[SkyShare] could not move {file} out for the update: {e.Message}");
                    }
                }
            } catch (Exception e) {
                ok = false;
                Logger.Warning($"[SkyShare] could not prepare the plugin folder for the update: {e.Message}");
            }
            return ok;
        }

        /// <summary>"0.7.0.0" -> "0.7.0".</summary>
        private static string? Short(string? v) =>
            v is null ? null : Version.TryParse(v, out var p) ? $"{p.Major}.{p.Minor}.{Math.Max(0, p.Build)}" : v;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose() {
            watcher?.Dispose();
            debounce.Dispose();
        }
    }
}
