#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AdaptiveAgentForPHD2.NinaPlugin.Safety
{
    /// <summary>
    /// Safety Monitor virtuale (v1.2). NINA lo consuma come driver ISafetyMonitor sotto la categoria
    /// "N.I.N.A." nella tendina Equipment -> Safety Monitor. Riflette lo stato della guida dell'Adaptive
    /// Agent for PHD2: dichiara unsafe quando STAR_LOST persiste oltre il timeout configurato.
    ///
    /// Non prende decisioni operative: aggiorna solo IsSafe/Connected. NINA decide cosa farne
    /// (pausa/parking/alert) secondo la policy safety configurata dall'utente. Nessun ISequenceMediator.
    ///
    /// NINA legge IsSafe/Connected tramite un DeviceUpdateTimer (polling a DevicePollingInterval),
    /// non via PropertyChanged: impostare Connected=false e' quindi sufficiente per propagare a NINA
    /// l'auto-disconnessione quando l'Agente diventa irraggiungibile (verificato nel pre-flight).
    /// </summary>
    public sealed class AdaptiveAgentSafetyMonitor : BaseINPC, ISafetyMonitor
    {
        private readonly PluginSettings _settings;
        private readonly AgentHealthChecker _health;
        private readonly SafetyDecisionEngine _engine;

        private bool _isSafe = true;   // ottimistico all'avvio: safe finche' non si dimostra il contrario
        private bool _connected;
        private bool _subscribed;

        public AdaptiveAgentSafetyMonitor(PluginSettings settings, AgentHealthChecker health, SafetyDecisionEngine engine)
        {
            _settings = settings;
            _health = health;
            _engine = engine;
        }

        // --- Identita' del driver (vedi pre-flight: stesse convenzioni del SafetyMonitorSimulator di NINA) ---
        public string Name => "Adaptive Agent for PHD2 — Guide Safety";
        public string DisplayName => Name;
        public string Description =>
            "Riflette lo stato della guida dell'Adaptive Agent for PHD2. Dichiara unsafe quando STAR_LOST " +
            "persiste oltre il timeout configurato (default 5 minuti) oppure (v1.4, N6) quando la trasparenza " +
            "NINA resta CLOUD oltre l'isteresi configurata. Fail-safe: senza telemetria fresca resta solo STAR_LOST.";
        public string DriverInfo => "Adaptive Agent for PHD2 v1.4.0.0 — Safety Monitor virtuale";
        public string DriverVersion => "1.4.0.0";
        public string Category => "N.I.N.A.";
        // GUID stabile, distinto dal GUID del plugin (6F2E9C19-...). Generato una volta sola e hard-coded.
        public string Id => "10A715AD-903C-499E-9CC7-CA8E66A49B7C";

        public bool HasSetupDialog => false;

        public IList<string> SupportedActions => new List<string>();

        public bool IsSafe
        {
            get => _isSafe;
            private set { _isSafe = value; RaisePropertyChanged(); }
        }

        public bool Connected
        {
            get => _connected;
            private set { _connected = value; RaisePropertyChanged(); }
        }

        public async Task<bool> Connect(CancellationToken token)
        {
            _engine.Reset();

            // Connected richiede che l'Agente risponda sia a /about sia a /status entro il timeout (3s).
            var health = await _health.CheckOnceAsync().ConfigureAwait(false);
            var snap = await _health.CheckStatusOnceAsync().ConfigureAwait(false);
            if (!health.IsOnline || !snap.IsValid)
            {
                // Fallimento: niente subscribe, niente status polling. NINA mostrera' il driver disconnesso.
                Connected = false;
                return false;
            }

            IsSafe = true; // stato iniziale neutro
            Subscribe();
            _health.StatusPollingEnabled = true;
            Connected = true;
            Logger.Info("Adaptive Agent Safety Monitor: connected — Agent online");
            return true;
        }

        public void Disconnect()
        {
            // Idempotente: NINA puo' chiamarlo dopo che noi stessi abbiamo gia' fatto auto-disconnect.
            Unsubscribe();
            _health.StatusPollingEnabled = false;
            _engine.Reset();
            IsSafe = true; // stato neutro a riposo
            Connected = false;
        }

        public void SetupDialog() { /* no-op: HasSetupDialog == false */ }

        // --- Equipment generico non usato da questo sensore virtuale (come nel simulatore NINA) ---
        public string Action(string actionName, string actionParameters) => throw new NotImplementedException();
        public string SendCommandString(string command, bool raw = true) => throw new NotImplementedException();
        public bool SendCommandBool(string command, bool raw = true) => throw new NotImplementedException();
        public void SendCommandBlind(string command, bool raw = true) => throw new NotImplementedException();

        private void Subscribe()
        {
            if (_subscribed) { return; }
            _subscribed = true;
            _health.StatusChanged += OnHealthChanged;
            _health.StatusUpdated += OnStatusUpdated;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) { return; }
            _subscribed = false;
            _health.StatusChanged -= OnHealthChanged;
            _health.StatusUpdated -= OnStatusUpdated;
        }

        /// <summary>Auto-disconnessione silenziosa quando l'Agente smette di rispondere a /about.</summary>
        private void OnHealthChanged(AgentHealth health)
        {
            if (!Connected) { return; }
            if (!health.IsOnline)
            {
                Logger.Info("Adaptive Agent Safety Monitor: disconnected — Agent unreachable");
                Notification.ShowWarning("Adaptive Agent: Agent unreachable — Safety Monitor disconnected");
                Disconnect();
            }
        }

        /// <summary>Un tick di /status: il decision engine decide se transitare unsafe/safe.</summary>
        private void OnStatusUpdated(AgentStatusSnapshot snap)
        {
            if (!Connected) { return; }

            var decision = _engine.Evaluate(snap, _settings);
            switch (decision)
            {
                case SafetyDecision.BecameUnsafe:
                    IsSafe = false;
                    if (_engine.LastCause == SafetyCause.Cloud)
                    {
                        // §49 N6 — nubi: la trasparenza NINA è rimasta CLOUD oltre l'isteresi.
                        Logger.Info($"Adaptive Agent Safety Monitor: UNSAFE — clouds (NINA transparency CLOUD for {_settings.CloudUnsafePolls} polls)");
                        Notification.ShowWarning("Adaptive Agent: persistent clouds (NINA transparency) — Safety Monitor unsafe");
                    }
                    else
                    {
                        var secs = _settings.StarLostConsolidationSeconds;
                        var dur = secs >= 60 ? $"{secs / 60} minutes" : $"{secs}s";
                        Logger.Info($"Adaptive Agent Safety Monitor: UNSAFE — STAR_LOST sustained for {dur}");
                        Notification.ShowWarning($"Adaptive Agent: guiding lost for {dur} — Safety Monitor unsafe");
                    }
                    break;

                case SafetyDecision.BecameSafe:
                    IsSafe = true;
                    Logger.Info("Adaptive Agent Safety Monitor: SAFE — guiding back to NORMAL");
                    Notification.ShowInformation("Adaptive Agent: guiding recovered — Safety Monitor safe");
                    break;

                case SafetyDecision.NoChange:
                default:
                    break;
            }
        }
    }
}
