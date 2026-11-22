using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// Looks for a newer SkyShare Planner release on GitHub, about a minute after NINA
    /// starts and then every 12 hours (when "Check for updates" is on), or on demand.
    /// Nothing is installed without a click: Install downloads the release's setup
    /// program and runs it silently. With NINA running it stages the update, and
    /// PendingUpdate then shows the restart banner.
    ///
    /// The only request is an anonymous GET to the GitHub API for this repository's
    /// latest release; the installer comes from that release's own assets.
    /// </summary>
    public sealed class UpdateChecker : INotifyPropertyChanged, IDisposable {
        private const string Repo = "BenCoullNeveu/SkySharePlanner";
        private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1), Every = TimeSpan.FromHours(12);

        private static readonly HttpClient Http = CreateClient();

        private readonly Func<bool> enabled;
        private readonly PendingUpdate pending;
        private readonly Version running;
        private readonly Timer timer;
        private string? notifiedFor;
        private string? installerUrl;

        public UpdateChecker(Func<bool> enabled, PendingUpdate pending) {
            this.enabled = enabled;
            this.pending = pending;
            running = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0);
            pending.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PendingUpdate.StagedVersion)) RaiseState(); };
            timer = new Timer(_ => { if (this.enabled()) _ = Check(automatic: true); }, null, FirstCheck, Every);
        }

        private static HttpClient CreateClient() {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var v = typeof(UpdateChecker).Assembly.GetName().Version;
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"SkySharePlanner/{v?.ToString(3) ?? "0"}"); // GitHub requires a User-Agent
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        // ---- state for the options page ---------------------------------------------

        private string? latestVersion;
        /// <summary>Newest release on GitHub ("0.9.0"), once checked.</summary>
        public string? LatestVersion { get => latestVersion; private set { latestVersion = value; RaiseState(); } }

        private string? releaseUrl;
        public string? ReleaseUrl { get => releaseUrl; private set { releaseUrl = value; Raise(); } }

        private string status = "";
        /// <summary>Result of the last check, for the line under the version.</summary>
        public string Status { get => status; private set { status = value; Raise(); } }

        private bool busy;
        public bool Busy { get => busy; private set { busy = value; Raise(); } }

        /// <summary>A newer release exists and it isn't already staged (then the restart banner shows instead).</summary>
        public bool IsAvailable => LatestVersion is { } v && Newer(v, running) &&
                                   !(pending.StagedVersion is { } s && Version.TryParse(s, out var sv) && !Newer(v, sv));

        public string Message => IsAvailable
            ? $"SkyShare Planner {LatestVersion} is available (running {running.ToString(3)}). Install it now; it loads the next time N.I.N.A. starts."
            : "";

        // ---- actions ----------------------------------------------------------------

        /// <summary>Asks GitHub for the latest release. Never throws.</summary>
        public async Task Check(bool automatic) {
            if (Busy) return;
            Busy = true;
            try {
                using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                if (!resp.IsSuccessStatusCode) {
                    Status = $"Update check failed: GitHub answered {(int)resp.StatusCode}";
                    return;
                }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                var tag = root.GetProperty("tag_name").GetString() ?? "";
                var version = tag.TrimStart('v', 'V');
                if (!Version.TryParse(version, out _)) {
                    Status = $"Update check: unexpected release tag '{tag}'";
                    return;
                }
                ReleaseUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
                installerUrl = root.GetProperty("assets").EnumerateArray()
                    .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                    .Where(a => a.Name.StartsWith("SkySharePlanner-Setup-", StringComparison.OrdinalIgnoreCase)
                             && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                             && a.Url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Url).FirstOrDefault();
                LatestVersion = version;
                Status = IsAvailable ? $"Version {version} is available"
                       : $"Up to date (latest release {version}), checked {DateTime.Now:HH:mm}";
                if (IsAvailable && automatic && notifiedFor != version) {
                    notifiedFor = version;
                    Logger.Info($"[SkyShare] {Message}");
                    Notification.ShowInformation($"{Message} See Plugins > SkyShare Planner.", TimeSpan.FromSeconds(30));
                }
            } catch (Exception e) {
                Status = $"Update check failed: {e.Message}";
                Logger.Warning($"[SkyShare] update check failed: {e.Message}");
            } finally {
                Busy = false;
            }
        }

        /// <summary>Downloads the release's installer and runs it silently (it stages the update while NINA runs).</summary>
        public async Task Install() {
            if (Busy) return;
            if (installerUrl is null || LatestVersion is null) {
                Notification.ShowWarning("No installer found on the latest release. Download it from the release page instead.");
                return;
            }
            Busy = true;
            var file = Path.Combine(Path.GetTempPath(), $"SkySharePlanner-Setup-{LatestVersion}.exe");
            try {
                Status = $"Downloading {LatestVersion}…";
                using (var resp = await Http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead)) {
                    resp.EnsureSuccessStatusCode();
                    await using var output = File.Create(file);
                    await resp.Content.CopyToAsync(output);
                }
                Status = $"Installing {LatestVersion}…";
                using var proc = Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true })
                                 ?? throw new Exception("the installer did not start");
                await proc.WaitForExitAsync();
                if (proc.ExitCode != 0) throw new Exception($"the installer exited with code {proc.ExitCode}");
                pending.Refresh(notify: false);
                Status = pending.IsPending ? $"{LatestVersion} installed: restart N.I.N.A. to load it" : $"{LatestVersion} installed";
                Logger.Info($"[SkyShare] installed {LatestVersion} from GitHub");
            } catch (Exception e) {
                Status = $"Install failed: {e.Message}";
                Logger.Error($"[SkyShare] update install failed: {e}");
                Notification.ShowError($"Could not install SkyShare Planner {LatestVersion}: {e.Message}");
            } finally {
                Busy = false;
                try { File.Delete(file); } catch { /* still in use or already gone */ }
            }
        }

        public void OpenReleasePage() {
            if (ReleaseUrl is { } url && url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private static bool Newer(string a, Version b) => Version.TryParse(a, out var va) && Newer(va, b);
        private static bool Newer(Version a, Version b) =>
            new Version(a.Major, a.Minor, Math.Max(0, a.Build)) > new Version(b.Major, b.Minor, Math.Max(0, b.Build));

        private void RaiseState() { Raise(nameof(LatestVersion)); Raise(nameof(IsAvailable)); Raise(nameof(Message)); }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose() => timer.Dispose();
    }
}
