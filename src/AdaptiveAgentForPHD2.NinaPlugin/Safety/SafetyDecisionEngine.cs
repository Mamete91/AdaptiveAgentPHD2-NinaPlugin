#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using System;
using System.Globalization;

namespace AdaptiveAgentForPHD2.NinaPlugin.Safety
{
    /// <summary>Esito di una valutazione del decision engine. Le transizioni sono gli unici eventi notificati.</summary>
    public enum SafetyDecision { NoChange, BecameUnsafe, BecameSafe }

    /// <summary>Causa dell'ultima transizione UNSAFE (per log/notifica distinti).</summary>
    public enum SafetyCause { None, StarLost, Cloud, AgentLost, GuideUnobservable }

    /// <summary>
    /// Motore di decisione del Safety Monitor: riceve uno snapshot di /status per tick e
    /// decide se la sessione e' diventata unsafe o safe.
    ///
    /// §126 (1.14) — LA CAMERA DI GUIDA GIUDICA IL CIELO, in entrambi i versi; la camera di
    /// ripresa e' informativa (notte 3-4/10: una posa con stelle allungate, indice 0.457,
    /// ha fermato la sequenza tre ore con la stella di guida a SNR 66.3 su 65.5).
    /// §128 (1.15) — il giudizio della camera di ripresa (percorso CLOUD sull'indice di
    /// trasparenza, latch STALE, cambio di giudice) e' stato TOLTO: dal 1.14 era spento di
    /// default e nessuna notte lo usava. Ritorno possibile solo reinstallando il 1.14.3.0.
    ///
    /// Condizioni (OR dei latch):
    ///   STAR_LOST           stella persa misurata dagli EVENTI di PHD2 (Agente 3.1) per
    ///                       StarLostConsolidationSeconds; con un Agente piu' vecchio, lo
    ///                       stato STAR_LOST del controller consolidato a tick (§65).
    ///   CLOUD               cielo dalla guida: SkyDegrading (SNR &lt;= 50% del riferimento
    ///                       per 90 s) accumula +1/poll fino a SkyDegradingUnsafePolls;
    ///                       SkyOk + canale pronto drena di tetto/ClearSafePolls; congelato a
    ///                       guida non attesa (calibrazione, Guide Assistant, autofocus).
    ///   AGENT_LOST          Agente irraggiungibile a sessione attiva (fix N6, Bug C).
    ///   GUIDE_UNOBSERVABLE  canale di guida muto mentre la guida e' attesa (§68).
    ///
    /// Rientro: al rilascio di STAR_LOST, AGENT_LOST e GUIDE_UNOBSERVABLE il latch passa al
    /// cielo della guida, saturato: il SAFE lo concede la guida quando conferma il sereno,
    /// non il solo ritorno dei dati (§126-bis).
    ///
    /// Giudice del cielo disponibile solo se l'Agente lo dichiara (`giudice_pronto`,
    /// Agente 3.1+ con il sensore del cielo acceso); deciso una volta per collegamento.
    /// Senza giudice il percorso CLOUD e' spento come con "Sicurezza nubi" disattivata:
    /// restano stella persa, canale muto e Agente perso.
    /// </summary>
    public sealed class SafetyDecisionEngine
    {
        public const int ResumeTicks = 3; // ~45s al default 15s

        // STAR_LOST
        private int _starLostStreakTicks;
        private int _normalStreakTicks;
        private bool _starLostUnsafe;

        // CLOUD — il cielo visto dalla guida: un solo accumulatore, [0, SkyDegradingUnsafePolls].
        private double _skyDegradation;
        private bool _cloudUnsafe;

        // AGENT_LOST (fix N6)
        private int _agentLostStreakPolls;
        private bool _agentLostUnsafe;

        // §68 — GUIDE_UNOBSERVABLE: accumulatore LEAKY (non streak consecutivo: e' la
        // lezione del Bug A §55 — un paio di campioni "buoni" in mezzo a un degrado non
        // devono azzerare il contatore).
        private double _guideSilenceAccum;
        private bool _guideUnobservableUnsafe;

        // §126-bis — il giudice del cielo, deciso una volta per collegamento all'Agente (al
        // primo /status valido dopo Connect o dopo un Agente perso): deciso poll per poll,
        // un payload transitorio accenderebbe e spegnerebbe il percorso del cielo.
        private bool? _skyJudge;
        private bool _judgeRedecide = true;

        // Memoria di sessione: la guida era attiva all'ultimo /status valido (AGENT_LOST).
        private bool _lastKnownGuidingActive;

        /// <summary>Causa dell'ultima transizione UNSAFE (per log/notifica distinti).</summary>
        public SafetyCause LastCause { get; private set; } = SafetyCause.None;

        /// <summary>Riga diagnostica dell'ultimo tick (input + contatori + latch), per il log (§3).</summary>
        public string LastTickSummary { get; private set; } = "";

        /// <summary>§126-bis — l'Agente fa da giudice del cielo (sensore della guida attivo).</summary>
        public bool GuideJudgeActive => _skyJudge == true;

        /// <summary>§126-bis — il giudice e' stato deciso (almeno un /status valido).</summary>
        public bool JudgeDecided => _skyJudge.HasValue;

        /// <summary>§126-bis — il giudice e' stato (ri)deciso in questo tick: il monitor lo scrive nel log.</summary>
        public bool JudgeChangedThisTick { get; private set; }

        /// <summary>§126-bis — la causa ATTUALE (il latch ancora acceso), non quella di
        /// ingresso: dopo una stella ritrovata il latch passa al cielo della guida e la
        /// dashboard deve dire "si aspetta il sereno", non "stella persa".</summary>
        public SafetyCause CurrentCause =>
            _starLostUnsafe ? SafetyCause.StarLost
            : _guideUnobservableUnsafe ? SafetyCause.GuideUnobservable
            : _agentLostUnsafe ? SafetyCause.AgentLost
            : _cloudUnsafe ? SafetyCause.Cloud
            : SafetyCause.None;

        /// <summary>Riporta il motore allo stato iniziale. Chiamato su Connect/Disconnect ESPLICITI.</summary>
        public void Reset()
        {
            _starLostStreakTicks = 0;
            _normalStreakTicks = 0;
            _starLostUnsafe = false;
            _skyDegradation = 0;
            _cloudUnsafe = false;
            _agentLostStreakPolls = 0;
            _agentLostUnsafe = false;
            _guideSilenceAccum = 0;
            _guideUnobservableUnsafe = false;
            _lastKnownGuidingActive = false;
            _skyJudge = null;
            _judgeRedecide = true;
            JudgeChangedThisTick = false;
            LastCause = SafetyCause.None;
            LastTickSummary = "";
        }

        public SafetyDecision Evaluate(AgentStatusSnapshot snap, ISafetySettings settings)
        {
            bool prevUnsafe = AnyUnsafe;
            bool prevStar = _starLostUnsafe, prevCloud = _cloudUnsafe,
                 prevLost = _agentLostUnsafe, prevGuide = _guideUnobservableUnsafe;
            JudgeChangedThisTick = false;

            if (!snap.AgentReachable)
            {
                // §126-bis — al ritorno l'Agente puo' essere un altro (riavviato, un'altra
                // versione): il giudice si ridecide.
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
                return Transition(prevUnsafe, prevStar, prevCloud, prevLost, prevGuide);
            }

            // Agente di nuovo raggiungibile: il contatore rientra; il latch AGENT_LOST viene
            // gestito piu' sotto (al cielo della guida, o rientrato con la stella tracciata).
            _agentLostStreakPolls = 0;

            if (!snap.IsValid)
            {
                // Payload raggiungibile ma incompleto/malformato (transitorio): mantieni tutto.
                BuildSummary(snap, settings);
                return Transition(prevUnsafe, prevStar, prevCloud, prevLost, prevGuide);
            }

            // Memoria di sessione: l'unico stato "non attivo" e' INACTIVE (guida ferma volontaria).
            _lastKnownGuidingActive = snap.GuidingState != "INACTIVE";

            DecideJudge(snap);
            bool skyJudge = _skyJudge == true && settings.CloudSafetyEnabled;

            // Senza giudice del cielo (sicurezza nubi spenta, o Agente che non puo' farlo)
            // nessun latch del cielo deve restare acceso.
            if (!skyJudge)
            {
                _cloudUnsafe = false;
                _skyDegradation = 0.0;
            }

            // AGENT_LOST al ritorno dell'Agente passa al cielo della guida, saturato: il SAFE
            // lo concede la guida quando conferma il sereno, non il fatto che l'Agente
            // risponda di nuovo. Senza giudice rientra con la stella tracciata (sotto).
            if (_agentLostUnsafe && skyJudge)
            {
                SaturateGuideSky(settings);
                _agentLostUnsafe = false;
            }

            // ---- STAR_LOST ----
            if (snap.StarLost is bool lostNow)
            {
                EvaluateStarLostFromEvents(snap, settings, lostNow, skyJudge);
            }
            else
            {
                EvaluateStarLostFromState(snap, settings);
            }

            // ---- GUIDE_UNOBSERVABLE (§68) ----
            // Domanda diversa da STAR_LOST: non "la stella e' persa?" ma "posso ancora
            // FIDARMI del canale di guida?". Il 26/7 PHD2 ha smesso di parlare e
            // `guiding_state` e' rimasto congelato su CRITICAL.
            //
            // Gate: la guida deve essere ATTESA. Le pause legittime (flip, autofocus, stop
            // volontario) PHD2 le ANNUNCIA; sui guasti tace.
            // S1 (fail-safe): il silenzio del canale oltre la soglia.
            // S2 (corroborazione): Alert PHD2 warning/error o raffica di ErrorCode
            // per-frame DIMEZZANO la soglia — possono solo ANTICIPARE, mai decidere da soli.
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
                    // §126-bis — come STAR_LOST e AGENT_LOST: il canale che riparla non e'
                    // prova di sereno. Il SAFE lo concede la guida.
                    if (skyJudge) { SaturateGuideSky(settings); }
                }
            }
            else
            {
                // Guida non attesa (pausa annunciata), latch spento o Agente che non espone
                // il blocco §68: nessun allarme. Un latch GIA' acceso pero' non si spegne
                // gratis: passa al cielo della guida (§126-bis).
                _guideSilenceAccum = 0.0;
                if (_guideUnobservableUnsafe)
                {
                    _guideUnobservableUnsafe = false;
                    if (skyJudge) { SaturateGuideSky(settings); }
                }
            }

            // ---- CLOUD: il cielo dalla guida ----
            if (skyJudge)
            {
                EvaluateGuideSky(snap, settings);
            }

            BuildSummary(snap, settings);
            return Transition(prevUnsafe, prevStar, prevCloud, prevLost, prevGuide);
        }

        /// <summary>
        /// §126 — STAR_LOST dagli eventi di PHD2 (Agente 3.1). Ingresso: stella persa di fila
        /// per StarLostConsolidationSeconds (la durata la misura l'Agente: non dipende dalla
        /// cadenza dei poll; il 10/8 un frame perso con lo stato del controller restava
        /// appiccicato). NON si congela a guida ferma: se la guida si ferma a stella persa,
        /// l'ultima osservazione e' "stella persa" e tale resta (24/8). Uscita: stella di
        /// nuovo tracciata per ResumeTicks; il latch passa al cielo della guida, saturato,
        /// cosi' il SAFE arriva solo quando la guida conferma anche il sereno (3/8: la stella
        /// tornava a SNR 0.28-0.49 del riferimento, nubi a tratti).
        /// </summary>
        private void EvaluateStarLostFromEvents(AgentStatusSnapshot snap, ISafetySettings settings,
            bool lostNow, bool skyJudge)
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
                // Ne' persa ne' tracciata ADESSO (nessun frame recente): nessuna evidenza
                // in nessun verso, i contatori restano dove sono.
                return;
            }
            _starLostStreakTicks = 0;
            if (!_starLostUnsafe && !_agentLostUnsafe) { return; }
            _normalStreakTicks++;
            if (_normalStreakTicks < ResumeTicks) { return; }
            if (_starLostUnsafe)
            {
                _starLostUnsafe = false;
                if (skyJudge) { SaturateGuideSky(settings); }
            }
            // AGENT_LOST senza cielo della guida che lo raccolga: la stella tornata tracciata
            // e' l'evidenza di rientro.
            if (_agentLostUnsafe) { _agentLostUnsafe = false; }
        }

        /// <summary>
        /// STAR_LOST quando lo /status non porta la stella persa misurata sugli eventi:
        /// Agente prima del 3.1, oppure un tick in cui il blocco della guida manca (server.py
        /// lo sostituisce con {"enabled": false} se guide_health fallisce) — anche con il
        /// giudice guida gia' deciso. Lo stato STAR_LOST del controller, consolidato a tick.
        /// Comportamento IDENTICO al 1.14.3 (revisione §128): al rilascio la stella non passa
        /// al cielo della guida. §65 — si esce con la
        /// guida OPERATIVA (qualunque stato tranne STAR_LOST e INACTIVE: il complemento esatto
        /// del criterio d'ingresso), non con la guida eccellente: il 19/7 il rientro dipendeva
        /// da un tuffo casuale dell'RMS. GuidingState null (payload incompleto) non conta come
        /// operativo: fail-safe.
        /// </summary>
        private void EvaluateStarLostFromState(AgentStatusSnapshot snap, ISafetySettings settings)
        {
            bool isStarLost = snap.GuidingState == "STAR_LOST";
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
                        // AGENT_LOST: con il cielo della guida e' gia' passato al cielo in
                        // Evaluate; qui resta solo senza giudice (o con le nubi spente), e
                        // rientra con la guida operativa — non piu' legato alla freschezza
                        // della telemetria della camera (§128).
                        _agentLostUnsafe = false;
                    }
                }
            }
            else
            {
                _starLostStreakTicks = 0;
                _normalStreakTicks = 0;
            }
        }

        /// <summary>
        /// §126 — il percorso del cielo. Un solo accumulatore (_skyDegradation, tetto
        /// SkyDegradingUnsafePolls):
        ///   • guida non attesa (stop, calibrazione, Guide Assistant, autofocus): congelato —
        ///     niente evidenza, niente decisione in nessun verso;
        ///   • SkyDegrading (SNR &lt;= 50% del riferimento per 90 s, misurato dall'Agente):
        ///     +1 per poll;
        ///   • SkyOk (SNR &gt;= 80% per 60 s, 5 minuti dopo un cambio in UNSAFE) E canale
        ///     pronto (§71: stella tracciata in modo stabile, frame recenti): drena di
        ///     tetto/ClearSafePolls per poll;
        ///   • in mezzo: invariato (isteresi).
        /// UNSAFE a tetto, SAFE a zero.
        /// </summary>
        private void EvaluateGuideSky(AgentStatusSnapshot snap, ISafetySettings settings)
        {
            if (!snap.GuidingExpected) { return; }

            int cap = Math.Max(1, settings.SkyDegradingUnsafePolls);
            int clearPolls = Math.Max(1, settings.ClearSafePolls);
            double drain = Math.Max(1.0, (double)cap / clearPolls);

            // Canale pronto assente (Agente vecchio) => non blocca: decide SkyOk.
            bool clear = snap.SkyOk && snap.ChannelReady != false;

            if (snap.SkyDegrading)
            {
                _skyDegradation = Math.Min(cap, _skyDegradation + 1.0);
            }
            else if (clear)
            {
                _skyDegradation = Math.Max(0.0, _skyDegradation - drain);
            }

            if (_skyDegradation >= cap) { _cloudUnsafe = true; }
            if (_skyDegradation <= 0.0 && _cloudUnsafe) { _cloudUnsafe = false; }
        }

        /// <summary>§126-bis — decide se l'Agente fa da giudice del cielo: al primo /status
        /// valido e dopo un Agente perso.</summary>
        private void DecideJudge(AgentStatusSnapshot snap)
        {
            if (_skyJudge.HasValue && !_judgeRedecide) { return; }
            bool nuovo = snap.GuideJudgeReady;
            JudgeChangedThisTick = !_skyJudge.HasValue || _skyJudge.Value != nuovo;
            _skyJudge = nuovo;
            _judgeRedecide = false;
        }

        /// <summary>§126 — porta il cielo della guida a UNSAFE pieno: da qui si esce solo con
        /// l'evidenza di sereno della guida.</summary>
        private void SaturateGuideSky(ISafetySettings settings)
        {
            _skyDegradation = Math.Max(_skyDegradation, Math.Max(1, settings.SkyDegradingUnsafePolls));
            _cloudUnsafe = true;
        }

        private bool AnyUnsafe => _starLostUnsafe || _cloudUnsafe || _agentLostUnsafe || _guideUnobservableUnsafe;

        private SafetyDecision Transition(bool prevUnsafe,
            bool prevStar, bool prevCloud, bool prevLost, bool prevGuide)
        {
            bool nowUnsafe = AnyUnsafe;
            if (nowUnsafe && !prevUnsafe)
            {
                // Causa = il latch scattato in QUESTO tick (in ordine di specificita').
                LastCause = (_starLostUnsafe && !prevStar) ? SafetyCause.StarLost
                          : (_cloudUnsafe && !prevCloud) ? SafetyCause.Cloud
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

        /// <summary>§3 osservabilita' — una riga per tick con input, contatori e latch. La
        /// trasparenza delle pose e' solo informazione (per le forensi), non entra nella decisione.</summary>
        private void BuildSummary(AgentStatusSnapshot snap, ISafetySettings settings)
        {
            var ic = CultureInfo.InvariantCulture;
            string idx = snap.TransparencyIndex?.ToString("0.00", ic) ?? "-";
            string judge = _skyJudge == true ? "GUIDE" : _skyJudge == false ? "NONE" : "?";
            LastTickSummary =
                $"reachable={snap.AgentReachable} guiding={snap.GuidingState ?? "-"} " +
                $"transp(info)={snap.TransparencyState ?? "-"} idx={idx} | " +
                $"sky={_skyDegradation.ToString("0.#", ic)}/{settings.SkyDegradingUnsafePolls} " +
                $"starlost={_starLostStreakTicks} " +
                $"lost={_agentLostStreakPolls}/{settings.AgentLostUnsafePolls} " +
                $"guide[age={snap.GuideFrameAgeS?.ToString("0", ic) ?? "-"}s exp={B(snap.GuidingExpected)} " +
                $"acc={_guideSilenceAccum.ToString("0.#", ic)}/{settings.GuideUnobservablePolls} " +
                $"err={snap.GuideStarErrorsRecent} alert={B(snap.GuideAlertSevere)} " +
                $"lost={(snap.StarLost is bool sl ? B(sl) : "-")}/{snap.StarLostS?.ToString("0", ic) ?? "-"}s " +
                $"trk={B(snap.StarTracked)} deg={B(snap.SkyDegrading)} ok={B(snap.SkyOk)} " +
                $"ready={(snap.ChannelReady is bool cr ? B(cr) : "-")}] " +
                $"judge={judge} jready={B(snap.GuideJudgeReady)} | " +
                $"latch[star={B(_starLostUnsafe)} cloud={B(_cloudUnsafe)} " +
                $"lost={B(_agentLostUnsafe)} guide={B(_guideUnobservableUnsafe)}]";
            static string B(bool b) => b ? "1" : "0";
        }
    }
}
