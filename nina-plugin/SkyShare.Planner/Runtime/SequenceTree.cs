using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using NINA.Core.Locale;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;

namespace SkyShare.Planner.Runtime {

    /// <summary>Small helpers for walking and editing the NINA sequence tree.</summary>
    public static class SequenceTree {

        /// <summary>Marker on every block the plugin generates, so re-runs can remove them.</summary>
        public const string GeneratedPrefix = "▶ ";

        public static IEnumerable<T> Descendants<T>(ISequenceContainer root) where T : class {
            foreach (var item in root.Items) {
                if (item is T t) yield return t;
                if (item is ISequenceContainer c)
                    foreach (var d in Descendants<T>(c)) yield return d;
            }
        }

        /// <summary>
        /// Conditions on a container. ISequenceContainer doesn't expose them; the concrete
        /// containers implement IConditionable. Returns empty for containers without conditions.
        /// </summary>
        public static IEnumerable<ISequenceCondition> Conditions(ISequenceContainer container) =>
            container is IConditionable c ? c.Conditions : Enumerable.Empty<ISequenceCondition>();

        /// <summary>The top of the sequence an item belongs to, or null if it isn't attached.</summary>
        public static ISequenceContainer? Root(ISequenceItem item) {
            var p = item.Parent;
            while (p?.Parent != null) p = p.Parent;
            return p;
        }

        /// <summary>
        /// Gives generated items the name, icon, category and description NINA shows in
        /// the sequencer. NINA normally stamps these from each class's [ExportMetadata]
        /// when it creates an instruction; items the plugin constructs directly (and the
        /// children their constructors create) skip that step and show up blank.
        /// Names the plugin set on purpose are kept. Call on the UI thread.
        /// </summary>
        public static void ApplyNinaMetadata(ISequenceEntity entity) {
            var meta = entity.GetType()
                .GetCustomAttributes(typeof(ExportMetadataAttribute), inherit: false)
                .Cast<ExportMetadataAttribute>()
                .Where(a => a.Value is string)
                .GroupBy(a => a.Name)
                .ToDictionary(g => g.Key, g => (string)g.First().Value!);

            if (string.IsNullOrEmpty(entity.Name) && meta.TryGetValue("Name", out var name)) entity.Name = Label(name);
            if (string.IsNullOrEmpty(entity.Description) && meta.TryGetValue("Description", out var desc)) entity.Description = Label(desc);
            if (string.IsNullOrEmpty(entity.Category) && meta.TryGetValue("Category", out var cat)) entity.Category = Label(cat);
            if (entity.Icon is null && meta.TryGetValue("Icon", out var icon))
                entity.Icon = Application.Current?.TryFindResource(icon) as GeometryGroup;

            if (entity is ISequenceContainer container)
                foreach (var item in container.Items) ApplyNinaMetadata(item);
            if (entity is IConditionable conditionable)
                foreach (var condition in conditionable.Conditions) ApplyNinaMetadata(condition);
            if (entity is ITriggerable triggerable)
                foreach (var trigger in triggerable.Triggers) ApplyNinaMetadata(trigger);
        }

        // NINA's own metadata holds translation keys ("Lbl_..."); plugin metadata is plain text.
        private static string Label(string keyOrText) =>
            keyOrText.StartsWith("Lbl_", StringComparison.Ordinal) ? Loc.Instance[keyOrText] : keyOrText;

        public static void RemoveGenerated(ISequenceContainer owner) {
            foreach (var old in owner.Items.Where(i => i.Name?.StartsWith(GeneratedPrefix) == true).ToList())
                owner.Remove(old);
        }

        /// <summary>
        /// The sequencer's collections are bound to the UI. Always mutate them on the
        /// WPF dispatcher, otherwise NINA throws cross-thread exceptions.
        /// </summary>
        public static Task OnUiThread(Action action) {
            var d = Application.Current?.Dispatcher;
            if (d is null || d.CheckAccess()) { action(); return Task.CompletedTask; }
            return d.InvokeAsync(action).Task;
        }
    }
}
