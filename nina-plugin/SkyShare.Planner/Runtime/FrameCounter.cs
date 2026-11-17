using System;
using NINA.Core.Utility;
using NINA.WPF.Base.Interfaces.Mediator;

namespace SkyShare.Planner.Runtime {

    /// <summary>
    /// Counts saved LIGHT frames per plan target from NINA's ImageSaved event.
    /// Used for the max_frames limit and for the session manifest's frame counts
    /// (Smart Exposure loop counters reset every cycle, so they can't be used for that).
    /// Frames are matched to targets by the target name NINA writes into each image.
    /// </summary>
    public static class FrameCounter {
        private static readonly object Gate = new();
        private static IImageSaveMediator? attached;

        /// <summary>Safe to call many times; subscribes once.</summary>
        public static void Attach(IImageSaveMediator mediator) {
            lock (Gate) {
                if (ReferenceEquals(attached, mediator)) return;
                if (attached != null) attached.ImageSaved -= OnImageSaved;
                attached = mediator;
                attached.ImageSaved += OnImageSaved;
            }
        }

        private static void OnImageSaved(object? sender, ImageSavedEventArgs e) {
            try {
                if (!string.Equals(e.MetaData?.Image?.ImageType, "LIGHT", StringComparison.OrdinalIgnoreCase)) return;
                var target = e.MetaData?.Target?.Name;
                if (string.IsNullOrWhiteSpace(target)) return;
                var filter = !string.IsNullOrWhiteSpace(e.Filter) ? e.Filter : e.MetaData?.FilterWheel?.Filter ?? "?";
                PlanStore.RecordLight(target, filter);
            } catch (Exception ex) {
                Logger.Warning($"[SkyShare] frame counter: {ex.Message}"); // never break image saving
            }
        }
    }
}
