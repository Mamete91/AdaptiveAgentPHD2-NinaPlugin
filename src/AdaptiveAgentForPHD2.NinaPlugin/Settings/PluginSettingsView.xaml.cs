using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AdaptiveAgentForPHD2.NinaPlugin.Settings
{
    public partial class PluginSettingsView : UserControl
    {
        public PluginSettingsView()
        {
            InitializeComponent();
        }

        private void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PluginSettings settings)
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Seleziona il file Avvia.bat dell'Adaptive Agent",
                Filter = "Script batch (*.bat)|*.bat|Tutti i file (*.*)|*.*",
                CheckFileExists = true,
            };

            try
            {
                if (!string.IsNullOrWhiteSpace(settings.AgentBatPath))
                {
                    var dir = Path.GetDirectoryName(settings.AgentBatPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        dialog.InitialDirectory = dir;
                    }
                    dialog.FileName = Path.GetFileName(settings.AgentBatPath);
                }
            }
            catch
            {
                // Percorso corrente non valido: apriamo il dialog senza posizione iniziale.
            }

            if (dialog.ShowDialog() == true)
            {
                settings.AgentBatPath = dialog.FileName;
            }
        }
    }
}
