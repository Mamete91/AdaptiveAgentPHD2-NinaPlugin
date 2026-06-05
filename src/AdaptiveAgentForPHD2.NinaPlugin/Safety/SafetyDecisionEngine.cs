#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using System;

namespace AdaptiveAgentForPHD2.NinaPlugin.Safety
{
    /// <summary>Esito di una valutazione del decision engine. Le transizioni sono gli unici eventi loggati/notificati.</summary>
    public enum SafetyDecision { NoChange, BecameUnsafe, BecameSafe }

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

        private int _starLostStreakTicks;   // tick consecutivi in STAR_LOST
        private int _normalStreakTicks;      // tick consecutivi in NORMAL (per il resume verso safe)
        private bool _currentlyUnsafe;

        /// <summary>Riporta il motore allo stato iniziale (safe, contatori azzerati). Chiamato su Connect/Disconnect.</summary>
        public void Reset()
        {
            _starLostStreakTicks = 0;
            _normalStreakTicks = 0;
            _currentlyUnsafe = false;
        }

        public SafetyDecision Evaluate(AgentStatusSnapshot snap, PluginSettings settings)
        {
            if (!snap.IsValid)
            {
                // Payload incompleto/non raggiungibile: mantieni lo stato precedente, non toccare i contatori.
                return SafetyDecision.NoChange;
            }

            bool isStarLost = snap.GuidingState == "STAR_LOST";
            bool isNormal = snap.GuidingState == "NORMAL";

            if (isStarLost)
            {
                _starLostStreakTicks++;
                _normalStreakTicks = 0;
                // Math.Max(1, ...): evita trigger immediato se l'utente configura consolidamento < intervallo.
                int consolidationTicks = Math.Max(1,
                    settings.StarLostConsolidationSeconds / settings.HealthCheckIntervalSeconds);
                if (!_currentlyUnsafe && _starLostStreakTicks >= consolidationTicks)
                {
                    _currentlyUnsafe = true;
                    return SafetyDecision.BecameUnsafe;
                }
            }
            else if (isNormal && _currentlyUnsafe)
            {
                _normalStreakTicks++;
                _starLostStreakTicks = 0;
                if (_normalStreakTicks >= ResumeTicks)
                {
                    _currentlyUnsafe = false;
                    return SafetyDecision.BecameSafe;
                }
            }
            else
            {
                // INACTIVE, DEGRADED, CRITICAL, RECOVERING o altro stato neutro:
                // reset del contatore STAR_LOST (fuori dall'emergenza). Il contatore NORMAL
                // si resetta solo se NON siamo in NORMAL (un NORMAL "isolato" non deve perdere lo streak).
                _starLostStreakTicks = 0;
                if (!isNormal) { _normalStreakTicks = 0; }
            }

            return SafetyDecision.NoChange;
        }
    }
}
