#nullable enable
using System;

namespace AdaptiveAgentForPHD2.NinaPlugin.Sequencer
{
    /// <summary>
    /// §57 — logica PURA del gate posa-sonda (estratta per unit-test, come
    /// SafetyDecisionEngine). Decide quando l'istruzione WaitForRecoveryHint deve
    /// restituire il controllo al template (=> il TakeExposure builtin scatta la sonda).
    ///
    /// Paletti del Gate §57:
    ///   1. S1 e' l'autorita': il timeout apre SEMPRE il gate, hint o non hint.
    ///      S2 (hint) puo' solo ANTICIPARE, mai sopprimere ne' dichiarare safety.
    ///   3. probe_min_interval e' un floor ASSOLUTO: vale anche per l'hint e anche
    ///      per il timeout (un hint "ballerino" non martella l'otturatore).
    /// </summary>
    public static class RecoveryProbeGate
    {
        public const string ReasonHint = "recovery hint active (S2)";
        public const string ReasonTimeout = "probe timeout elapsed (S1)";

        /// <summary>
        /// Valuta il gate. `sinceStart` = tempo dall'inizio dell'attesa corrente;
        /// `sinceLastProbe` = tempo dall'ultima sonda (across iterazioni del loop).
        /// </summary>
        public static (bool Open, string Reason) Evaluate(
            TimeSpan sinceStart,
            TimeSpan sinceLastProbe,
            bool hintActive,
            TimeSpan timeout,
            TimeSpan minInterval)
        {
            if (sinceLastProbe < minInterval)
            {
                return (false, $"min-interval floor ({sinceLastProbe.TotalMinutes:0.0}/{minInterval.TotalMinutes:0.0} min)");
            }
            if (hintActive)
            {
                return (true, ReasonHint);
            }
            if (sinceStart >= timeout)
            {
                return (true, ReasonTimeout);
            }
            return (false, $"waiting ({sinceStart.TotalMinutes:0.0}/{timeout.TotalMinutes:0.0} min, hint inactive)");
        }
    }
}
