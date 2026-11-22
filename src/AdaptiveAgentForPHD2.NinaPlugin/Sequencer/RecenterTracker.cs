#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using System;

namespace AdaptiveAgentForPHD2.NinaPlugin.Sequencer
{
    /// <summary>Risposta alla domanda "al ritorno del sereno serve un controllo della posizione?".</summary>
    public readonly record struct RecenterDecision(
        bool Check, bool FlipDuringUnsafe, double UnguidedMinutes, string Reason);

    /// <summary>
    /// §129 — la memoria dietro l'istruzione "Recenter if needed (Adaptive Agent)".
    ///
    /// Sorgente NINA 3.3.0.1070 (studio del 21/11/2026): sotto le nubi il flip si completa,
    /// ma il ricentraggio e' "best effort" (fallisce, avvisa e prosegue) e AfterMeridianFlip
    /// riporta comunque Success=true: il plugin non puo' sapere se il ricentraggio e'
    /// riuscito. Per questo l'istruzione non lo chiede a NINA: lo MISURA con un plate solve,
    /// e solo quando c'e' un motivo per dubitare della posizione:
    ///   • un flip di NINA avvenuto con il monitor internamente UNSAFE (la finestra
    ///     meridiano lo ha lasciato passare sotto le nubi);
    ///   • almeno N minuti senza guida durante l'unsafe (stella persa, guida ferma, Agente
    ///     irraggiungibile): la montatura ha inseguito senza correzioni e, alla ripartenza
    ///     su un nuovo aggancio, lo spostamento resta.
    /// I motivi si accumulano fino a un controllo RIUSCITO (Consume), anche attraverso piu'
    /// episodi di unsafe: la deriva non si azzera da sola. Logica PURA (orologio iniettato),
    /// thread-safe: il tick arriva dal timer del monitor, i flip dal thread di NINA,
    /// la lettura dall'istruzione della sequenza.
    /// </summary>
    public sealed class RecenterTracker
    {
        private readonly object _lock = new();
        private bool _monitorConnected;
        private bool _internalSafe = true;
        private double? _lastTickAt;
        private double _unguidedSeconds;
        private bool _flipDuringUnsafe;
        private NINA.Astrometry.Coordinates? _lastFlipTarget;

        /// <summary>Coordinate del bersaglio dell'ultimo flip (ripiego se il contesto non ne ha).</summary>
        public NINA.Astrometry.Coordinates? LastFlipTarget
        {
            get { lock (_lock) { return _lastFlipTarget; } }
        }

        /// <summary>Connect/Disconnect espliciti del monitor: si riparte da zero.</summary>
        public void Reset(bool monitorConnected)
        {
            lock (_lock)
            {
                _monitorConnected = monitorConnected;
                _internalSafe = true;
                _lastTickAt = null;
                _unguidedSeconds = 0;
                _flipDuringUnsafe = false;
                _lastFlipTarget = null;
            }
        }

        /// <summary>
        /// La guida corregge ADESSO? true = no (stella persa, guida ferma, nessuna notizia
        /// dall'Agente: prudente), false = si', null = payload incompleto (non si conta).
        /// GuidingExpected falso copre anche il flip, che ferma la guida.
        /// </summary>
        public static bool? IsUnguided(AgentStatusSnapshot snap)
        {
            if (!snap.AgentReachable) { return true; }
            if (!snap.IsValid) { return null; }
            if (snap.StarLost == true) { return true; }
            if (snap.StarLost == null && snap.GuidingState == "STAR_LOST") { return true; }
            if (!snap.GuidingExpected) { return true; }
            return false;
        }

        /// <summary>
        /// Un tick del monitor. Il tempo fra due tick si somma solo con il monitor
        /// internamente UNSAFE e la guida assente; un salto piu' lungo di maxStepSeconds
        /// (PC sospeso, monitor fermo) conta al massimo maxStepSeconds.
        /// </summary>
        public void OnTick(bool internalSafe, bool? unguided, double nowSeconds, double maxStepSeconds)
        {
            lock (_lock)
            {
                if (_lastTickAt is double prev && !internalSafe && unguided == true)
                {
                    _unguidedSeconds += Math.Clamp(nowSeconds - prev, 0.0, Math.Max(1.0, maxStepSeconds));
                }
                _lastTickAt = nowSeconds;
                _internalSafe = internalSafe;
            }
        }

        /// <summary>NINA inizia un flip (BeforeMeridianFlip).</summary>
        public void OnFlipStarted(bool internalSafe, NINA.Astrometry.Coordinates? target)
        {
            lock (_lock)
            {
                if (target != null) { _lastFlipTarget = target; }
                if (!internalSafe) { _flipDuringUnsafe = true; }
            }
        }

        /// <summary>NINA ha concluso il flip (AfterMeridianFlip, anche annullato).</summary>
        public void OnFlipFinished(bool internalSafe)
        {
            lock (_lock)
            {
                if (!internalSafe) { _flipDuringUnsafe = true; }
            }
        }

        /// <summary>Serve un controllo della posizione adesso?</summary>
        public RecenterDecision Decide(double unguidedMinutesThreshold)
        {
            lock (_lock)
            {
                double minutes = _unguidedSeconds / 60.0;
                if (!_monitorConnected)
                {
                    return new(false, _flipDuringUnsafe, minutes,
                        "the Sky Conditions monitor is not connected (nothing is tracked)");
                }
                if (!_internalSafe)
                {
                    // Dentro la finestra meridiano il monitor riporta SAFE ma il cielo non lo e':
                    // il plate solve fallirebbe e il flip sta per arrivare. Non si consuma nulla.
                    return new(false, _flipDuringUnsafe, minutes,
                        "conditions are not really safe yet (meridian protection window)");
                }
                if (_flipDuringUnsafe)
                {
                    return new(true, true, minutes,
                        "a meridian flip ran while unsafe — N.I.N.A.'s own recenter may have failed under clouds");
                }
                double threshold = Math.Max(1.0, unguidedMinutesThreshold);
                if (minutes >= threshold)
                {
                    return new(true, false, minutes,
                        $"{minutes:0.#} min without guiding while unsafe (threshold {threshold:0.#} min)");
                }
                return new(false, false, minutes,
                    $"only {minutes:0.#} min without guiding while unsafe (threshold {threshold:0.#} min) and no flip");
            }
        }

        /// <summary>Un controllo della posizione e' riuscito (centrato o gia' in tolleranza):
        /// i motivi accumulati sono chiusi.</summary>
        public void Consume()
        {
            lock (_lock)
            {
                _unguidedSeconds = 0;
                _flipDuringUnsafe = false;
            }
        }
    }
}
