#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Localization;
using AdaptiveAgentForPHD2.NinaPlugin.Lifecycle;
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AdaptiveAgentForPHD2.NinaPlugin.Sequencer
{
    /// <summary>
    /// "Recovery probe (Adaptive Agent)": il recupero automatico da UNSAFE in una sola
    /// istruzione, da mettere DIRETTAMENTE dentro "Before Waiting For Safety" del Trigger
    /// On Unsafe (i container del trigger rifiutano istruzioni Camera e condizioni di ciclo:
    /// §57-bis/§57-ter, per questo il ciclo e' interno all'istruzione).
    ///
    /// §126-bis — SONDA DI GUIDA. Il cielo lo giudica la camera di guida, che continua a
    /// misurare anche a sequenza ferma: il ritorno a SAFE non passa da nessuna posa. Il
    /// compito della sonda e' tenere vivo il giudice: durante l'UNSAFE fa ripartire la
    /// guida se e' ferma, se la stella resta persa, o una volta per TimeoutMinutes di UNSAFE
    /// con la guida che gira (GuideRestartPolicy). Esce da sola al SAFE.
    /// §128 (1.15) — tolto il ciclo delle pose-sonda della camera di ripresa (cadenza
    /// automatica, posa di ripiego, memoria dell'ultimo light). Le sequenze salvate con i
    /// campi AutoTimeout/FallbackExposureSeconds si caricano lo stesso: NINA legge le
    /// sequenze con Newtonsoft e le impostazioni standard ignorano i campi sconosciuti
    /// (verificato nel test RecoveryProbeCompatibilityTests).
    ///
    /// Confine di safety: l'istruzione LEGGE lo stato del monitor (come il WaitUntilSafe
    /// core) solo per capire quando fermarsi; non giudica e non imposta mai IsSafe.
    /// </summary>
    [ExportMetadata("Name", "Recovery probe (Adaptive Agent)")]
    [ExportMetadata("Description",
        "Recovery step for the Trigger On Unsafe ('Before Waiting For Safety'). The guide channel judges " +
        "the sky, so this instruction takes no exposures: it keeps the judge alive. While unsafe it restarts " +
        "guiding if guiding has stopped, if the guide star has been lost for longer than the minimum interval, " +
        "or once per restart interval of continued unsafe (postponed while the guide is confirming a clear " +
        "sky); it ends on its own when the monitor returns SAFE.\n\n" +
        "IT: passo di recupero per il Trigger On Unsafe ('Before Waiting For Safety'). Il cielo lo giudica la " +
        "guida, quindi non scatta pose: tiene vivo il giudice. Durante l'unsafe fa ripartire la guida se si e' " +
        "fermata, se la stella resta persa piu' dell'intervallo minimo, o una volta ogni intervallo di riavvio di " +
        "unsafe (rimandato mentre la guida conferma il sereno); finisce da sola quando il monitor torna SAFE.")]
    [ExportMetadata("Icon", "HourglassSVG")]
    [ExportMetadata("Category", "Adaptive Agent for PHD2")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class RecoveryProbe : SequenceItem, IValidatable
    {
        private const double DefaultTimeoutMinutes = 12;
        private const double DefaultMinIntervalMinutes = 5;
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        // Ultimo riavvio della guida: condiviso tra istanze (floor dell'intervallo minimo).
        private static DateTimeOffset _lastRestartUtc = DateTimeOffset.MinValue;

        private readonly ISafetyMonitorMediator _safetyMediator;
        private readonly IGuiderMediator _guiderMediator;
        private readonly ITelescopeMediator _telescopeMediator;
        private readonly HttpClient _http;
        private readonly PluginSettings _settings;

        private double _timeoutMinutes = DefaultTimeoutMinutes;
        private double _minIntervalMinutes = DefaultMinIntervalMinutes;
        private IList<string> _issues = new List<string>();

        [ImportingConstructor]
        public RecoveryProbe(ISafetyMonitorMediator safetyMonitorMediator,
                             IGuiderMediator guiderMediator,
                             ITelescopeMediator telescopeMediator)
        {
            _safetyMediator = safetyMonitorMediator;
            _guiderMediator = guiderMediator;
            _telescopeMediator = telescopeMediator;
            _settings = AgentServices.Instance.Settings;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        private RecoveryProbe(RecoveryProbe cloneMe)
            : this(cloneMe._safetyMediator, cloneMe._guiderMediator, cloneMe._telescopeMediator)
        {
            CopyMetaData(cloneMe);
        }

        public override object Clone()
        {
            return new RecoveryProbe(this)
            {
                TimeoutMinutes = TimeoutMinutes,
                MinIntervalMinutes = MinIntervalMinutes,
            };
        }

        /// <summary>Ogni quanti minuti di UNSAFE continuo, con la guida che gira, la sonda
        /// rimette in moto il canale di guida (il 3/10 un guasto della camera di guida si e'
        /// risolto a una ripartenza). Nome storico conservato: e' la chiave nelle sequenze salvate.</summary>
        [JsonProperty]
        public double TimeoutMinutes
        {
            get => _timeoutMinutes;
            set { _timeoutMinutes = Math.Clamp(value, 1, 120); RaisePropertyChanged(); }
        }

        /// <summary>Intervallo minimo fra due ripartenze della guida, e quanto la guida deve
        /// restare ferma (o la stella persa) prima di farla ripartire. Mai sotto 1 minuto.</summary>
        [JsonProperty]
        public double MinIntervalMinutes
        {
            get => _minIntervalMinutes;
            set { _minIntervalMinutes = Math.Clamp(value, 0, 60); RaisePropertyChanged(); }
        }

        public IList<string> Issues
        {
            get => _issues;
            private set { _issues = value; RaisePropertyChanged(); }
        }

        public bool Validate()
        {
            var issues = new List<string>();
            if (!_guiderMediator.GetInfo().Connected)
            {
                issues.Add(Loc.T("Probe_Issue_Guider"));
            }
            if (!_safetyMediator.GetInfo().Connected)
            {
                issues.Add(Loc.T("Probe_Issue_Safety"));
            }
            Issues = issues;
            return issues.Count == 0;
        }

        /// <summary>
        /// La sonda di guida. Non giudica e non imposta IsSafe: tiene viva la GUIDA, che e'
        /// il giudice. Tre casi, uno alla volta, mai piu' spesso di MinIntervalMinutes:
        ///   • guida NON attesa da almeno MinInterval → StartGuiding;
        ///   • stella persa da almeno MinInterval → StopGuiding + StartGuiding: dopo una nube
        ///     lunga la stella puo' essere uscita dalla regione di ricerca di PHD2 (15 px);
        ///   • UNSAFE da oltre TimeoutMinutes con la guida che gira → un riavvio del canale,
        ///     rimandato mentre la guida sta confermando il sereno (§126-sexies).
        /// </summary>
        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            // §126-quater — l'intervallo minimo e' l'unico freno ai riavvii: mai sotto 1
            // minuto (0 era ammesso e dava una raffica).
            var minInterval = TimeSpan.FromMinutes(Math.Max(1.0, MinIntervalMinutes));
            var cadenza = TimeSpan.FromMinutes(TimeoutMinutes);
            var ultimoRiavvio = DateTimeOffset.UtcNow;
            DateTimeOffset? nonAttesaDa = null;
            bool toastMostrato = false;
            Logger.Info("RecoveryProbe: the guide channel judges the sky — no probe exposures; "
                        + $"guiding is restarted if it stops (min interval {MinIntervalMinutes:0.#} min, "
                        + $"channel restart every {TimeoutMinutes:0.#} min of unsafe)");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (IsSafeNow())
                {
                    Logger.Info("RecoveryProbe: safety monitor reports SAFE — waiting ends");
                    return;
                }

                var (attesa, persa, persaDa, conferma) = await ReadGuideStatusAsync().ConfigureAwait(false);
                var ora = DateTimeOffset.UtcNow;
                bool liberoDaIntervallo = ora - _lastRestartUtc >= minInterval;
                if (attesa == false) { nonAttesaDa ??= ora; } else { nonAttesaDa = null; }
                var (motivo, fermaPrima) = GuideRestartPolicy.Decide(
                    attesa, persa, persaDa, conferma,
                    nonAttesaDa.HasValue ? ora - nonAttesaDa.Value : TimeSpan.Zero,
                    ora - ultimoRiavvio, liberoDaIntervallo, minInterval, cadenza);

                if (motivo != null)
                {
                    _lastRestartUtc = ora;
                    ultimoRiavvio = ora;
                    nonAttesaDa = null;
                    try
                    {
                        await RestartGuidingAsync(motivo, fermaPrima, progress, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"RecoveryProbe: restarting guiding FAILED ({ex.Message}) — "
                                       + "will retry after the minimum interval");
                        if (!toastMostrato)
                        {
                            toastMostrato = true;
                            ToastHelper.Show(() => NINA.Core.Utility.Notification.Notification.ShowWarning(
                                string.Format(Loc.T("Toast_GuideProbeFailed"), ex.Message)));
                        }
                    }
                }
                else
                {
                    progress?.Report(new ApplicationStatus
                    {
                        Status = conferma == true
                            ? "Recovery probe: the guide channel is confirming a clear sky — waiting for SAFE"
                            : "Recovery probe: the guide channel is watching the sky — waiting for SAFE"
                    });
                }
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
        }

        private async Task RestartGuidingAsync(string motivo, bool fermaPrima,
                                               IProgress<ApplicationStatus>? progress, CancellationToken token)
        {
            // §126-quater — il monitor puo' essere tornato SAFE mentre si attendeva il
            // turno: in quel caso non si tocca niente.
            if (IsSafeNow()) { return; }
            var tel = _telescopeMediator.GetInfo();
            if (!tel.Connected || tel.AtPark || !tel.TrackingEnabled || tel.Slewing)
            {
                // Montatura scollegata, parcheggiata, ferma o in movimento: far ripartire
                // la guida non serve o disturba (§72: la guardia di NINA puo' aver
                // fermato il tracking alla scadenza del flip).
                Logger.Warning("RecoveryProbe: mount not ready (connected=" + tel.Connected
                               + ", parked=" + tel.AtPark + ", tracking=" + tel.TrackingEnabled
                               + ", slewing=" + tel.Slewing + ") — guiding NOT restarted");
                return;
            }
            if (!_guiderMediator.GetInfo().Connected)
            {
                Logger.Warning("RecoveryProbe: guider not connected — guiding NOT restarted");
                return;
            }
            Logger.Warning($"RecoveryProbe: {motivo} — restarting guiding so the guide channel can judge the sky");
            progress?.Report(new ApplicationStatus { Status = "Recovery probe: restarting guiding" });
            if (fermaPrima)
            {
                await _guiderMediator.StopGuiding(token).ConfigureAwait(false);
            }
            bool ok = await _guiderMediator.StartGuiding(false, progress, token).ConfigureAwait(false);
            if (!ok) { throw new InvalidOperationException("start guiding did not succeed"); }
            Logger.Info("RecoveryProbe: guiding restarted — the guide channel will confirm the sky");
        }

        /// <summary>§126-bis — GET /status → (guida attesa, stella persa, da quanti secondi,
        /// §126-sexies: la guida sta confermando il sereno).
        /// Graceful: qualunque errore => tutto null e nessuna azione.</summary>
        private async Task<(bool? Attesa, bool? Persa, double? PersaDa, bool? Conferma)> ReadGuideStatusAsync()
        {
            try
            {
                var url = _settings.DashboardUrl.TrimEnd('/') + "/status";
                using var response = await _http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) { return (null, null, null, null); }
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("guide_health", out var gh)
                    || gh.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return (null, null, null, null);
                }
                bool? attesa = null, persa = null;
                double? persaDa = null;
                if (gh.TryGetProperty("guiding_expected", out var ge))
                {
                    if (ge.ValueKind == System.Text.Json.JsonValueKind.True) { attesa = true; }
                    else if (ge.ValueKind == System.Text.Json.JsonValueKind.False) { attesa = false; }
                }
                if (gh.TryGetProperty("star_lost", out var sl))
                {
                    if (sl.ValueKind == System.Text.Json.JsonValueKind.True) { persa = true; }
                    else if (sl.ValueKind == System.Text.Json.JsonValueKind.False) { persa = false; }
                }
                if (gh.TryGetProperty("star_lost_s", out var sls) && sls.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    persaDa = sls.GetDouble();
                }
                // §126-sexies — Agente 3.2: la guida sta accumulando sereno (assente prima).
                bool? conferma = null;
                if (doc.RootElement.TryGetProperty("recovery_hint", out var rh)
                    && rh.ValueKind == System.Text.Json.JsonValueKind.Object
                    && rh.TryGetProperty("sereno_in_conferma", out var sc))
                {
                    if (sc.ValueKind == System.Text.Json.JsonValueKind.True) { conferma = true; }
                    else if (sc.ValueKind == System.Text.Json.JsonValueKind.False) { conferma = false; }
                }
                return (attesa, persa, persaDa, conferma);
            }
            catch
            {
                return (null, null, null, null);
            }
        }

        /// <summary>Lettura (mai scrittura) dello stato del Safety Monitor — come il WaitUntilSafe core.</summary>
        private bool IsSafeNow()
        {
            try
            {
                var info = _safetyMediator.GetInfo();
                return info.Connected && info.IsSafe;
            }
            catch
            {
                return false;   // stato ignoto => continua il recovery (conservativo)
            }
        }

        public override TimeSpan GetEstimatedDuration()
        {
            // Durata reale = finche' torna SAFE (non prevedibile): stima di un intervallo di riavvio.
            return TimeSpan.FromMinutes(TimeoutMinutes);
        }

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(RecoveryProbe)}, " +
            $"Restart: {TimeoutMinutes} min, MinInterval: {MinIntervalMinutes} min";
    }
}
