#nullable enable
using NINA.Core.Utility;
using System;
using System.IO;
using System.Text.Json;

namespace AdaptiveAgentForPHD2.NinaPlugin.Settings
{
    /// <summary>
    /// Modello di configurazione del plugin con persistenza JSON.
    /// NINA 3.2 SDK non espone un IPluginOptionsAccessor (verificato in pre-flight),
    /// quindi serializziamo manualmente in %LOCALAPPDATA%\NINA\Plugins\&lt;plugin&gt;\settings.json.
    /// Deriva da BaseINPC (NINA) per il binding two-way con la pagina opzioni.
    /// </summary>
    public sealed class PluginSettings : BaseINPC
    {
        public const string DefaultDashboardUrl = "http://localhost:8080";
        public const int DefaultIntervalSeconds = 15;
        public const int MinIntervalSeconds = 5;
        public const int MaxIntervalSeconds = 120;
        public const int DefaultStarLostConsolidationSeconds = 300;
        public const int MinStarLostConsolidationSeconds = 30;
        public const int MaxStarLostConsolidationSeconds = 1800;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "Plugins", "AdaptiveAgentForPHD2.NinaPlugin", "settings.json");

        private string _agentBatPath = "";
        private int _healthCheckIntervalSeconds = DefaultIntervalSeconds;
        private string _dashboardUrl = DefaultDashboardUrl;
        private int _starLostConsolidationSeconds = DefaultStarLostConsolidationSeconds;
        private bool _suppressSave;

        /// <summary>Sollevato quando l'intervallo di polling cambia, così il poller riarma il timer.</summary>
        public event Action? IntervalChanged;

        public string AgentBatPath
        {
            get => _agentBatPath;
            set
            {
                var v = value ?? "";
                if (_agentBatPath == v) { return; }
                _agentBatPath = v;
                RaisePropertyChanged();
                Save();
            }
        }

        public int HealthCheckIntervalSeconds
        {
            get => _healthCheckIntervalSeconds;
            set
            {
                var clamped = Math.Clamp(value, MinIntervalSeconds, MaxIntervalSeconds);
                if (_healthCheckIntervalSeconds == clamped) { return; }
                _healthCheckIntervalSeconds = clamped;
                RaisePropertyChanged();
                Save();
                IntervalChanged?.Invoke();
            }
        }

        public string DashboardUrl
        {
            get => _dashboardUrl;
            set
            {
                var v = string.IsNullOrWhiteSpace(value) ? DefaultDashboardUrl : value.Trim();
                if (_dashboardUrl == v) { return; }
                _dashboardUrl = v;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>
        /// Tempo (secondi) per cui STAR_LOST deve persistere prima che il Safety Monitor v1.2
        /// dichiari unsafe. Default 5 minuti. Range 30-1800. Usato solo dal Safety Monitor;
        /// l'intervallo di polling resta HealthCheckIntervalSeconds.
        /// </summary>
        public int StarLostConsolidationSeconds
        {
            get => _starLostConsolidationSeconds;
            set
            {
                var clamped = Math.Clamp(value, MinStarLostConsolidationSeconds, MaxStarLostConsolidationSeconds);
                if (_starLostConsolidationSeconds == clamped) { return; }
                _starLostConsolidationSeconds = clamped;
                RaisePropertyChanged();
                Save();
            }
        }

        public static PluginSettings Load()
        {
            var settings = new PluginSettings();
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(SettingsPath));
                    if (dto != null)
                    {
                        // Assegniamo via setter (per il clamp/normalizzazione) ma senza ri-salvare.
                        settings._suppressSave = true;
                        settings.AgentBatPath = dto.AgentBatPath ?? "";
                        settings.HealthCheckIntervalSeconds =
                            dto.HealthCheckIntervalSeconds == 0 ? DefaultIntervalSeconds : dto.HealthCheckIntervalSeconds;
                        settings.DashboardUrl = dto.DashboardUrl ?? DefaultDashboardUrl;
                        // Utenti che aggiornano da v1.1 non hanno la chiave => 0 => applica il default.
                        settings.StarLostConsolidationSeconds =
                            dto.StarLostConsolidationSeconds == 0 ? DefaultStarLostConsolidationSeconds : dto.StarLostConsolidationSeconds;
                        settings._suppressSave = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Lettura settings plugin fallita: {ex.Message}");
            }
            return settings;
        }

        private void Save()
        {
            if (_suppressSave) { return; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                var dto = new SettingsDto
                {
                    AgentBatPath = _agentBatPath,
                    HealthCheckIntervalSeconds = _healthCheckIntervalSeconds,
                    DashboardUrl = _dashboardUrl,
                    StarLostConsolidationSeconds = _starLostConsolidationSeconds,
                };
                File.WriteAllText(SettingsPath,
                    JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Logger.Error($"Salvataggio settings plugin fallito: {ex.Message}");
            }
        }

        private sealed class SettingsDto
        {
            public string? AgentBatPath { get; set; }
            public int HealthCheckIntervalSeconds { get; set; }
            public string? DashboardUrl { get; set; }
            public int StarLostConsolidationSeconds { get; set; }
        }
    }
}
