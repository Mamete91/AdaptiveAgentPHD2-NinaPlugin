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

        // §64 — parametri del timeout AUTOMATICO (vedi AdaptiveTimeout).
        public const double AutoFloorSeconds = 60;      // mai piu' fitto di 1 min
        public const double AutoCeilingSeconds = 900;   // rete di sicurezza (col target e' inerte)
        public const double TargetSeconds = 180;        // §64-v2 — cap di latenza VALIDATO SUL CIELO (3 min)
        public const double FallbackWindowFloorSeconds = 180;    // §43 staleness_seconds
        public const double FallbackWindowExposureFactor = 1.5;  // §43 staleness_exposure_factor

        /// <summary>
        /// §64-v2 — cadenza fail-safe S1 calcolata invece che configurata.
        ///
        /// FUNZIONE DI COSTO (lessicografica, in quest'ordine):
        ///  1. MAI telemetria stantia (vincolo rigido di correttezza): il ciclo attesa+posa
        ///     deve chiudersi DENTRO la finestra di freschezza §43, altrimenti il latch
        ///     STALE puo' scattare e al ritorno dei dati RISATURA l'accumulatore nubi,
        ///     distruggendo il progresso di drain parziale tra le sonde.
        ///  2. Latenza di rientro cappata al target validato sul cielo (3 min di attesa
        ///     massima quando la finestra lo consente): la finestra e' un TETTO, non
        ///     un'uguaglianza — sedersi sul tetto (v1) allungava il recupero dei sub
        ///     lunghi senza alcun beneficio (600 s: 5 min di attesa contro i 3 validati).
        ///  3. Minimo numero di sonde soggetto a 1-2 (min-interval resta la leva utente
        ///     sui frame su disco; l'hint S2 puo' solo anticipare).
        ///
        ///     timeout = clamp( min(finestra − posa, 3 min), 1 min, 15 min )
        ///
        /// Esempi: posa 60 s -> 2 min (vincola la finestra); 300 s -> 2.5 min (finestra);
        /// 600 s -> 3 min (vincola il target — la v1 dava 5 min, il fisso da campo 3).
        ///
        /// `agentWindowSeconds` = `/status.nina.transparency.window_s` (§55): la finestra
        /// VERA dell'agente, non una copia della formula — se l'utente ritara
        /// `[nina_telemetry]`, la cadenza lo segue da sola. Se il valore non e' disponibile
        /// (agente offline, N1 spento, nessuna posa ancora inoltrata) si ricade sulla
        /// formula §43 con i default noti: nessun percorso resta senza cadenza.
        /// </summary>
        public static TimeSpan AdaptiveTimeout(double? agentWindowSeconds, double probeExposureSeconds)
        {
            var exposure = Math.Max(1.0, probeExposureSeconds);
            var window = (agentWindowSeconds is double w && w > 0 && !double.IsNaN(w))
                ? w
                : Math.Max(FallbackWindowFloorSeconds, FallbackWindowExposureFactor * exposure);
            // La finestra §43 e' sempre > posa (floor 180 s oppure 1.5x), quindi il margine
            // e' positivo per costruzione; i clamp restano come rete su config esotiche.
            var wait = Math.Min(window - exposure, TargetSeconds);
            return TimeSpan.FromSeconds(Math.Clamp(wait, AutoFloorSeconds, AutoCeilingSeconds));
        }

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
