#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using System;

namespace AdaptiveAgentForPHD2.NinaPlugin.Safety
{
    /// <summary>Esito di una valutazione del decision engine. Le transizioni sono gli unici eventi loggati/notificati.</summary>
    public enum SafetyDecision { NoChange, BecameUnsafe, BecameSafe }

    /// <summary>§49 — causa dell'ultima transizione UNSAFE (per log/notifica distinti).</summary>
    public enum SafetyCause { None, StarLost, Cloud }

    /// <summary>
    /// Motore di decisione del Safety Monitor v1.2. Riceve uno snapshot per tick e decide se la
    /// guida e' diventata unsafe/safe. Logica volutamente a singola condizione:
    ///
    ///   UNSAFE  <=  guiding_state == "STAR_LOST" consolidato per StarLostConsolidationSeconds
    ///               (default 5 minuti — alta evidenza: l'AI Star Finder dovrebbe recuperare entro
    ///               quel tempo; oltre e' un'emergenza vera).
    ///   SAFE    <=  guiding_state == "NORMAL" per 3 tick consecutivi (~45s al default 15s — soglia
    ///               bassa: appena la guida torna stabile vogliamo riprendere in fretta).
    ///
    /// L'asimmetria dei tempi (lento verso unsafe, rapido verso safe) e' intenzionale.
    /// Stati neutrali (INACTIVE/DEGRADED/CRITICAL/RECOVERING/altro) NON triggerano nulla e resettano
    /// il contatore STAR_LOST: siamo "fuori dall'emergenza" ma non ancora "ritorno confermato".
    /// Nessun'altra condizione (escalation_gate, saturation, RMS) entra qui: per design.
    /// </summary>
    public sealed class SafetyDecisionEngine
    {
        public const int ResumeTicks = 3; // ~45s al default 15s

        // STAR_LOST (v1.2, invariato)
        private int _starLostStreakTicks;   // tick consecutivi in STAR_LOST
        private int _normalStreakTicks;      // tick consecutivi in NORMAL (per il resume verso safe)
        private bool _starLostUnsafe;        // latch STAR_LOST

        // CLOUD (§49 N6): latch indipendente + isteresi asimmetrica in POLL.
        private int _cloudStreakPolls;
        private int _clearStreakPolls;
        private bool _cloudUnsafe;

        /// <summary>Causa dell'ultima transizione (per log/notifica distinti STAR_LOST vs CLOUD).</summary>
        public SafetyCause LastCause { get; private set; } = SafetyCause.None;

        /// <summary>Riporta il motore allo stato iniziale (safe, contatori azzerati). Chiamato su Connect/Disconnect.</summary>
        public void Reset()
        {
            _starLostStreakTicks = 0;
            _normalStreakTicks = 0;
            _starLostUnsafe = false;
            _cloudStreakPolls = 0;
            _clearStreakPolls = 0;
            _cloudUnsafe = false;
            LastCause = SafetyCause.None;
        }

        public SafetyDecision Evaluate(AgentStatusSnapshot snap, PluginSettings settings)
        {
            if (!snap.IsValid)
            {
                // Payload incompleto/non raggiungibile: mantieni lo stato precedente, non toccare i contatori.
                return SafetyDecision.NoChange;
            }

            bool prevUnsafe = _starLostUnsafe || _cloudUnsafe;

            // ---- Condizione 1: STAR_LOST (guida) — invariata ----
            bool isStarLost = snap.GuidingState == "STAR_LOST";
            bool isNormal = snap.GuidingState == "NORMAL";
            if (isStarLost)
            {
                _starLostStreakTicks++;
                _normalStreakTicks = 0;
                int consolidationTicks = Math.Max(1,
                    settings.StarLostConsolidationSeconds / settings.HealthCheckIntervalSeconds);
                if (_starLostStreakTicks >= consolidationTicks) { _starLostUnsafe = true; }
            }
            else if (isNormal)
            {
                _starLostStreakTicks = 0;
                if (_starLostUnsafe)
                {
                    _normalStreakTicks++;
                    if (_normalStreakTicks >= ResumeTicks) { _starLostUnsafe = false; }
                }
            }
            else
            {
                _starLostStreakTicks = 0;
                _normalStreakTicks = 0;
            }

            // ---- Condizione 2: CLOUD (trasparenza NINA) — §49 N6, ACCANTO a STAR_LOST ----
            // FAIL-SAFE: agisce SOLO su segnale positivo e FRESCO. Feature off, telemetria
            // assente o stantia (TransparencyFresh=false) => condizione nubi NEUTRA: contatori
            // azzerati, latch NON toccato (non forza né UNSAFE né SAFE). STAR_LOST resta backstop.
            if (settings.CloudSafetyEnabled && snap.TransparencyFresh && snap.TransparencyState != null)
            {
                bool isCloud = snap.TransparencyState == "CLOUD";
                bool isClearish = snap.TransparencyState == "CLEAR" || snap.TransparencyState == "HAZE";
                if (isCloud)
                {
                    // Lento verso UNSAFE: N poll consecutivi di CLOUD (una velatura breve non basta).
                    _cloudStreakPolls++;
                    _clearStreakPolls = 0;
                    if (_cloudStreakPolls >= Math.Max(1, settings.CloudUnsafePolls)) { _cloudUnsafe = true; }
                }
                else if (isClearish)
                {
                    // Recovery più rapido: M poll consecutivi di CLEAR/HAZE.
                    _cloudStreakPolls = 0;
                    if (_cloudUnsafe)
                    {
                        _clearStreakPolls++;
                        if (_clearStreakPolls >= Math.Max(1, settings.ClearSafePolls)) { _cloudUnsafe = false; }
                    }
                }
                // stato sconosciuto -> non tocca i latch
            }
            else
            {
                _cloudStreakPolls = 0;
                _clearStreakPolls = 0;
            }

            // ---- Stato complessivo = OR dei due latch ----
            bool nowUnsafe = _starLostUnsafe || _cloudUnsafe;
            if (nowUnsafe && !prevUnsafe)
            {
                LastCause = (_starLostUnsafe && isStarLost) ? SafetyCause.StarLost
                            : _cloudUnsafe ? SafetyCause.Cloud
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
    }
}
