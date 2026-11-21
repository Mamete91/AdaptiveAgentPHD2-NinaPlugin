#nullable enable
using System;

namespace AdaptiveAgentForPHD2.NinaPlugin.Safety
{
    /// <summary>Esito di un tick della protezione meridiano.</summary>
    public enum MeridianEvent { None, Opened, OpenedDuringFlip, ClosedFlipDone, ClosedTimeout, ClosedConditionsLost }

    /// <summary>
    /// §72 — MERIDIAN_PROTECTION: finestra limitata in cui il monitor riporta Safe
    /// per consentire la SOLA manovra meccanica del meridian flip durante un UNSAFE
    /// prolungato.
    ///
    /// PERCHE' (verificato su log 19/7 + sorgente MeridianFlipTrigger/MeridianFlipVM):
    /// alla deadline del flip con monitor unsafe, NINA non flippa e FERMA IL TRACKING
    /// ("Stopping tracking instead"); da li' il trigger si auto-esclude ("not tracking
    /// -> skip") e NESSUN percorso di codice riavvia il tracking. Conseguenza: stella
    /// di guida in deriva => STAR_LOST perenne; sonde su montatura ferma => campo
    /// strisciato => N1 CLOUD anche a cielo perfetto. Il monitor perde TUTTI gli occhi
    /// insieme: deadlock irreversibile senza operatore. Per un sistema che deve
    /// OSSERVARE per decidere (§55), una protezione che acceca l'osservatore e' essa
    /// stessa un guasto di sicurezza: questa finestra e' AUTOPROTEZIONE del monitor,
    /// non una deroga (decisione di Alessandro, 2026-08-04 — born-operative nel
    /// perimetro attuale, dove l'unico consumatore di IsSafe e' NINA; da rivalutare
    /// con tetti/cupole automatizzati).
    ///
    /// COSA NON E': non e' un ritorno a SAFE. I latch interni NON vengono toccati e
    /// continuano a valutare onestamente; la deviazione vive SOLO sul valore
    /// riportato (reported = internal || window). "Autorizza solo il flip" non lo
    /// puo' imporre il canale (IsSafe e' UN bit, broadcast): lo impongono il TIMING
    /// (si apre a lead minuti dalla deadline: il flip parte per primo per costruzione
    /// — "no more remaining time") e la REVOCA immediata (pier cambiato => chiusa =>
    /// l'unsafe onesto ri-parcheggia entro un ciclo; al piu' un inizio di posa
    /// interrotto). Il flip sotto nubi e' meccanicamente completo: il sorgente NINA
    /// riattiva il tracking PRIMA dello slew e su OGNI percorso d'errore; il recenter
    /// e' "best effort, always return true".
    ///
    /// Macchina a stati PURA (testabile con clock finto): Idle -> Open -> Idle/Lockout.
    /// Lockout: finestra scaduta senza flip (trigger assente/disabilitato) => niente
    /// riaperture per lo stesso flip pendente — si esce solo con pier cambiato
    /// (operatore) o rientro reale in safe. Mai oscillazioni safe/unsafe cicliche.
    ///
    /// §72-bis (notte 20-21/11) — il flip NON finisce quando cambia il lato del pilastro.
    /// Per NINA (MeridianFlipVM.DoMeridianFlip) il flip e': stop guida, passaggio del
    /// meridiano (tracking fermo per la pausa), flip, ricentraggio, nuova stella, ripresa
    /// della guida, assestamento. Alle 00:34:01 la finestra si e' chiusa al cambio di
    /// pilastro, l'unsafe e' tornato e il Trigger On Unsafe ha interrotto il flip alle
    /// 00:34:24, prima del ricentraggio: montatura girata ma non ricentrata, guida spenta.
    /// Ora, quando NINA annuncia il flip (ITelescopeMediator.BeforeMeridianFlip /
    /// AfterMeridianFlip, presenti nell'SDK 3.2.0.9001; After arriva a fine workflow,
    /// anche se il flip e' stato annullato), la finestra resta aperta finche' il flip non
    /// e' concluso, con un tetto di FlipMaxMinutes. Durante il flip il tracking non si
    /// tocca: lo ferma e lo riavvia NINA (alle 00:33:31 la protezione lo aveva riattivato
    /// scambiando la pausa del meridiano di NINA per la guardia dell'unsafe). Se l'unsafe
    /// arriva a flip gia' iniziato, la finestra si apre per proteggerlo.
    /// Senza avvisi di NINA (flip a mano dalla montatura) vale la regola di prima: il
    /// cambio di pilastro chiude.
    /// </summary>
    public sealed class MeridianProtectionEngine
    {
        /// <summary>Durata massima della finestra: oltre, il flip evidentemente non
        /// arrivera' (trigger assente/disabilitato) e la menzogna DEVE finire.</summary>
        public const double WindowMaxMinutes = 20.0;

        /// <summary>§72-bis — tetto di un flip di NINA in corso: oltre, NINA e' evidentemente
        /// bloccata e l'unsafe onesto deve tornare (il ricentraggio sotto le nubi puo'
        /// ritentare la soluzione astrometrica per diversi minuti).</summary>
        public const double FlipMaxMinutes = 30.0;

        private enum State { Idle, Open, Lockout }

        private State _state = State.Idle;
        private double _openedAt;
        private bool _trackingResumeIssued;
        private int _flipsAtOpen;
        private double? _flipSeenSince;

        /// <summary>Finestra attualmente aperta (il monitor riporta Safe).</summary>
        public bool WindowOpen => _state == State.Open;

        /// <summary>Riporta l'engine allo stato iniziale (Connect/Disconnect espliciti).</summary>
        public void Reset()
        {
            _state = State.Idle;
            _trackingResumeIssued = false;
            _flipSeenSince = null;
        }

        /// <summary>
        /// Un tick (cadenza del poll N6). Ritorna l'evento di transizione e se il
        /// chiamante deve riattivare il tracking (caso ex-post: NINA lo ha gia'
        /// fermato — riavviarlo DENTRO la finestra e' sicuro solo perche' il flip
        /// tardivo scatta alla valutazione successiva del trigger; una sola volta
        /// per finestra).
        /// </summary>
        /// <param name="enabled">kill-switch impostazioni.</param>
        /// <param name="internalSafe">stato ONESTO dei latch (pre-override).</param>
        /// <param name="mountConnected">montatura connessa e leggibile.</param>
        /// <param name="trackingEnabled">tracking attivo adesso.</param>
        /// <param name="pierIsWest">true=flip pendente; false=flip fatto; null=ignoto.</param>
        /// <param name="minutesToFlipDeadline">minuti alla deadline (negativo = oltre).</param>
        /// <param name="leadMinutes">anticipo di apertura della finestra.</param>
        /// <param name="nowSeconds">clock monotono iniettabile.</param>
        /// <param name="flipInProgress">§72-bis — NINA ha annunciato un flip non ancora concluso.</param>
        /// <param name="flipsCompleted">§72-bis — flip conclusi annunciati da NINA (contatore).</param>
        public (MeridianEvent Event, bool ResumeTracking) Tick(
            bool enabled, bool internalSafe, bool mountConnected, bool trackingEnabled,
            bool? pierIsWest, double? minutesToFlipDeadline, double leadMinutes,
            double nowSeconds, bool flipInProgress = false, int flipsCompleted = 0)
        {
            // §72-bis — il flip di NINA conta solo entro il suo tetto.
            if (flipInProgress) { _flipSeenSince ??= nowSeconds; }
            else { _flipSeenSince = null; }
            bool flipActive = flipInProgress
                              && (nowSeconds - _flipSeenSince!.Value) < FlipMaxMinutes * 60.0;
            bool apriPerFlip = flipActive && enabled && !internalSafe && mountConnected;

            switch (_state)
            {
                case State.Idle:
                    if (apriPerFlip)
                    {
                        // §72-bis — unsafe arrivato a flip gia' iniziato: si protegge il flip.
                        // Il tracking lo gestisce NINA: nessuna azione.
                        _state = State.Open;
                        _openedAt = nowSeconds;
                        _trackingResumeIssued = true;
                        _flipsAtOpen = flipsCompleted;
                        return (MeridianEvent.OpenedDuringFlip, false);
                    }
                    if (enabled && !internalSafe && mountConnected
                        && pierIsWest == true
                        && minutesToFlipDeadline is double m && m <= leadMinutes)
                    {
                        _state = State.Open;
                        _openedAt = nowSeconds;
                        _trackingResumeIssued = false;
                        _flipsAtOpen = flipsCompleted;
                        // Ex-post nello stesso tick: se NINA ha GIA' fermato il
                        // tracking (finestra nata in ritardo), va riattivato subito.
                        if (!trackingEnabled)
                        {
                            _trackingResumeIssued = true;
                            return (MeridianEvent.Opened, true);
                        }
                        return (MeridianEvent.Opened, false);
                    }
                    return (MeridianEvent.None, false);

                case State.Open:
                    if (!enabled || !mountConnected)
                    {
                        _state = State.Idle;
                        return (MeridianEvent.ClosedConditionsLost, false);
                    }
                    if (flipInProgress)
                    {
                        // §72-bis — NINA sta flippando: la finestra resta aperta finche' il
                        // flip non e' concluso (ricentraggio e ripresa della guida compresi).
                        // Il cambio di pilastro qui NON chiude, e il tracking non si tocca.
                        if (!flipActive)
                        {
                            _state = State.Lockout;
                            return (MeridianEvent.ClosedTimeout, false);
                        }
                        return (MeridianEvent.None, false);
                    }
                    if (flipsCompleted > _flipsAtOpen)
                    {
                        // §72-bis — NINA ha concluso il flip: missione compiuta.
                        _state = State.Idle;
                        return (MeridianEvent.ClosedFlipDone, false);
                    }
                    if (pierIsWest == false)
                    {
                        // Pier cambiato: il flip meccanico e' avvenuto. Missione
                        // compiuta, la finestra si chiude e l'unsafe onesto torna.
                        _state = State.Idle;
                        return (MeridianEvent.ClosedFlipDone, false);
                    }
                    if (internalSafe || !enabled || !mountConnected || pierIsWest == null)
                    {
                        // Rientro reale in safe / kill-switch / mount perso: la
                        // finestra non serve piu' (o non e' piu' verificabile).
                        _state = State.Idle;
                        return (MeridianEvent.ClosedConditionsLost, false);
                    }
                    if ((nowSeconds - _openedAt) >= WindowMaxMinutes * 60.0)
                    {
                        // Il flip non e' arrivato (trigger assente/disabilitato):
                        // la menzogna finisce QUI. Lockout: niente riaperture per
                        // questo flip pendente (mai oscillazioni cicliche).
                        _state = State.Lockout;
                        return (MeridianEvent.ClosedTimeout, false);
                    }
                    if (!trackingEnabled && !_trackingResumeIssued)
                    {
                        _trackingResumeIssued = true;
                        return (MeridianEvent.None, true);
                    }
                    return (MeridianEvent.None, false);

                case State.Lockout:
                default:
                    if (apriPerFlip)
                    {
                        // §72-bis — un flip partito dopo la scadenza (per esempio forzato
                        // dall'operatore riavviando la sequenza) va protetto fino in fondo.
                        _state = State.Open;
                        _openedAt = nowSeconds;
                        _trackingResumeIssued = true;
                        _flipsAtOpen = flipsCompleted;
                        return (MeridianEvent.OpenedDuringFlip, false);
                    }
                    if (pierIsWest == false || internalSafe || !enabled)
                    {
                        _state = State.Idle;   // operatore ha flippato, o safe reale
                    }
                    return (MeridianEvent.None, false);
            }
        }
    }
}
