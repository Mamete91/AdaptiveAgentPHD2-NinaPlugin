#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using System;
using System.Globalization;

namespace AdaptiveAgentForPHD2.NinaPlugin.Safety
{
    /// <summary>Esito di una valutazione del decision engine. Le transizioni sono gli unici eventi notificati.</summary>
    public enum SafetyDecision { NoChange, BecameUnsafe, BecameSafe }

    /// <summary>Causa dell'ultima transizione UNSAFE (per log/notifica distinti).</summary>
    public enum SafetyCause { None, StarLost, Cloud, StaleTelemetry, AgentLost }

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
    /// Recupero: al ritorno dei dati, i latch STALE/AGENT_LOST si trasferiscono sul percorso
    /// CLOUD (degrado saturato) =&gt; servono ClearSafePolls di evidenza CLEAR per tornare SAFE.
    /// Senza trasparenza disponibile, AGENT_LOST rientra con guida NORMAL per ResumeTicks.
    /// </summary>
    public sealed class SafetyDecisionEngine
    {
        public const int ResumeTicks = 3; // ~45s al default 15s

        // STAR_LOST (v1.2, invariato)
        private int _starLostStreakTicks;
        private int _normalStreakTicks;
        private bool _starLostUnsafe;

        // CLOUD — logica a indice (leaky) e logica legacy condividono il latch.
        private double _cloudDegradation;    // accumulatore leaky [0, CloudUnsafePolls]
        private int _cloudStreakPolls;       // solo logica legacy
        private int _clearStreakPolls;       // solo logica legacy
        private bool _cloudUnsafe;

        // STALE / AGENT_LOST (fix N6)
        private int _staleStreakPolls;
        private bool _staleUnsafe;
        private int _agentLostStreakPolls;
        private bool _agentLostUnsafe;

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
            _cloudStreakPolls = 0;
            _clearStreakPolls = 0;
            _cloudUnsafe = false;
            _staleStreakPolls = 0;
            _staleUnsafe = false;
            _agentLostStreakPolls = 0;
            _agentLostUnsafe = false;
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
                 prevStale = _staleUnsafe, prevLost = _agentLostUnsafe;

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
                return Transition(prevUnsafe, prevStar, prevCloud, prevStale, prevLost);
            }

            // Agente di nuovo raggiungibile: il contatore rientra; il latch AGENT_LOST viene
            // gestito piu' sotto (trasferito al percorso CLOUD o rientrato via guida NORMAL).
            _agentLostStreakPolls = 0;

            if (!snap.IsValid)
            {
                // Payload raggiungibile ma incompleto/malformato (transitorio): mantieni tutto.
                BuildSummary(snap, settings);
                return Transition(prevUnsafe, prevStar, prevCloud, prevStale, prevLost);
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

            // ---- Condizione 1: STAR_LOST (guida) — invariata ----
            bool isStarLost = snap.GuidingState == "STAR_LOST";
            bool isNormal = snap.GuidingState == "NORMAL";
            if (isStarLost)
            {
                _starLostStreakTicks++;
                _normalStreakTicks = 0;
                int consolidationTicks = Math.Max(1,
                    settings.StarLostConsolidationSeconds / Math.Max(1, settings.HealthCheckIntervalSeconds));
                if (_starLostStreakTicks >= consolidationTicks) { _starLostUnsafe = true; }
            }
            else if (isNormal)
            {
                _starLostStreakTicks = 0;
                if (_starLostUnsafe || _agentLostUnsafe)
                {
                    _normalStreakTicks++;
                    if (_normalStreakTicks >= ResumeTicks)
                    {
                        _starLostUnsafe = false;
                        // AGENT_LOST senza trasparenza disponibile: la guida tornata NORMAL
                        // e' l'evidenza di rientro (con trasparenza, governa il percorso CLOUD).
                        if (_agentLostUnsafe && !fresh) { _agentLostUnsafe = false; }
                    }
                }
            }
            else
            {
                _starLostStreakTicks = 0;
                _normalStreakTicks = 0;
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
            if (settings.CloudSafetyEnabled && fresh)
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
            return Transition(prevUnsafe, prevStar, prevCloud, prevStale, prevLost);
        }

        /// <summary>Fix Bug A: accumulatore leaky sull'indice. HAZE (zona intermedia) e' NEUTRA.</summary>
        private void EvaluateCloudByIndex(AgentStatusSnapshot snap, ISafetySettings settings)
        {
            double below = settings.CloudIndexAccumulateBelow;
            double above = Math.Max(settings.CloudIndexDrainAbove, below + 0.01);
            int cap = Math.Max(1, settings.CloudUnsafePolls);
            double drainRate = Math.Max(1.0,
                (double)cap / Math.Max(1, settings.ClearSafePolls));

            bool accumulate, drain;
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
            else
            {
                return; // nessun dato di trasparenza: non toccare nulla
            }

            if (accumulate)
            {
                _cloudDegradation = Math.Min(cap, _cloudDegradation + 1.0);
            }
            else if (drain)
            {
                _cloudDegradation = Math.Max(0.0, _cloudDegradation - drainRate);
            }
            // zona intermedia: nessuna variazione (il flicker CLOUD<->HAZE non azzera piu')

            if (_cloudDegradation >= cap) { _cloudUnsafe = true; }
            if (_cloudDegradation <= 0.0 && _cloudUnsafe) { _cloudUnsafe = false; }
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

        private bool AnyUnsafe => _starLostUnsafe || _cloudUnsafe || _staleUnsafe || _agentLostUnsafe;

        private SafetyDecision Transition(bool prevUnsafe,
            bool prevStar, bool prevCloud, bool prevStale, bool prevLost)
        {
            bool nowUnsafe = AnyUnsafe;
            if (nowUnsafe && !prevUnsafe)
            {
                // Causa = il latch scattato in QUESTO tick (in ordine di specificita').
                LastCause = (_starLostUnsafe && !prevStar) ? SafetyCause.StarLost
                          : (_cloudUnsafe && !prevCloud) ? SafetyCause.Cloud
                          : (_staleUnsafe && !prevStale) ? SafetyCause.StaleTelemetry
                          : (_agentLostUnsafe && !prevLost) ? SafetyCause.AgentLost
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
                $"starlost={_starLostStreakTicks} stale={_staleStreakPolls}/{settings.StaleUnsafePolls} " +
                $"lost={_agentLostStreakPolls}/{settings.AgentLostUnsafePolls} | " +
                $"latch[star={B(_starLostUnsafe)} cloud={B(_cloudUnsafe)} stale={B(_staleUnsafe)} lost={B(_agentLostUnsafe)}]";
            static string B(bool b) => b ? "1" : "0";
        }
    }
}
