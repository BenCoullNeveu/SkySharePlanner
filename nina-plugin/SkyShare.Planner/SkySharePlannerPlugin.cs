using System.ComponentModel.Composition;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

namespace SkyShare.Planner {

    /// <summary>
    /// Plugin entry point. NINA discovers this via MEF. All real work is in the
    /// sequencer items under Sequencer/; they are exported individually.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class SkySharePlannerPlugin : PluginBase {

        [ImportingConstructor]
        public SkySharePlannerPlugin() { }
    }
}
