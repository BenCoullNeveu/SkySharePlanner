using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using NINA.Core.Enum;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;

namespace SkyShare.Planner.Runtime {

    /// <summary>Small helpers for walking and editing the NINA sequence tree.</summary>
    public static class SequenceTree {

        /// <summary>Marker on every block the plugin generates, so re-runs can remove them.</summary>
        public const string GeneratedPrefix = "▶ ";
        public const string PrototypeName = "PROTOTYPE";

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

        /// <summary>
        /// Inserts an item at a position. ISequenceContainer only has Add(); SequenceContainer
        /// has InsertIntoSequenceBlocks, which also wires up the item's Parent.
        /// </summary>
        public static void Insert(ISequenceContainer container, int index, ISequenceItem item) {
            if (container is SequenceContainer sc) sc.InsertIntoSequenceBlocks(index, item);
            else container.Add(item);
        }

        public static T? FirstDescendant<T>(ISequenceContainer root) where T : class =>
            Descendants<T>(root).FirstOrDefault();

        public static ISequenceContainer? FindContainer(ISequenceContainer root, string name) =>
            Descendants<ISequenceContainer>(root).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The PROTOTYPE child the user designed in the template. It must be Disabled
        /// (right-click > Disable) so it never runs itself; we enforce that here too.
        /// </summary>
        public static T RequirePrototype<T>(ISequenceContainer owner) where T : class, ISequenceItem {
            var proto = owner.Items.OfType<T>()
                .FirstOrDefault(i => string.Equals(i.Name, PrototypeName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"'{owner.Name}' needs a child {typeof(T).Name} named '{PrototypeName}'.");
            SetStatus(proto, SequenceEntityStatus.DISABLED); // VERIFY: NINA 3 "Disable" = this status
            return proto;
        }

        /// <summary>Deep-clones a prototype into a fresh, runnable copy.</summary>
        public static T CloneFresh<T>(T prototype, string name) where T : class, ISequenceItem {
            var copy = (T)prototype.Clone();
            copy.Name = GeneratedPrefix + name;
            SetStatus(copy, SequenceEntityStatus.CREATED);
            copy.ResetProgress();
            return copy;
        }

        // ISequenceItem only exposes a getter; the concrete base class has the setter.
        public static void SetStatus(ISequenceItem item, SequenceEntityStatus status) {
            if (item is SequenceItem si) si.Status = status;
        }

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
