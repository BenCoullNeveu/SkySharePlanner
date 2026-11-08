using System.ComponentModel.Composition;
using System.Windows;

namespace SkyShare.Planner.Resources {

    /// <summary>NINA merges every exported ResourceDictionary into the app resources.</summary>
    [Export(typeof(ResourceDictionary))]
    public partial class DataTemplates : ResourceDictionary {
        public DataTemplates() {
            InitializeComponent();
        }
    }
}
