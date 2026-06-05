using System.ComponentModel.Composition;
using System.Windows;

namespace AdaptiveAgentForPHD2.NinaPlugin.Resources
{
    /// <summary>
    /// Il MEF export su ResourceDictionary e' il meccanismo con cui NINA scopre
    /// i DataTemplate del plugin e li aggiunge alle risorse dell'applicazione.
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class DataTemplates : ResourceDictionary
    {
        public DataTemplates()
        {
            InitializeComponent();
        }
    }
}
