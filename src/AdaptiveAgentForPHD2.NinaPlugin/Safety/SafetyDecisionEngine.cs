#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using System;
using System.Globalization;

namespace AdaptiveAgentForPHD2.NinaPlugin.Safety
{
    /// <summary>Esito di una valutazione del decision engine. Le transizioni sono gli unici eventi notificati.</summary>
    public enum SafetyDecision { NoChange, BecameUnsafe, BecameSafe }

    /// <summary>Causa dell'ultima transizione UNSAFE (per log/notifica distinti).</summary>
    public enum SafetyCause { None, StarLost, Cloud, StaleTelemetry, AgentLost, GuideUnobservable }

    /// <summary>
    /// Motore di decisione del Safety Monitor (v1.5 — fix N6 post-validazione 2026-07-10).
    /// Riceve uno snapshot per tick e decide se la sessione e' diventata unsafe/safe.
    ///
    /// Principio guida: la perdita di osservazione affidabile del cielo durante una sessione
    /// attiva e' essa stessa una condizione di rischio. Quando il motore smette di vedere bene
    /// (telemetria stantia, agente irraggiungibile) NON si dichiara al sicuro: escalation
    /// verso UNSAFE con isteresi propria.
    ///
    /// Condizioni (OR dei latch):
    ///   STAR_LOST     guiding_state == STAR_LOST consolidato (v1.2, invariata).
    ///   CLOUD         degrado di trasparenza persistente. Default (UseIndexCloudLogic):
    ///                 accumulatore leaky sull'INDICE — index &lt; AccumulateBelow accumula (+1),
    ///                 index &gt;= DrainAbove drena (-CloudUnsafePolls/ClearSafePolls), zona HAZE
    ///                 in mezzo NEUTRA (non azzera: fix Bug A). UNSAFE a degrado &gt;= CloudUnsafePolls,
    ///                 SAFE a degrado 0. Kill-switch =&gt; logica legacy a poll consecutivi sullo stato.
    ///   STALE         telemetria non fresca (oltre la finestra adattiva §43 — il gap normale
    ///                 tra pose e' DENTRO la finestra) + sessione attiva + ultimo contesto
    ///                 degradato =&gt; UNSAFE dopo StaleUnsafePolls (fix Bug B: prima il ramo
    ///                 stantio azzerava tutto e spegneva la sicurezza in silenzio).
    ///   AGENT_LOST    agente irraggiungibile + sessione attiva =&gt; UNSAFE dopo
    ///                 AgentLostUnsafePolls (fix Bug C: prima produceva disconnect-to-SAFE).
    ///
    /// §76 — il percorso CLOUD ha ora DUE sensori: quello lento (N1, camera di ripresa,
    /// 300 s) e quello veloce (SNR della stella di guida, 3 s). Il veloce puo' solo
    /// ACCUMULARE verso unsafe e impedire il drain, mai drenare: una stella sola puo'
    /// testimoniare che il cielo e' brutto, non che il campo e' tornato buono.
    ///
    /// Recupero: al ritorno dei dati, i latch STALE/AGENT_LOST si trasferiscono sul percorso
    /// CLOUD (degrado saturato) =&gt; servono ClearSafePolls di evidenza CLEAR per tornare SAFE.
    /// Senza trasparenza disponibile, AGENT_LOST rientra con guida OPERATIVA per ResumeTicks.
    ///
    /// §65 — "guida operativa" = stella tracciata = qualunque stato tranne STAR_LOST e
    /// INACTIVE (insieme canonico dell'agente). Il rientro dal latch STAR_LOST NON dipende
    /// piu' dalla QUALITA' della guida (che compete al motore adattivo): il criterio di
    /// uscita e' il complemento esatto di quello d'ingresso.
    /// </summary>
    public sealed class SafetyDecisionEngine
    {
        public const int ResumeTicks = 3; // ~45s al default 15s

        // STAR_LOST (v1.2, invariato)
        private int _starLostStreakTicks;
        private int _normalStreakTicks;
        private bool _starLostUnsafe;

        // CLOUD — logica a indice (leaky) e logica legacy condividono il latch.
        private double _cloudDegradation;    // §55 percorso LENTO  — camera di ripresa, [0, CloudUnsafePolls]
        private double _fastDegradation;     // §79 percorso RAPIDO — canale guida,     [0, SkyDegradingUnsafePolls]
        private int _cloudStreakPolls;       // solo logica legacy
        private int _clearStreakPolls;       // solo logica legacy
        private bool _cloudUnsafe;

        // STALE / AGENT_LOST (fix N6)
        private int _staleStreakPolls;
        private bool _staleUnsafe;
        private int _agentLostStreakPolls;
        private bool _agentLostUnsafe;

        // §68 — GUIDE_UNOBSERVABLE: accumulatore LEAKY (non streak consecutivo: e' la
        // lezione del Bug A §55 — un paio di campioni "buoni" in mezzo a un degrado non
        // devono azzerare il contatore).
        private double _guideSilenceAccum;
        private bool _guideUnobservableUnsafe;

        // Memoria dell'ultimo contesto osservato in modo affidabile.
        private bool _lastKnownGuidingActive;
        private double? _lastFreshIndex;
        private string? _lastFreshState;

        /// <summary>Causa dell'ultima transizione UNSAFE (per log/notifica distinti).</summary>
        public SafetyCause LastCause { get; private set; } = SafetyCause.None;

        /// <summary>Riga diagnostica dell'ultimo tick (input + contatori + latch), per il log Debug (§3).</summary>
        public string LastTickSummary { get; private set; } = "";

        /// <summary>Riporta il motore allo stato iniziale. Chiamato su Connect/Disconnect ESPLICITI.</summary>
        public void Reset()
        {
            _starLostStreakTicks = 0;
            _normalStreakTicks = 0;
            _starLostUnsafe = false;
            _cloudDegradation = 0;
            _fastDegradation = 0;
            _cloudStreakPolls = 0;
            _clearStreakPolls = 0;
            _cloudUnsafe = false;
            _staleStreakPolls = 0;
            _staleUnsafe = false;
            _agentLostStreakPolls = 0;
            _agentLostUnsafe = false;
            _guideSilenceAccum = 0;
            _guideUnobservableUnsafe = false;
            _lastKnownGuidingActive = false;
            _lastFreshIndex = null;
            _lastFreshState = null;
            LastCause = SafetyCause.None;
            LastTickSummary = "";
        }

        public SafetyDecision Evaluate(AgentStatusSnapshot snap, ISafetySettings settings)
        {
            bool prevUnsafe = AnyUnsafe;
            bool prevStar = _starLostUnsafe, prevCloud = _cloudUnsafe,
                 prevStale = _staleUnsafe, prevLost = _agentLostUnsafe,
                 prevGuide = _guideUnobservableUnsafe;

            if (!snap.AgentReachable)
            {
                // Fix Bug C: agente irraggiungibile. Nessun reset, nessun disconnect: i latch
                // restano; a sessione attiva l'assenza di osservazione ESCALA verso UNSAFE.
                if (settings.AgentLostUnsafeEnabled && _lastKnownGuidingActive)
                {
                    _agentLostStreakPolls++;
                    if (_agentLostStreakPolls >= Math.Max(1, settings.AgentLostUnsafePolls))
                    {
                        _agentLostUnsafe = true;
                    }
                }
                BuildSummary(snap, settings);
                return Transition(prevUnsafe, prevStar, prevCloud, prevStale, prevLost, prevGuide);
            }

            // Agente di nuovo raggiungibile: il contatore rientra; il latch AGENT_LOST viene
            // gestito piu' sotto (trasferito al percorso CLOUD o rientrato via guida NORMAL).
            _agentLostStreakPolls = 0;

            if (!snap.IsValid)
            {
                // Payload raggiungibile ma incompleto/malformato (transitorio): mantieni tutto.
                BuildSummary(snap, settings);
                return Transition(prevUnsafe, prevStar, prevCloud, prevStale, prevLost, prevGuide);
            }

            // Memoria di sessione: l'unico stato "non attivo" e' INACTIVE (guida ferma volontaria).
            _lastKnownGuidingActive = snap.GuidingState != "INACTIVE";

            bool fresh = snap.TransparencyFresh;
            if (fresh)
            {
                if (snap.TransparencyIndex is double fi) { _lastFreshIndex = fi; }
                if (snap.TransparencyState is string fs) { _lastFreshState = fs; }
            }

            // ---- Trasferimento latch STALE/AGENT_LOST al ritorno dei dati ----
            // Il rientro non e' gratis: satura il degrado CLOUD => servono ClearSafePolls di
            // evidenza CLEAR per tornare SAFE (isteresi di recupero unica e coerente).
            if ((_staleUnsafe && fresh) || (_agentLostUnsafe && fresh && settings.CloudSafetyEnabled))
            {
                _cloudDegradation = Math.Max(_cloudDegradation, Math.Max(1, settings.CloudUnsafePolls));
                _cloudUnsafe = true;
                _staleUnsafe = false;
                _agentLostUnsafe = false;
            }

            // ---- Condizione 1: STAR_LOST (guida) ----
            bool isStarLost = snap.GuidingState == "STAR_LOST";
            // §65 — RIENTRO su "guida OPERATIVA", non su "guida eccellente".
            //
            // Prima si usciva dal latch solo con guiding_state == NORMAL, che nell'agente
            // richiede rms < rms_low (75% della baseline): una condizione RARA — la guida
            // normale vive nella banda neutra, dove lo stato non viene aggiornato affatto.
            // Effetto provato sul campo (notte 19/7): cielo limpido (indice N1 0.95-1.00)
            // e UNSAFE mantenuto per 18 minuti, con rientro dipendente da un tuffo casuale
            // dell'RMS — non dal cielo. Il monitor aspettava la QUALITA' della guida, che
            // e' responsabilita' del motore adattivo, non della safety.
            //
            // Il criterio corretto e' il COMPLEMENTO ESATTO di quello d'ingresso: si entra
            // in UNSAFE perche' la stella e' persa, si esce quando la stella e' di nuovo
            // tracciata. Uno stato che non basta a FAR SCATTARE la protezione (DEGRADED,
            // CRITICAL: nessuno dei due genera UNSAFE) non deve poterla MANTENERE.
            // Insieme allineato al raggruppamento canonico dell'agente — controller.py,
            // "PHD2 deve essere in guida valida per riselezionare":
            //     non operativi = { STAR_LOST, INACTIVE }  ->  operativi = tutti gli altri.
            // GuidingState null (payload incompleto) NON conta come operativo: fail-safe.
            bool isGuidingOperational = snap.GuidingState is string g
                                        && g != "STAR_LOST" && g != "INACTIVE";
            if (isStarLost)
            {
                _starLostStreakTicks++;
                _normalStreakTicks = 0;
                int consolidationTicks = Math.Max(1,
                    settings.StarLostConsolidationSeconds / Math.Max(1, settings.HealthCheckIntervalSeconds));
                if (_starLostStreakTicks >= consolidationTicks) { _starLostUnsafe = true; }
            }
            else if (isGuidingOperational)
            {
                _starLostStreakTicks = 0;
                if (_starLostUnsafe || _agentLostUnsafe)
                {
                    _normalStreakTicks++;
                    if (_normalStreakTicks >= ResumeTicks)
                    {
                        _starLostUnsafe = false;
                        // AGENT_LOST senza trasparenza disponibile: la guida tornata
                        // OPERATIVA e' l'evidenza di rientro (con trasparenza, governa
                        // il percorso CLOUD).
                        if (_agentLostUnsafe && !fresh) { _agentLostUnsafe = false; }
                    }
                }
            }
            else
            {
                _starLostStreakTicks = 0;
                _normalStreakTicks = 0;
            }

            // ---- Condizione 1-bis (§68): GUIDE_UNOBSERVABLE ----
            // Domanda diversa da STAR_LOST: non "la stella e' persa?" ma "posso ancora
            // FIDARMI del canale di guida?". Il 26/7 PHD2 ha smesso di parlare e
            // `guiding_state` e' rimasto congelato su CRITICAL: N6 leggeva "sta guidando
            // male" mentre la verita' era "non sta guidando affatto".
            //
            // Gate: la guida deve essere ATTESA. Le pause legittime (flip, autofocus,
            // stop volontario) PHD2 le ANNUNCIA; sui guasti tace. Il gate e' calcolato
            // dall'agente su quegli annunci ed e' volutamente INDIPENDENTE da
            // _lastKnownGuidingActive, che governa STALE/AGENT_LOST e non va toccato.
            //
            // S1 (fail-safe deterministico): il silenzio del canale oltre la soglia.
            // S2 (corroborazione): Alert PHD2 warning/error o raffica di ErrorCode
            // per-frame DIMEZZANO la soglia — possono solo ANTICIPARE, mai decidere da
            // soli (stesso paletto del §57: l'acceleratore non ha autorita').
            if (settings.GuideUnobservableEnabled && snap.GuidingExpected
                && snap.GuideFrameAgeS is double frameAge)
            {
                double threshold = Math.Max(5, settings.GuideSilenceSeconds);
                bool corroborated = snap.GuideAlertSevere || snap.GuideStarErrorsRecent >= 3;
                if (corroborated) { threshold /= 2.0; }

                double cap = Math.Max(1, settings.GuideUnobservablePolls);
                if (frameAge > threshold)
                {
                    _guideSilenceAccum = Math.Min(cap, _guideSilenceAccum + 1.0);
                }
                else
                {
                    // Frame che tornano a fluire = evidenza POSITIVA di osservabilita'.
                    _guideSilenceAccum = Math.Max(0.0, _guideSilenceAccum - 1.0);
                }
                if (_guideSilenceAccum >= cap) { _guideUnobservableUnsafe = true; }
                if (_guideSilenceAccum <= 0.0) { _guideUnobservableUnsafe = false; }
            }
            else
            {
                // Guida non attesa (pausa annunciata), latch spento o Agente che non
                // espone il blocco §68: nessun allarme e rientro immediato. La pausa
                // legittima non deve mai lasciare strascichi.
                _guideSilenceAccum = 0.0;
                _guideUnobservableUnsafe = false;
            }

            // ---- Condizione 2: STALE (fix Bug B) ----
            if (fresh)
            {
                _staleStreakPolls = 0;
            }
            else if (settings.CloudSafetyEnabled && settings.StaleUnsafeEnabled
                     && _lastKnownGuidingActive && LastContextDegraded(settings))
            {
                // fresh=false significa GIA' "oltre la finestra adattiva §43" (il gap normale
                // tra pose e' dentro la finestra: nessun falso allarme sul buco tra sub).
                _staleStreakPolls++;
                if (_staleStreakPolls >= Math.Max(1, settings.StaleUnsafePolls)) { _staleUnsafe = true; }
            }
            // NB: lo stantio NON azzera piu' il degrado accumulato (prima: reset silenzioso).

            // ---- Condizione 3: CLOUD ----
            // §76 — il gate si apre anche col SOLO sensore veloce: la SNR di guida
            // arriva da PHD2, non da NINA, quindi non dipende dalla freschezza della
            // telemetria di ripresa. E' proprio il caso in cui e' l'unico che parla.
            if (settings.CloudSafetyEnabled
                && (fresh || (settings.SkyDegradingAccumulateEnabled && snap.SkyDegrading)))
            {
                if (settings.UseIndexCloudLogic)
                {
                    EvaluateCloudByIndex(snap, settings);
                }
                else
                {
                    EvaluateCloudLegacy(snap, settings);
                }
            }
            else if (settings.CloudSafetyEnabled && !settings.UseIndexCloudLogic)
            {
                // Logica LEGACY (kill-switch): comportamento pre-fix, streak azzerati sullo stantio.
                _cloudStreakPolls = 0;
                _clearStreakPolls = 0;
            }
            // Logica a indice + stantio: il degrado RESTA (congelato) finche' non tornano dati.

            BuildSummary(snap, settings);
            return Transition(prevUnsafe, prevStar, prevCloud, prevStale, prevLost, prevGuide);
        }

        /// <summary>
        /// Fix Bug A: accumulatore leaky sull'indice. HAZE (zona intermedia) e' NEUTRA.
        ///
        /// §79 — DUE PERCORSI SEPARATI per la stessa informazione (il cielo peggiora),
        /// con accumulatori e soglie indipendenti:
        ///
        ///   RAPIDO  (§76)  canale guida / PHD2, ~3 s   -> "sta succedendo adesso"
        ///   LENTO   (§55)  camera di ripresa, ~300 s   -> "dura da abbastanza tempo"
        ///
        /// Prima condividevano accumulatore e tetto: alzare la soglia di PERSISTENZA
        /// (CloudUnsafePolls) rallentava anche il sensore RAPIDO, cioe' proprio cio' per
        /// cui il §76 era nato — anticipare la camera. Sono dimensioni diverse e ora
        /// hanno contatori diversi; l'unsafe e' l'OR dei due.
        ///
        /// Restano invariate le due asimmetrie del §76:
        ///   • il canale guida puo' BLOCCARE il drenaggio del percorso lento (evidenza
        ///     negativa fresca contro evidenza positiva vecchia), mai provocarlo;
        ///   • il rientro verso SAFE lo concede SOLO la camera di ripresa. La stella di
        ///     guida e' UNA: puo' dire "qui e' brutto" (le nubi sono grandi), non "il
        ///     campo e' buono" — uno squarcio sopra la stella non salva il resto. Per
        ///     questo entrambi gli accumulatori drenano sull'evidenza della camera.
        /// </summary>
        private void EvaluateCloudByIndex(AgentStatusSnapshot snap, ISafetySettings settings)
        {
            double below = settings.CloudIndexAccumulateBelow;
            double above = Math.Max(settings.CloudIndexDrainAbove, below + 0.01);
            int slowCap = Math.Max(1, settings.CloudUnsafePolls);
            int fastCap = Math.Max(1, settings.SkyDegradingUnsafePolls);
            int clearPolls = Math.Max(1, settings.ClearSafePolls);

            // Il drenaggio si scala sul PROPRIO tetto: cosi' ClearSafePolls conserva il
            // suo significato ("N poll di cielo sereno per rientrare") su entrambi i
            // percorsi, quale che sia il rapporto fra le due soglie.
            double slowDrain = Math.Max(1.0, (double)slowCap / clearPolls);
            double fastDrain = Math.Max(1.0, (double)fastCap / clearPolls);

            bool fastEvidence = settings.SkyDegradingAccumulateEnabled && snap.SkyDegrading;

            // ---- evidenza LENTA: solo dalla camera di ripresa ----
            bool accumulate = false, drain = false;
            if (snap.TransparencyIndex is double idx)
            {
                accumulate = idx < below;
                drain = idx >= above;
            }
            else if (snap.TransparencyState is string st)
            {
                // Fallback robusto quando l'indice non e' (ancora) disponibile:
                // CLOUD accumula, CLEAR drena, HAZE neutra (NON azzera).
                accumulate = st == "CLOUD";
                drain = st == "CLEAR";
            }
            else if (!fastEvidence)
            {
                return; // nessun dato di trasparenza e nessun segnale rapido: non toccare nulla
            }
            // (nessun dato di trasparenza ma canale guida che vede il degrado: si prosegue
            //  con accumulate/drain a false — lavora il solo percorso rapido, qui sotto.)

            // Evidenza negativa FRESCA contro evidenza positiva VECCHIA: finche' il canale
            // guida vede brutto, il percorso lento non scende. Non lo alimenta piu' (§79):
            // ha il suo contatore.
            if (fastEvidence) { drain = false; }

            if (accumulate)
            {
                _cloudDegradation = Math.Min(slowCap, _cloudDegradation + 1.0);
            }
            else if (drain)
            {
                _cloudDegradation = Math.Max(0.0, _cloudDegradation - slowDrain);
            }
            // zona intermedia: nessuna variazione (il flicker CLOUD<->HAZE non azzera piu')

            if (fastEvidence)
            {
                _fastDegradation = Math.Min(fastCap, _fastDegradation + 1.0);
            }
            else if (drain)
            {
                _fastDegradation = Math.Max(0.0, _fastDegradation - fastDrain);
            }

            if (_cloudDegradation >= slowCap || _fastDegradation >= fastCap) { _cloudUnsafe = true; }
            if (_cloudDegradation <= 0.0 && _fastDegradation <= 0.0 && _cloudUnsafe) { _cloudUnsafe = false; }
        }

        /// <summary>Logica legacy pre-fix (kill-switch): poll consecutivi sullo stato discreto.</summary>
        private void EvaluateCloudLegacy(AgentStatusSnapshot snap, ISafetySettings settings)
        {
            if (snap.TransparencyState == null) { return; }
            bool isCloud = snap.TransparencyState == "CLOUD";
            bool isClearish = snap.TransparencyState == "CLEAR" || snap.TransparencyState == "HAZE";
            if (isCloud)
            {
                _cloudStreakPolls++;
                _clearStreakPolls = 0;
                if (_cloudStreakPolls >= Math.Max(1, settings.CloudUnsafePolls)) { _cloudUnsafe = true; }
            }
            else if (isClearish)
            {
                _cloudStreakPolls = 0;
                if (_cloudUnsafe)
                {
                    _clearStreakPolls++;
                    if (_clearStreakPolls >= Math.Max(1, settings.ClearSafePolls)) { _cloudUnsafe = false; }
                }
            }
            // stato sconosciuto -> non tocca i latch
        }

        /// <summary>Ultimo contesto osservato in modo affidabile = degradato? (indice basso o stato non-CLEAR).</summary>
        private bool LastContextDegraded(ISafetySettings settings)
        {
            if (_lastFreshIndex is double idx && idx < settings.CloudIndexAccumulateBelow) { return true; }
            if (_lastFreshState is string st && st != "CLEAR") { return true; }
            return false;
        }

        private bool AnyUnsafe => _starLostUnsafe || _cloudUnsafe || _staleUnsafe
                                  || _agentLostUnsafe || _guideUnobservableUnsafe;

        private SafetyDecision Transition(bool prevUnsafe,
            bool prevStar, bool prevCloud, bool prevStale, bool prevLost, bool prevGuide)
        {
            bool nowUnsafe = AnyUnsafe;
            if (nowUnsafe && !prevUnsafe)
            {
                // Causa = il latch scattato in QUESTO tick (in ordine di specificita').
                LastCause = (_starLostUnsafe && !prevStar) ? SafetyCause.StarLost
                          : (_cloudUnsafe && !prevCloud) ? SafetyCause.Cloud
                          : (_staleUnsafe && !prevStale) ? SafetyCause.StaleTelemetry
                          : (_agentLostUnsafe && !prevLost) ? SafetyCause.AgentLost
                          : (_guideUnobservableUnsafe && !prevGuide) ? SafetyCause.GuideUnobservable
                          : SafetyCause.StarLost;
                return SafetyDecision.BecameUnsafe;
            }
            if (!nowUnsafe && prevUnsafe)
            {
                LastCause = SafetyCause.None;
                return SafetyDecision.BecameSafe;
            }
            return SafetyDecision.NoChange;
        }

        /// <summary>§3 osservabilita' — una riga per tick con input, contatori e latch.</summary>
        private void BuildSummary(AgentStatusSnapshot snap, ISafetySettings settings)
        {
            var ic = CultureInfo.InvariantCulture;
            string idx = snap.TransparencyIndex?.ToString("0.00", ic) ?? "-";
            string age = snap.TelemetryAgeS?.ToString("0", ic) ?? "-";
            LastTickSummary =
                $"reachable={snap.AgentReachable} guiding={snap.GuidingState ?? "-"} " +
                $"transp={snap.TransparencyState ?? "-"} idx={idx} fresh={snap.TransparencyFresh} age={age}s | " +
                $"degr={_cloudDegradation.ToString("0.#", ic)}/{settings.CloudUnsafePolls} " +
                $"fast={_fastDegradation.ToString("0.#", ic)}/{settings.SkyDegradingUnsafePolls} " +
                $"starlost={_starLostStreakTicks} stale={_staleStreakPolls}/{settings.StaleUnsafePolls} " +
                $"lost={_agentLostStreakPolls}/{settings.AgentLostUnsafePolls} " +
                $"guide[age={snap.GuideFrameAgeS?.ToString("0", ic) ?? "-"}s exp={B(snap.GuidingExpected)} " +
                $"acc={_guideSilenceAccum.ToString("0.#", ic)}/{settings.GuideUnobservablePolls} " +
                $"err={snap.GuideStarErrorsRecent} alert={B(snap.GuideAlertSevere)}] | " +
                $"latch[star={B(_starLostUnsafe)} cloud={B(_cloudUnsafe)} stale={B(_staleUnsafe)} " +
                $"lost={B(_agentLostUnsafe)} guide={B(_guideUnobservableUnsafe)}]";
            static string B(bool b) => b ? "1" : "0";
        }
    }
}
