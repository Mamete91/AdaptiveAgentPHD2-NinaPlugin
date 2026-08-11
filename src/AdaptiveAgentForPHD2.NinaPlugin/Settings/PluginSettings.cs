#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Localization;
using AdaptiveAgentForPHD2.NinaPlugin.Safety;
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
    /// Implementa ISafetySettings: la vista read-only consumata dal SafetyDecisionEngine.
    /// </summary>
    public sealed class PluginSettings : BaseINPC, ISafetySettings
    {
        // §69 — 127.0.0.1 e NON "localhost": su Windows `localhost` risolve PRIMA a ::1
        // (IPv6), dove l'Agente non ascolta (uvicorn bind 0.0.0.0 = solo IPv4), e ogni
        // chiamata paga il fallback. Misurato sui log NINA del 30/7: POST /shutdown
        // 2031 ms -> 5 ms, chiusura visibile 3043 ms -> 402 ms. Il bind dell'Agente NON
        // cambia: la dashboard resta raggiungibile da tutta la LAN via IP della macchina.
        public const string DefaultDashboardUrl = "http://127.0.0.1:8080";
        public const string LegacyLocalhostUrl = "http://localhost:8080";
        public const int DefaultIntervalSeconds = 15;
        public const int MinIntervalSeconds = 5;
        public const int MaxIntervalSeconds = 120;
        public const int DefaultStarLostConsolidationSeconds = 300;
        public const int MinStarLostConsolidationSeconds = 30;
        public const int MaxStarLostConsolidationSeconds = 1800;
        public const bool DefaultForwardTelemetryToAgent = true;   // §42: born-operative
        // §49 N6 — safety su nubi (trasparenza NINA). Isteresi asimmetrica: lento verso
        // UNSAFE (N poll di CLOUD), più rapido verso SAFE (M poll di CLEAR/HAZE).
        public const bool DefaultCloudSafetyEnabled = true;
        public const int DefaultCloudUnsafePolls = 8;   // poll consecutivi CLOUD -> UNSAFE
        public const int MinCloudUnsafePolls = 2;
        public const int MaxCloudUnsafePolls = 120;
        public const int DefaultClearSafePolls = 4;     // poll consecutivi CLEAR/HAZE -> SAFE
        public const int MinClearSafePolls = 1;
        public const int MaxClearSafePolls = 60;
        // Fix N6 (§1/§2/§5) — nuova logica di sicurezza, nata operativa con kill-switch.
        // Soglie indice allineate a N1 (config agente: cloud_below=0.5, clear_above=0.8).
        public const bool DefaultUseIndexCloudLogic = true;
        public const double DefaultCloudIndexAccumulateBelow = 0.5;
        public const double DefaultCloudIndexDrainAbove = 0.8;
        public const bool DefaultStaleUnsafeEnabled = true;
        public const int DefaultStaleUnsafePolls = 8;   // ~2 min al default 15s
        public const int MinStaleUnsafePolls = 2;
        public const int MaxStaleUnsafePolls = 120;
        public const bool DefaultAgentLostUnsafeEnabled = true;
        public const int DefaultAgentLostUnsafePolls = 4;  // ~1 min: perdere l'osservazione e' peggio
        public const int MinAgentLostUnsafePolls = 1;
        public const int MaxAgentLostUnsafePolls = 60;
        // §58 — auto-gestione del ciclo di vita dell'Agente dal plugin.
        // §60 — default ribaltati a ON ("installa e funziona"): col ciclo di vita ormai
        // maturo (§56 orphan recovery, §58 graceful shutdown, §59 watchdog di
        // auto-terminazione) l'opt-in non proteggeva piu' nulla e faceva sembrare il
        // plugin inerte alla prima installazione. Restano i kill-switch in opzioni;
        // upgrade-safe: un false salvato esplicitamente resta false (DTO nullable).
        // AutoLaunch e' comunque inerte finche' il path del launcher non e' configurato.
        public const bool DefaultAutoLaunchEnabled = true;
        public const bool DefaultManageExternalAgent = true;
        // §68 — osservabilita' del canale di guida. Nato dal guasto del 26/7 (camera di
        // guida patologica: PHD2 tace e `guiding_state` resta congelato). Conservativo:
        // 90 s di silenzio + 3 poll di consolidamento (~45 s) prima di UNSAFE.
        public const bool DefaultGuideUnobservableEnabled = true;
        public const int DefaultGuideSilenceSeconds = 90;
        public const int MinGuideSilenceSeconds = 20;
        public const int MaxGuideSilenceSeconds = 600;
        public const int DefaultGuideUnobservablePolls = 3;
        public const int MinGuideUnobservablePolls = 1;
        public const int MaxGuideUnobservablePolls = 60;
        // §76 — il canale guida (3 s) puo' ACCUMULARE verso unsafe quando vede il
        // cielo peggiorare mentre N1 (300 s) e' ancora fermo sull'ultima posa buona.
        // Mai il contrario. Born-operative: chiude un buco misurato di 8 minuti.
        public const bool DefaultSkyDegradingAccumulateEnabled = true;
        // §79 — default = DefaultCloudUnsafePolls: alla separazione il comportamento
        // resta IDENTICO a prima. Cambia la tarabilita', non i tempi.
        public const int DefaultSkyDegradingUnsafePolls = 8;
        public const int MinSkyDegradingUnsafePolls = 2;
        public const int MaxSkyDegradingUnsafePolls = 120;
        // §71 — gate della Recovery Probe sul "canale pronto" (consenso §68). Deferrer
        // puro: allunga la cadenza S1 fino al tetto (RecoveryProbeGate.AutoCeilingSeconds),
        // mai un veto. Born-operative: a fail-open totale (agente vecchio => inerte).
        public const bool DefaultProbeChannelGateEnabled = true;
        // §72 — protezione meridiano. Decisione di Alessandro (2026-08-04): born-operative,
        // NON opt-in — nel perimetro attuale l'unico consumatore di IsSafe e' NINA e il
        // rischio reale e' il DEADLOCK (flip mancato => tracking fermo => tutti gli occhi
        // del monitor spenti => mai piu' SAFE). Da RIVALUTARE se compariranno consumatori
        // ulteriori di IsSafe (tetto motorizzato, cupola, osservatorio remoto).
        public const bool DefaultMeridianProtectionEnabled = true;
        public const int DefaultMeridianLeadMinutes = 4;
        public const int MinMeridianLeadMinutes = 1;
        public const int MaxMeridianLeadMinutes = 10;
        // §60 — lingua del SOLO plugin: "" = Follow N.I.N.A., "en", "it".
        public const string DefaultPluginLanguage = "";

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "Plugins", "AdaptiveAgentForPHD2.NinaPlugin", "settings.json");

        private string _agentBatPath = "";
        private int _healthCheckIntervalSeconds = DefaultIntervalSeconds;
        private string _dashboardUrl = DefaultDashboardUrl;
        private int _starLostConsolidationSeconds = DefaultStarLostConsolidationSeconds;
        private bool _forwardTelemetryToAgent = DefaultForwardTelemetryToAgent;
        private bool _cloudSafetyEnabled = DefaultCloudSafetyEnabled;
        private int _cloudUnsafePolls = DefaultCloudUnsafePolls;
        private int _clearSafePolls = DefaultClearSafePolls;
        private bool _useIndexCloudLogic = DefaultUseIndexCloudLogic;
        private double _cloudIndexAccumulateBelow = DefaultCloudIndexAccumulateBelow;
        private double _cloudIndexDrainAbove = DefaultCloudIndexDrainAbove;
        private bool _staleUnsafeEnabled = DefaultStaleUnsafeEnabled;
        private int _staleUnsafePolls = DefaultStaleUnsafePolls;
        private bool _agentLostUnsafeEnabled = DefaultAgentLostUnsafeEnabled;
        private int _agentLostUnsafePolls = DefaultAgentLostUnsafePolls;
        private bool _autoLaunchEnabled = DefaultAutoLaunchEnabled;
        private bool _manageExternalAgent = DefaultManageExternalAgent;
        private bool _guideUnobservableEnabled = DefaultGuideUnobservableEnabled;
        private int _guideSilenceSeconds = DefaultGuideSilenceSeconds;
        private int _guideUnobservablePolls = DefaultGuideUnobservablePolls;
        private bool _skyDegradingAccumulateEnabled = DefaultSkyDegradingAccumulateEnabled;
        private int _skyDegradingUnsafePolls = DefaultSkyDegradingUnsafePolls;
        private bool _probeChannelGateEnabled = DefaultProbeChannelGateEnabled;
        private bool _meridianProtectionEnabled = DefaultMeridianProtectionEnabled;
        private int _meridianLeadMinutes = DefaultMeridianLeadMinutes;
        private string _pluginLanguage = DefaultPluginLanguage;
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

        /// <summary>
        /// §42 — inoltro delle metriche per-posa di NINA all'Agente (POST /nina/telemetry).
        /// Default true (born-operative). false = kill-switch lato plugin: il forwarder resta
        /// iscritto a ImageSaved ma NON POSTa. Opzionale/graceful: non influisce su NINA.
        /// </summary>
        public bool ForwardTelemetryToAgent
        {
            get => _forwardTelemetryToAgent;
            set
            {
                if (_forwardTelemetryToAgent == value) { return; }
                _forwardTelemetryToAgent = value;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>
        /// §49 N6 — safety su nubi: quando true, il Safety Monitor dichiara UNSAFE anche se
        /// la trasparenza NINA resta CLOUD per CloudUnsafePolls poll (accanto a STAR_LOST).
        /// Default true. false = kill-switch (solo STAR_LOST, comportamento pre-N6).
        /// FAIL-SAFE: senza telemetria fresca la condizione nubi è comunque neutra.
        /// </summary>
        public bool CloudSafetyEnabled
        {
            get => _cloudSafetyEnabled;
            set
            {
                if (_cloudSafetyEnabled == value) { return; }
                _cloudSafetyEnabled = value;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>Poll consecutivi con trasparenza CLOUD prima di UNSAFE (isteresi lenta).</summary>
        public int CloudUnsafePolls
        {
            get => _cloudUnsafePolls;
            set
            {
                var clamped = Math.Clamp(value, MinCloudUnsafePolls, MaxCloudUnsafePolls);
                if (_cloudUnsafePolls == clamped) { return; }
                _cloudUnsafePolls = clamped;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>§79 — poll di degrado dal CANALE GUIDA prima di UNSAFE. Soglia
        /// PROPRIA: alzare CloudUnsafePolls (persistenza del cielo misurata dalla camera)
        /// non deve rallentare questa, che nasce proprio per anticipare la camera.</summary>
        public int SkyDegradingUnsafePolls
        {
            get => _skyDegradingUnsafePolls;
            set
            {
                var clamped = Math.Clamp(value, MinSkyDegradingUnsafePolls, MaxSkyDegradingUnsafePolls);
                if (_skyDegradingUnsafePolls == clamped) { return; }
                _skyDegradingUnsafePolls = clamped;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>Poll consecutivi con trasparenza CLEAR/HAZE prima di tornare SAFE (recovery più rapido).</summary>
        public int ClearSafePolls
        {
            get => _clearSafePolls;
            set
            {
                var clamped = Math.Clamp(value, MinClearSafePolls, MaxClearSafePolls);
                if (_clearSafePolls == clamped) { return; }
                _clearSafePolls = clamped;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>
        /// Fix N6 §2 — true: persistenza CLOUD calcolata sull'INDICE di trasparenza con
        /// accumulatore leaky (HAZE non azzera). false = kill-switch: logica legacy a poll
        /// consecutivi sullo stato discreto (comportamento pre-fix, Bug A incluso).
        /// </summary>
        public bool UseIndexCloudLogic
        {
            get => _useIndexCloudLogic;
            set
            {
                if (_useIndexCloudLogic == value) { return; }
                _useIndexCloudLogic = value;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>Indice sotto cui il degrado accumula (+1/poll). Allineato a cloud_below di N1.</summary>
        public double CloudIndexAccumulateBelow
        {
            get => _cloudIndexAccumulateBelow;
            set
            {
                var clamped = Math.Clamp(value, 0.05, 0.9);
                if (Math.Abs(_cloudIndexAccumulateBelow - clamped) < 1e-9) { return; }
                _cloudIndexAccumulateBelow = clamped;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>Indice sopra cui il degrado drena. Allineato a clear_above di N1.</summary>
        public double CloudIndexDrainAbove
        {
            get => _cloudIndexDrainAbove;
            set
            {
                var clamped = Math.Clamp(value, 0.1, 1.0);
                if (Math.Abs(_cloudIndexDrainAbove - clamped) < 1e-9) { return; }
                _cloudIndexDrainAbove = clamped;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>
        /// Fix N6 §1 — telemetria stantia (oltre la finestra adattiva §43) durante sessione
        /// attiva con ultimo contesto degradato => UNSAFE dopo StaleUnsafePolls. false =
        /// kill-switch (comportamento pre-fix: stantio ignora le nubi).
        /// </summary>
        public bool StaleUnsafeEnabled
        {
            get => _staleUnsafeEnabled;
            set
            {
                if (_staleUnsafeEnabled == value) { return; }
                _staleUnsafeEnabled = value;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>Poll con telemetria stantia (a contesto degradato) prima di UNSAFE.</summary>
        public int StaleUnsafePolls
        {
            get => _staleUnsafePolls;
            set
            {
                var clamped = Math.Clamp(value, MinStaleUnsafePolls, MaxStaleUnsafePolls);
                if (_staleUnsafePolls == clamped) { return; }
                _staleUnsafePolls = clamped;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>
        /// Fix N6 §5 — agente irraggiungibile durante sessione attiva => UNSAFE dopo
        /// AgentLostUnsafePolls (mai disconnect-to-SAFE). false = solo log, nessuna escalation.
        /// </summary>
        public bool AgentLostUnsafeEnabled
        {
            get => _agentLostUnsafeEnabled;
            set
            {
                if (_agentLostUnsafeEnabled == value) { return; }
                _agentLostUnsafeEnabled = value;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>Poll con agente irraggiungibile (a sessione attiva) prima di UNSAFE.</summary>
        public int AgentLostUnsafePolls
        {
            get => _agentLostUnsafePolls;
            set
            {
                var clamped = Math.Clamp(value, MinAgentLostUnsafePolls, MaxAgentLostUnsafePolls);
                if (_agentLostUnsafePolls == clamped) { return; }
                _agentLostUnsafePolls = clamped;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>
        /// §58 — all'avvio del plugin: se true, path configurato e Agente non gia'
        /// raggiungibile, il plugin lo avvia da solo. Default true (§60); inerte
        /// finche' il percorso del launcher non e' configurato.
        /// </summary>
        public bool AutoLaunchEnabled
        {
            get => _autoLaunchEnabled;
            set
            {
                if (_autoLaunchEnabled == value) { return; }
                _autoLaunchEnabled = value;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>
        /// §58 — politica B: alla chiusura di NINA spegni (graceful) anche un Agente
        /// NON avviato dal plugin. Default true (§60): l'Agente esiste per servire
        /// NINA — chi lo usa standalone e vuole che sopravviva la disattiva. NB: su un
        /// agente esterno non c'e' handle di processo per il fallback kill — se e'
        /// piantato resta solo il §56 al riavvio.
        /// </summary>
        public bool ManageExternalAgent
        {
            get => _manageExternalAgent;
            set
            {
                if (_manageExternalAgent == value) { return; }
                _manageExternalAgent = value;
                RaisePropertyChanged();
                Save();
            }
        }


        /// <summary>§76 — consenti al canale guida di accumulare verso unsafe quando
        /// il cielo peggiora prima che N1 possa accorgersene. Mai verso safe.</summary>
        public bool SkyDegradingAccumulateEnabled
        {
            get => _skyDegradingAccumulateEnabled;
            set
            {
                if (_skyDegradingAccumulateEnabled == value) { return; }
                _skyDegradingAccumulateEnabled = value;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>§71 — gate della sonda sul "canale pronto": differisce la S1 (mai
        /// oltre il tetto di 15 min) finche' il canale guida non e' stabilmente tornato.</summary>
        public bool ProbeChannelGateEnabled
        {
            get => _probeChannelGateEnabled;
            set
            {
                if (_probeChannelGateEnabled == value) { return; }
                _probeChannelGateEnabled = value;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>§72 — finestra di protezione al meridiano (vedi MeridianProtectionEngine).</summary>
        public bool MeridianProtectionEnabled
        {
            get => _meridianProtectionEnabled;
            set
            {
                if (_meridianProtectionEnabled == value) { return; }
                _meridianProtectionEnabled = value;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>§72 — anticipo (minuti) con cui la finestra si apre rispetto alla
        /// deadline del flip (meridiano + MaxMinutesAfterMeridian del profilo NINA).</summary>
        public int MeridianLeadMinutes
        {
            get => _meridianLeadMinutes;
            set
            {
                var clamped = Math.Clamp(value, MinMeridianLeadMinutes, MaxMeridianLeadMinutes);
                if (_meridianLeadMinutes == clamped) { return; }
                _meridianLeadMinutes = clamped;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>
        /// §68 — kill-switch del latch GUIDE_UNOBSERVABLE. false = comportamento pre-§68
        /// (il canale di guida non ha alcun watchdog di osservabilita').
        /// </summary>
        public bool GuideUnobservableEnabled
        {
            get => _guideUnobservableEnabled;
            set
            {
                if (_guideUnobservableEnabled == value) { return; }
                _guideUnobservableEnabled = value;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>Secondi di SILENZIO del canale oltre i quali il canale e' sospetto.
        /// Con corroborazione (Alert severo / raffica di ErrorCode) la soglia si dimezza.</summary>
        public int GuideSilenceSeconds
        {
            get => _guideSilenceSeconds;
            set
            {
                var clamped = Math.Clamp(value, MinGuideSilenceSeconds, MaxGuideSilenceSeconds);
                if (_guideSilenceSeconds == clamped) { return; }
                _guideSilenceSeconds = clamped;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>Poll di consolidamento (accumulatore leaky) prima di UNSAFE.</summary>
        public int GuideUnobservablePolls
        {
            get => _guideUnobservablePolls;
            set
            {
                var clamped = Math.Clamp(value, MinGuideUnobservablePolls, MaxGuideUnobservablePolls);
                if (_guideUnobservablePolls == clamped) { return; }
                _guideUnobservablePolls = clamped;
                RaisePropertyChanged();
                Save();
            }
        }

        /// <summary>
        /// §60 — lingua dell'interfaccia del plugin ("" = segue N.I.N.A.). Aggiorna la
        /// UI live via Loc (indexer bindabile); non tocca mai la cultura di N.I.N.A.
        /// </summary>
        public string PluginLanguage
        {
            get => _pluginLanguage;
            set
            {
                var v = (value ?? "").Trim().ToLowerInvariant();
                if (v != "" && v != "en" && v != "it") { v = DefaultPluginLanguage; }
                if (_pluginLanguage == v) { return; }
                _pluginLanguage = v;
                Loc.Instance.SetLanguage(v);
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
                        // §69 — migrazione one-shot: chi ha in settings.json il VECCHIO
                        // default passa a 127.0.0.1. Non era una scelta dell'utente ma un
                        // default con un difetto di prestazioni misurato; un URL scelto a
                        // mano (host, porta o schema diversi) viene invece rispettato.
                        var storedUrl = dto.DashboardUrl ?? DefaultDashboardUrl;
                        if (string.Equals(storedUrl.TrimEnd('/'), LegacyLocalhostUrl,
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Info("Plugin settings: migrating dashboard URL "
                                        + $"{LegacyLocalhostUrl} -> {DefaultDashboardUrl} "
                                        + "(§69: localhost resolves to IPv6 first on Windows, "
                                        + "costing ~2 s per call)");
                            storedUrl = DefaultDashboardUrl;
                        }
                        settings.DashboardUrl = storedUrl;
                        // Utenti che aggiornano da v1.1 non hanno la chiave => 0 => applica il default.
                        settings.StarLostConsolidationSeconds =
                            dto.StarLostConsolidationSeconds == 0 ? DefaultStarLostConsolidationSeconds : dto.StarLostConsolidationSeconds;
                        // §42 — bool? nel DTO: chiave assente (upgrade da <v1.3) => null => default true.
                        // (un bool non-nullable deserializzerebbe a false, disabilitando il forwarder.)
                        settings.ForwardTelemetryToAgent =
                            dto.ForwardTelemetryToAgent ?? DefaultForwardTelemetryToAgent;
                        // §49 — bool?/int? nel DTO: chiave assente (upgrade <v1.4) => default.
                        settings.CloudSafetyEnabled =
                            dto.CloudSafetyEnabled ?? DefaultCloudSafetyEnabled;
                        settings.CloudUnsafePolls =
                            (dto.CloudUnsafePolls == null || dto.CloudUnsafePolls == 0)
                                ? DefaultCloudUnsafePolls : dto.CloudUnsafePolls.Value;
                        settings.ClearSafePolls =
                            (dto.ClearSafePolls == null || dto.ClearSafePolls == 0)
                                ? DefaultClearSafePolls : dto.ClearSafePolls.Value;
                        // Fix N6 — chiavi assenti (upgrade <v1.5) => default (born-operative).
                        settings.UseIndexCloudLogic = dto.UseIndexCloudLogic ?? DefaultUseIndexCloudLogic;
                        settings.CloudIndexAccumulateBelow =
                            (dto.CloudIndexAccumulateBelow == null || dto.CloudIndexAccumulateBelow <= 0)
                                ? DefaultCloudIndexAccumulateBelow : dto.CloudIndexAccumulateBelow.Value;
                        settings.CloudIndexDrainAbove =
                            (dto.CloudIndexDrainAbove == null || dto.CloudIndexDrainAbove <= 0)
                                ? DefaultCloudIndexDrainAbove : dto.CloudIndexDrainAbove.Value;
                        settings.StaleUnsafeEnabled = dto.StaleUnsafeEnabled ?? DefaultStaleUnsafeEnabled;
                        settings.StaleUnsafePolls =
                            (dto.StaleUnsafePolls == null || dto.StaleUnsafePolls == 0)
                                ? DefaultStaleUnsafePolls : dto.StaleUnsafePolls.Value;
                        settings.AgentLostUnsafeEnabled = dto.AgentLostUnsafeEnabled ?? DefaultAgentLostUnsafeEnabled;
                        settings.AgentLostUnsafePolls =
                            (dto.AgentLostUnsafePolls == null || dto.AgentLostUnsafePolls == 0)
                                ? DefaultAgentLostUnsafePolls : dto.AgentLostUnsafePolls.Value;
                        // §58 — chiavi assenti (upgrade <v1.7) => default (§60: lifecycle ON);
                        // un false salvato esplicitamente resta false.
                        settings.AutoLaunchEnabled = dto.AutoLaunchEnabled ?? DefaultAutoLaunchEnabled;
                        settings.ManageExternalAgent = dto.ManageExternalAgent ?? DefaultManageExternalAgent;
                        // §68 — chiavi assenti (upgrade <v1.8) => default (born-operative).
                        settings.GuideUnobservableEnabled =
                            dto.GuideUnobservableEnabled ?? DefaultGuideUnobservableEnabled;
                        settings.GuideSilenceSeconds =
                            (dto.GuideSilenceSeconds == null || dto.GuideSilenceSeconds == 0)
                                ? DefaultGuideSilenceSeconds : dto.GuideSilenceSeconds.Value;
                        settings.GuideUnobservablePolls =
                            (dto.GuideUnobservablePolls == null || dto.GuideUnobservablePolls == 0)
                                ? DefaultGuideUnobservablePolls : dto.GuideUnobservablePolls.Value;
                        settings.SkyDegradingAccumulateEnabled =
                            dto.SkyDegradingAccumulateEnabled ?? DefaultSkyDegradingAccumulateEnabled;
                        settings.SkyDegradingUnsafePolls =
                            (dto.SkyDegradingUnsafePolls == null || dto.SkyDegradingUnsafePolls == 0)
                                ? DefaultSkyDegradingUnsafePolls : dto.SkyDegradingUnsafePolls.Value;
                        settings.ProbeChannelGateEnabled =
                            dto.ProbeChannelGateEnabled ?? DefaultProbeChannelGateEnabled;
                        settings.MeridianProtectionEnabled =
                            dto.MeridianProtectionEnabled ?? DefaultMeridianProtectionEnabled;
                        settings.MeridianLeadMinutes =
                            (dto.MeridianLeadMinutes == null || dto.MeridianLeadMinutes == 0)
                                ? DefaultMeridianLeadMinutes : dto.MeridianLeadMinutes.Value;
                        settings.PluginLanguage = dto.PluginLanguage ?? DefaultPluginLanguage;
                        settings._suppressSave = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to read plugin settings: {ex.Message}");
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
                    ForwardTelemetryToAgent = _forwardTelemetryToAgent,
                    CloudSafetyEnabled = _cloudSafetyEnabled,
                    CloudUnsafePolls = _cloudUnsafePolls,
                    ClearSafePolls = _clearSafePolls,
                    UseIndexCloudLogic = _useIndexCloudLogic,
                    CloudIndexAccumulateBelow = _cloudIndexAccumulateBelow,
                    CloudIndexDrainAbove = _cloudIndexDrainAbove,
                    StaleUnsafeEnabled = _staleUnsafeEnabled,
                    StaleUnsafePolls = _staleUnsafePolls,
                    AgentLostUnsafeEnabled = _agentLostUnsafeEnabled,
                    AgentLostUnsafePolls = _agentLostUnsafePolls,
                    AutoLaunchEnabled = _autoLaunchEnabled,
                    ManageExternalAgent = _manageExternalAgent,
                    GuideUnobservableEnabled = _guideUnobservableEnabled,
                    GuideSilenceSeconds = _guideSilenceSeconds,
                    GuideUnobservablePolls = _guideUnobservablePolls,
                    SkyDegradingAccumulateEnabled = _skyDegradingAccumulateEnabled,
                    ProbeChannelGateEnabled = _probeChannelGateEnabled,
                    MeridianProtectionEnabled = _meridianProtectionEnabled,
                    MeridianLeadMinutes = _meridianLeadMinutes,
                    PluginLanguage = _pluginLanguage,
                };
                File.WriteAllText(SettingsPath,
                    JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save plugin settings: {ex.Message}");
            }
        }

        private sealed class SettingsDto
        {
            public string? AgentBatPath { get; set; }
            public int HealthCheckIntervalSeconds { get; set; }
            public string? DashboardUrl { get; set; }
            public int StarLostConsolidationSeconds { get; set; }
            // §42 — nullable: distingue "chiave assente" (upgrade) da "false esplicito".
            public bool? ForwardTelemetryToAgent { get; set; }
            // §49 — nullable per upgrade-safety (chiave assente => default).
            public bool? CloudSafetyEnabled { get; set; }
            public int? CloudUnsafePolls { get; set; }
            public int? ClearSafePolls { get; set; }
            // Fix N6 — nullable per upgrade-safety (chiave assente da <v1.5 => default).
            public bool? UseIndexCloudLogic { get; set; }
            public double? CloudIndexAccumulateBelow { get; set; }
            public double? CloudIndexDrainAbove { get; set; }
            public bool? StaleUnsafeEnabled { get; set; }
            public int? StaleUnsafePolls { get; set; }
            public bool? AgentLostUnsafeEnabled { get; set; }
            public int? AgentLostUnsafePolls { get; set; }
            // §58 — nullable per upgrade-safety (chiave assente da <v1.7 => default).
            public bool? AutoLaunchEnabled { get; set; }
            public bool? ManageExternalAgent { get; set; }
            // §68 — nullable per upgrade-safety (chiave assente da <v1.8 => default).
            public bool? GuideUnobservableEnabled { get; set; }
            public int? GuideSilenceSeconds { get; set; }
            public int? GuideUnobservablePolls { get; set; }
            public bool? SkyDegradingAccumulateEnabled { get; set; }
            public int? SkyDegradingUnsafePolls { get; set; }
            public bool? ProbeChannelGateEnabled { get; set; }
            public bool? MeridianProtectionEnabled { get; set; }
            public int? MeridianLeadMinutes { get; set; }
            public string? PluginLanguage { get; set; }
        }
    }
}
