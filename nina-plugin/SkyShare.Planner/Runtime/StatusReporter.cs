using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// Reports progress back to the people who made the plan.
    ///
    /// 1. Appends JSON lines to "&lt;plan&gt;.status.jsonl" next to the plan file.
    ///    Dropbox syncs it to the compute server, where the Discord bot / SkyStack
    ///    can tail it. No network setup needed.
    /// 2. Optionally posts to a Discord webhook if the environment variable
    ///    SKYSHARE_DISCORD_WEBHOOK is set on the acquisition PC (kept out of the
    ///    sequence file and out of Dropbox on purpose).
    ///
    /// Never throws: reporting must not break imaging.
    /// </summary>
    public static class StatusReporter {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly object FileGate = new();

        public static void Report(string @event, string message, object? data = null) {
            Logger.Info($"[SkyShare] {@event}: {message}");
            WriteStatusLine(@event, message, data);
            _ = PostWebhookAsync($"**{@event}** - {message}");
        }

        private static void WriteStatusLine(string @event, string message, object? data) {
            try {
                var plan = PlanStore.PlanPath;
                if (plan is null) return;
                var path = Path.ChangeExtension(plan, null) + ".status.jsonl"; // x.plan.json -> x.plan.status.jsonl
                var line = JsonSerializer.Serialize(new Dictionary<string, object?> {
                    ["utc"] = DateTime.UtcNow.ToString("o"),
                    ["event"] = @event,
                    ["message"] = message,
                    ["data"] = data,
                });
                lock (FileGate) File.AppendAllText(path, line + "\n", Encoding.UTF8);
            } catch (Exception e) {
                Logger.Warning($"[SkyShare] could not write status file: {e.Message}");
            }
        }

        private static async Task PostWebhookAsync(string content) {
            try {
                var url = Environment.GetEnvironmentVariable("SKYSHARE_DISCORD_WEBHOOK");
                if (string.IsNullOrWhiteSpace(url)) return;
                var body = JsonSerializer.Serialize(new { content });
                using var resp = await Http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
            } catch (Exception e) {
                Logger.Warning($"[SkyShare] webhook failed: {e.Message}");
            }
        }
    }
}
