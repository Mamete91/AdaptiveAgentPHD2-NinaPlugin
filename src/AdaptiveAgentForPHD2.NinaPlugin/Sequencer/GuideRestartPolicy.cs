#nullable enable
using System;

namespace AdaptiveAgentForPHD2.NinaPlugin.Sequencer
{
    /// <summary>
    /// §126-quater/sexies — logica PURA della sonda di guida (estratta per unit-test, come
    /// RecoveryProbeGate): durante un UNSAFE con la guida giudice, quando far ripartire la
    /// guida. Nessun mediator, nessun orologio: decide sui fatti che riceve.
    ///
    /// Tre motivi, nell'ordine:
    ///   1. la guida non e' attesa da almeno l'intervallo minimo -> StartGuiding;
    ///   2. la stella e' persa da almeno l'intervallo minimo -> Stop + Start (dopo una
    ///      nube lunga la stella puo' essere uscita dalla regione di ricerca di PHD2);
    ///   3. una volta per cadenza di UNSAFE con la guida che gira -> Stop + Start (il 3/10
    ///      un guasto della camera di guida si e' risolto a una ripartenza).
    /// Mai piu' spesso dell'intervallo minimo.
    ///
    /// §126-sexies — il motivo 3 si rimanda mentre la guida sta confermando il sereno
    /// (Agente 3.2: recovery_hint.sereno_in_conferma). Una ripartenza sospende la guida e la
    /// conferma riparte da zero; dopo un cambio di stella o di posa in UNSAFE la conferma
    /// dura 5 minuti, e una cadenza piu' corta non la lascerebbe mai finire: notte ferma a
    /// cielo sereno. Un guasto della camera di guida non da' sereno, quindi non e' toccato.
    /// Con un Agente piu' vecchio il campo manca (null) e vale la regola del 1.14.
    /// </summary>
    public static class GuideRestartPolicy
    {
        public const string ReasonNotRunning = "guiding is not running";

        public static (string? Reason, bool StopFirst) Decide(
            bool? guidingExpected,
            bool? starLost,
            double? starLostSeconds,
            bool? clearSkyConfirming,
            TimeSpan notExpectedFor,
            TimeSpan sinceLastRestart,
            bool minIntervalElapsed,
            TimeSpan minInterval,
            TimeSpan cadence)
        {
            if (!minIntervalElapsed)
            {
                return (null, false);
            }
            if (guidingExpected == false)
            {
                return notExpectedFor >= minInterval ? (ReasonNotRunning, false) : (null, false);
            }
            if (starLost == true && (starLostSeconds ?? 0) >= minInterval.TotalSeconds)
            {
                return ($"guide star lost for {starLostSeconds:0} s", true);
            }
            if (guidingExpected == true && sinceLastRestart >= cadence && clearSkyConfirming != true)
            {
                return ($"still unsafe after {cadence.TotalMinutes:0.#} min — restarting the guide channel", true);
            }
            return (null, false);
        }
    }
}
