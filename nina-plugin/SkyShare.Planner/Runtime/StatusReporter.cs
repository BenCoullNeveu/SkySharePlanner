using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// 2. Posts to Discord through the webhook set in the Discord Alert plugin
    ///    (NINA Options → Plugins → Discord Alert). The environment variable
    ///    SKYSHARE_DISCORD_WEBHOOK on the acquisition PC overrides it. Neither is
    ///    in the sequence file or in Dropbox, on purpose.
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
        /// <param name="postToDiscord">false when the same news was already posted another way.</param>
        public static void Warn(string @event, string message, object? data = null, bool postToDiscord = true) {
            Logger.Warning($"[SkyShare] {@event}: {message}");
            Notify(() => Notification.ShowWarning($"SkyShare: {message}"));
            if (postToDiscord) Send("warning", @event, message, data);
            else WriteStatusLine("warning", @event, message, data);
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
        public static void ReportWithImage(string @event, string summary, string markdown, byte[]? png, string fileName, object? data = null) =>
            ReportWithImages(@event, summary, markdown, png is null ? Array.Empty<Attachment>() : new[] { new Attachment(png, fileName) }, data);

        /// <summary>An image attached to a Discord post. Type is the MIME type.</summary>
        public sealed record Attachment(byte[] Data, string Name, string Type = "image/png");

        /// <summary>Like ReportWithImage, with several images in one Discord message (Discord allows 10).</summary>
        public static void ReportWithImages(string @event, string summary, string markdown, IReadOnlyList<Attachment> files, object? data = null) {
            Logger.Info($"[SkyShare] {@event}: {summary}");
            WriteStatusLine("info", @event, summary, data);
            _ = PostWebhookAsync(markdown, files);
        }

        /// <summary>
        /// Where Discord posts go: SKYSHARE_DISCORD_WEBHOOK if set (an override), otherwise
        /// the webhook configured in the Discord Alert plugin (NINA Options → Plugins →
        /// Discord Alert). Read on every post, so a change in NINA applies straight away.
        /// </summary>
        public static string? WebhookUrl() {
            var url = Environment.GetEnvironmentVariable("SKYSHARE_DISCORD_WEBHOOK");
            return string.IsNullOrWhiteSpace(url) ? DiscordAlertWebhook() : url;
        }

        private static System.Reflection.PropertyInfo? discordAlertDefault;
        private static bool discordAlertSearched;

        // Discord Alert keeps its URL in an internal settings class; we look it up by name so
        // SkyShare has no hard dependency on that plugin (absent = no Discord posts, as before).
        private static string? DiscordAlertWebhook() {
            try {
                if (!discordAlertSearched) {
                    discordAlertSearched = true;
                    var type = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("NINA.DiscordAlert.Properties.Settings", throwOnError: false))
                        .FirstOrDefault(t => t != null);
                    discordAlertDefault = type?.GetProperty("Default",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (discordAlertDefault is null) {
                        discordAlertSearched = false; // plugin may load after us; try again next time
                        return null;
                    }
                    Logger.Info("[SkyShare] using the Discord Alert plugin's webhook for Discord posts");
                }
                var settings = discordAlertDefault?.GetValue(null);
                var url = settings?.GetType().GetProperty("DiscordWebhookURL")?.GetValue(settings) as string;
                return string.IsNullOrWhiteSpace(url) ? null : url;
            } catch (Exception e) {
                Logger.Warning($"[SkyShare] could not read the Discord Alert webhook: {e.Message}");
                return null;
            }
        }

        private static async Task PostWebhookAsync(string content, IReadOnlyList<Attachment>? files = null) {
            try {
                var url = WebhookUrl();
                if (string.IsNullOrWhiteSpace(url)) return;
                var body = JsonSerializer.Serialize(new { content });
                HttpContent request;
                if (files is null || files.Count == 0) {
                    request = new StringContent(body, Encoding.UTF8, "application/json");
                } else {
                    // Discord: JSON in "payload_json", attachments as files[n]
                    var form = new MultipartFormDataContent {
                        { new StringContent(body, Encoding.UTF8, "application/json"), "payload_json" },
                    };
                    for (int i = 0; i < Math.Min(files.Count, 10); i++) {
                        var image = new ByteArrayContent(files[i].Data);
                        image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(files[i].Type);
                        form.Add(image, $"files[{i}]", files[i].Name);
                    }
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
