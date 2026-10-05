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
    /// §126 (v1.14, 04/10/2026) — LA GUIDA GIUDICA IL CIELO. Decisione dell'operatore dopo la
    /// forense della notte 3-4/10: un falso UNSAFE della camera di ripresa (UNA posa con
    /// stelle allungate, indice 0.457) ha fermato la sequenza per tre ore mentre la stella di
    /// guida — stesso tubo, stesso cielo — aveva SNR 66.3 su un riferimento di 65.5.
    /// Da questa versione, con ImagingCameraUnsafeEnabled=false (default):
    ///   • il cielo lo giudica il SOLO canale di guida, in entrambi i versi: il degrado
    ///     (SkyDegrading) accumula, l'evidenza di sereno (SkyOk + canale pronto) drena;
    ///   • la camera di ripresa e' informativa: niente percorso CLOUD sull'indice, niente
    ///     latch STALE; la sua telemetria resta in dashboard;
    ///   • STAR_LOST si legge sulla stella persa misurata dagli EVENTI di PHD2 (Agente 3.1),
    ///     non sullo stato del controller, che usciva solo con l'RMS fuori dalla banda
    ///     neutra (10/8: un frame perso, 126 buoni, UNSAFE); al rientro la guida conferma il
    ///     cielo prima del SAFE;
    ///   • con la guida ferma per annuncio (calibrazione, Guide Assistant, autofocus) il
    ///     percorso del cielo si congela: niente evidenza, niente decisione.
    /// ImagingCameraUnsafeEnabled=true riporta il comportamento fino al 1.13, descritto sotto.
    ///
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

        // §126-bis — giudice EFFETTIVO del cielo, deciso una volta per collegamento
        // all'Agente (al primo /status valido dopo Connect o dopo un Agente perso):
        // guida solo se l'opzione lo chiede E l'Agente dichiara di poterlo fare.
        // Deciso poll per poll, un payload transitorio cambierebbe giudice a latch acceso.
        private bool? _guideJudge;
        private bool _judgeOptionAtDecision;
        private bool _judgeRedecide = true;

        // Memoria dell'ultimo contesto osservato in modo affidabile.
        private bool _lastKnownGuidingActive;
        private double? _lastFreshIndex;
        private string? _lastFreshState;

        /// <summary>Causa dell'ultima transizione UNSAFE (per log/notifica distinti).</summary>
        public SafetyCause LastCause { get; private set; } = SafetyCause.None;

        /// <summary>Riga diagnostica dell'ultimo tick (input + contatori + latch), per il log Debug (§3).</summary>
        public string LastTickSummary { get; private set; } = "";

        /// <summary>§126-bis — il giudice effettivo e' la guida.</summary>
        public bool GuideJudgeActive => _guideJudge == true;

        /// <summary>§126-bis — il giudice e' stato deciso (almeno un /status valido).</summary>
        public bool JudgeDecided => _guideJudge.HasValue;

        /// <summary>§126-bis — il giudice e' stato (ri)deciso in questo tick: il monitor lo scrive nel log.</summary>
        public bool JudgeChangedThisTick { get; private set; }

        /// <summary>§126-bis — la causa ATTUALE (il latch ancora acceso), non quella di
        /// ingresso: dopo una stella ritrovata il latch passa al cielo della guida e la
        /// dashboard deve dire "si aspetta il sereno", non "stella persa".</summary>
        public SafetyCause CurrentCause =>
            _starLostUnsafe ? SafetyCause.StarLost
            : _guideUnobservableUnsafe ? SafetyCause.GuideUnobservable
            : _agentLostUnsafe ? SafetyCause.AgentLost
            : _staleUnsafe ? SafetyCause.StaleTelemetry
            : _cloudUnsafe ? SafetyCause.Cloud
            : SafetyCause.None;

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
            _guideJudge = null;
            _judgeRedecide = true;
            JudgeChangedThisTick = false;
            LastCloudFromGuide = false;
            LastCause = SafetyCause.None;
            LastTickSummary = "";
        }

        public SafetyDecision Evaluate(AgentStatusSnapshot snap, ISafetySettings settings)
        {
            bool prevUnsafe = AnyUnsafe;
            bool prevStar = _starLostUnsafe, prevCloud = _cloudUnsafe,
                 prevStale = _staleUnsafe, prevLost = _agentLostUnsafe,
                 prevGuide = _guideUnobservableUnsafe;
            JudgeChangedThisTick = false;

            if (!snap.AgentReachable)
            {
                // §126-bis — al ritorno l'Agente puo' essere un altro (riavviato,
                // un'altra versione): il giudice si ridecide.
                _judgeRedecide = true;
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

            // §126 — chi giudica il cielo. §126-bis: il giudice EFFETTIVO.
            DecideJudge(snap, settings);
            bool cameraJudge = _guideJudge != true;
            bool guideSkyJudge = !cameraJudge && settings.CloudSafetyEnabled;

            // §126-bis — sicurezza nubi spenta: nessun latch del cielo deve restare
            // acceso (prima restava per sempre, anche nel 1.13).
            if (!settings.CloudSafetyEnabled)
            {
                _cloudUnsafe = false;
                _cloudDegradation = 0.0;
                _fastDegradation = 0.0;
            }

            // ---- Trasferimento latch STALE/AGENT_LOST al ritorno dei dati ----
            // Il rientro non e' gratis: satura il degrado CLOUD => servono ClearSafePolls di
            // evidenza CLEAR per tornare SAFE (isteresi di recupero unica e coerente).
            if (cameraJudge)
            {
                if ((_staleUnsafe && fresh) || (_agentLostUnsafe && fresh && settings.CloudSafetyEnabled))
                {
                    _cloudDegradation = Math.Max(_cloudDegradation, Math.Max(1, settings.CloudUnsafePolls));
                    _cloudUnsafe = true;
                    _staleUnsafe = false;
                    _agentLostUnsafe = false;
                }
            }
            else
            {
                // §126 — senza camera giudice STALE non esiste (opzione cambiata a latch
                // acceso: si spegne). AGENT_LOST al ritorno dell'Agente passa al percorso
                // del cielo della guida, saturato: il SAFE lo concede la guida quando
                // conferma il sereno, non il solo fatto che l'Agente risponda di nuovo.
                _staleUnsafe = false;
                _staleStreakPolls = 0;
                if (_agentLostUnsafe && guideSkyJudge)
                {
                    SaturateGuideSky(settings);
                    _agentLostUnsafe = false;
                }
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
            if (snap.StarLost is bool lostNow)
            {
                // §126 — Agente 3.1: la stella persa e' misurata sugli EVENTI di PHD2
                // (StarLost la apre; GuideStep, passo di calibrazione o nuova stella la
                // chiudono) e porta la propria durata. Due difetti chiusi:
                //   • il 10/8 un frame perso alle 22:32:03, poi 126 frame buoni, e UNSAFE
                //     alle 22:34:55: lo stato del controller usciva solo con l'RMS fuori
                //     dalla banda neutra;
                //   • la durata non dipende piu' dalla cadenza dei poll.
                // NON si congela a guida ferma: se la guida si ferma mentre la stella e'
                // persa e nessuno la rivede, l'ultima osservazione e' "stella persa" e
                // tale resta (il 24/8 l'allarme nato prima della calibrazione era con ogni
                // probabilita' giusto: nubi). Congelarla accecherebbe il monitor.
                EvaluateStarLostFromEvents(snap, settings, lostNow, fresh, cameraJudge, guideSkyJudge);
            }
            else if (isStarLost)
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
                if (_guideSilenceAccum <= 0.0 && _guideUnobservableUnsafe)
                {
                    _guideUnobservableUnsafe = false;
                    // §126-bis — come STAR_LOST e AGENT_LOST: il canale che riparla
                    // non e' prova di sereno. Il SAFE lo concede la guida.
                    if (guideSkyJudge) { SaturateGuideSky(settings); }
                }
            }
            else
            {
                // Guida non attesa (pausa annunciata), latch spento o Agente che non
                // espone il blocco §68: nessun allarme. La pausa legittima non lascia
                // strascichi; un latch GIA' acceso pero' non si spegne gratis: con la
                // guida giudice passa al cielo della guida (§126-bis).
                _guideSilenceAccum = 0.0;
                if (_guideUnobservableUnsafe)
                {
                    _guideUnobservableUnsafe = false;
                    if (guideSkyJudge) { SaturateGuideSky(settings); }
                }
            }

            // ---- Condizione 2: STALE (fix Bug B) ----
            if (fresh)
            {
                _staleStreakPolls = 0;
            }
            else if (cameraJudge && settings.CloudSafetyEnabled && settings.StaleUnsafeEnabled
                     && _lastKnownGuidingActive && LastContextDegraded(settings))
            {
                // fresh=false significa GIA' "oltre la finestra adattiva §43" (il gap normale
                // tra pose e' dentro la finestra: nessun falso allarme sul buco tra sub).
                _staleStreakPolls++;
                if (_staleStreakPolls >= Math.Max(1, settings.StaleUnsafePolls)) { _staleUnsafe = true; }
            }
            // NB: lo stantio NON azzera piu' il degrado accumulato (prima: reset silenzioso).

            // ---- Condizione 3: CLOUD ----
            // §126 — giudice = la guida: valutata a OGNI tick, la camera non entra.
            if (guideSkyJudge)
            {
                EvaluateGuideSky(snap, settings);
            }
            // §76 — il gate si apre anche col SOLO sensore veloce: la SNR di guida
            // arriva da PHD2, non da NINA, quindi non dipende dalla freschezza della
            // telemetria di ripresa. E' proprio il caso in cui e' l'unico che parla.
            else if (cameraJudge && settings.CloudSafetyEnabled
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
            else if (cameraJudge && settings.CloudSafetyEnabled && !settings.UseIndexCloudLogic)
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
        /// §126 — STAR_LOST dagli eventi di PHD2 (Agente 3.1). Ingresso: stella persa di
        /// fila per StarLostConsolidationSeconds. Uscita: stella di nuovo tracciata per
        /// ResumeTicks; con la guida giudice del cielo il latch passa al percorso del
        /// cielo, saturato, cosi' il SAFE arriva solo quando la guida conferma anche il
        /// sereno (il 3/8 la stella tornava a SNR 0.28-0.49 del riferimento, nubi a tratti).
        /// </summary>
        private void EvaluateStarLostFromEvents(AgentStatusSnapshot snap, ISafetySettings settings,
            bool lostNow, bool fresh, bool cameraJudge, bool guideSkyJudge)
        {
            if (lostNow)
            {
                _starLostStreakTicks++;
                _normalStreakTicks = 0;
                double lostFor = snap.StarLostS ?? 0.0;
                if (lostFor >= Math.Max(1, settings.StarLostConsolidationSeconds)) { _starLostUnsafe = true; }
                return;
            }
            if (!snap.StarTracked)
            {
                // Ne' persa ne' tracciata ADESSO (nessun frame recente): nessuna
                // evidenza in nessun verso, i contatori restano dove sono.
                return;
            }
            _starLostStreakTicks = 0;
            if (!_starLostUnsafe && !_agentLostUnsafe) { return; }
            _normalStreakTicks++;
            if (_normalStreakTicks < ResumeTicks) { return; }
            if (_starLostUnsafe)
            {
                _starLostUnsafe = false;
                if (guideSkyJudge) { SaturateGuideSky(settings); }
            }
            // AGENT_LOST senza percorso del cielo che lo raccolga: la guida tornata
            // tracciata e' l'evidenza di rientro (con la camera giudice: solo senza
            // trasparenza fresca, come prima).
            if (_agentLostUnsafe && (!cameraJudge || !fresh)) { _agentLostUnsafe = false; }
        }

        /// <summary>
        /// §126 — il percorso del cielo con la GUIDA come giudice. Un solo accumulatore
        /// (_fastDegradation, tetto SkyDegradingUnsafePolls):
        ///   • guida non attesa (stop, calibrazione, Guide Assistant, autofocus): congelato —
        ///     niente evidenza, niente decisione in nessun verso;
        ///   • SkyDegrading (SNR &lt;= 50% del riferimento per 90 s, misurato dall'Agente):
        ///     +1 per poll;
        ///   • SkyOk (SNR &gt;= 80% per 60 s) E canale pronto (§71: stella tracciata in modo
        ///     stabile, frame recenti): drena di tetto/ClearSafePolls per poll;
        ///   • in mezzo: invariato (isteresi).
        /// UNSAFE a tetto, SAFE a zero. La camera di ripresa non entra.
        /// </summary>
        private void EvaluateGuideSky(AgentStatusSnapshot snap, ISafetySettings settings)
        {
            if (!snap.GuidingExpected) { return; }

            int fastCap = Math.Max(1, settings.SkyDegradingUnsafePolls);
            int clearPolls = Math.Max(1, settings.ClearSafePolls);
            double fastDrain = Math.Max(1.0, (double)fastCap / clearPolls);

            // §126-bis — con la guida giudice il crollo della stella e' l'UNICA via per
            // le nubi: la casella "dichiara unsafe prima" (SkyDegradingAccumulate,
            // nata come acceleratore della camera) non deve poterla spegnere. A
            // spegnere il percorso del cielo resta "Sicurezza nubi attiva".
            bool degrading = snap.SkyDegrading;
            // Canale pronto assente (Agente vecchio) => non blocca: decide SkyOk.
            bool clear = snap.SkyOk && snap.ChannelReady != false;

            if (degrading)
            {
                _fastDegradation = Math.Min(fastCap, _fastDegradation + 1.0);
            }
            else if (clear)
            {
                _fastDegradation = Math.Max(0.0, _fastDegradation - fastDrain);
            }

            if (_fastDegradation >= fastCap) { _cloudUnsafe = true; }
            if (_fastDegradation <= 0.0 && _cloudUnsafe) { _cloudUnsafe = false; }
        }

        /// <summary>
        /// §126-bis — decide il giudice effettivo: al primo /status valido, dopo un Agente
        /// perso, o se l'opzione cambia. Se cambia a latch acceso il latch si TRASFERISCE
        /// (verso la guida: cielo saturato; verso la camera: degrado lento saturato),
        /// cosi' nessun cambio di giudice spegne un UNSAFE senza evidenza.
        /// </summary>
        private void DecideJudge(AgentStatusSnapshot snap, ISafetySettings settings)
        {
            bool option = settings.ImagingCameraUnsafeEnabled;
            if (_guideJudge.HasValue && !_judgeRedecide && option == _judgeOptionAtDecision) { return; }
            bool nuovo = !option && snap.GuideJudgeReady;
            if (_guideJudge.HasValue && _guideJudge.Value != nuovo && _cloudUnsafe)
            {
                if (nuovo) { SaturateGuideSky(settings); }
                else { _cloudDegradation = Math.Max(_cloudDegradation, Math.Max(1, settings.CloudUnsafePolls)); }
            }
            if (nuovo)
            {
                // §126-quater — anche un latch STALE acceso non si spegne gratis: passa
                // al cielo della guida (come _cloudUnsafe sopra).
                if (_staleUnsafe) { SaturateGuideSky(settings); }
                _staleUnsafe = false;
                _staleStreakPolls = 0;
            }
            JudgeChangedThisTick = !_guideJudge.HasValue || _guideJudge.Value != nuovo;
            _guideJudge = nuovo;
            _judgeOptionAtDecision = option;
            _judgeRedecide = false;
        }

        /// <summary>§126 — porta il percorso del cielo della guida a UNSAFE pieno: da qui
        /// si esce solo con l'evidenza di sereno della guida.</summary>
        private void SaturateGuideSky(ISafetySettings settings)
        {
            _fastDegradation = Math.Max(_fastDegradation, Math.Max(1, settings.SkyDegradingUnsafePolls));
            _cloudDegradation = 0.0;
            _cloudUnsafe = true;
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

        /// <summary>§126 — true se il latch del cielo e' stato acceso dal percorso della
        /// GUIDA (per il messaggio di log: "segnale della stella" vs "trasparenza").</summary>
        public bool LastCloudFromGuide { get; private set; }

        private SafetyDecision Transition(bool prevUnsafe,
            bool prevStar, bool prevCloud, bool prevStale, bool prevLost, bool prevGuide)
        {
            bool nowUnsafe = AnyUnsafe;
            if (nowUnsafe && !prevUnsafe)
            {
                // Causa = il latch scattato in QUESTO tick (in ordine di specificita').
                // §126-bis — dal giudice effettivo, non dagli accumulatori.
                LastCloudFromGuide = _cloudUnsafe && _guideJudge == true;
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
                $"err={snap.GuideStarErrorsRecent} alert={B(snap.GuideAlertSevere)} " +
                $"lost={(snap.StarLost is bool sl ? B(sl) : "-")}/{snap.StarLostS?.ToString("0", ic) ?? "-"}s " +
                $"trk={B(snap.StarTracked)} deg={B(snap.SkyDegrading)} ok={B(snap.SkyOk)} " +
                $"ready={(snap.ChannelReady is bool cr ? B(cr) : "-")}] " +
                $"judge={(_guideJudge == true ? "GUIDE" : "CAMERA")} " +
                $"jready={B(snap.GuideJudgeReady)} | " +
                $"latch[star={B(_starLostUnsafe)} cloud={B(_cloudUnsafe)} stale={B(_staleUnsafe)} " +
                $"lost={B(_agentLostUnsafe)} guide={B(_guideUnobservableUnsafe)}]";
            static string B(bool b) => b ? "1" : "0";
        }
    }
}
