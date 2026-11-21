#nullable enable
namespace AdaptiveAgentForPHD2.NinaPlugin.Safety
{
    /// <summary>
    /// Vista in sola lettura delle impostazioni consumate dal SafetyDecisionEngine.
    /// Estratta da PluginSettings (che la implementa) per rendere l'engine unit-testabile
    /// con uno stub, senza il costruttore con I/O su disco di PluginSettings.
    /// §128 (1.15) — tolte le impostazioni del giudizio della camera di ripresa.
    /// </summary>
    public interface ISafetySettings
    {
        int HealthCheckIntervalSeconds { get; }
        int StarLostConsolidationSeconds { get; }
        /// <summary>Sicurezza nubi: false spegne il percorso del cielo (restano stella
        /// persa, canale muto e Agente perso).</summary>
        bool CloudSafetyEnabled { get; }
        /// <summary>§126 — poll di sereno confermato dalla guida per scaricare del tutto il
        /// cielo saturo e tornare SAFE.</summary>
        int ClearSafePolls { get; }
        /// <summary>§79/§126 — poll di segnale crollato della stella di guida prima
        /// dell'UNSAFE per nubi (dopo i 90 s che l'Agente chiede per confermarlo).</summary>
        int SkyDegradingUnsafePolls { get; }
        // Fix N6 §5 — agente irraggiungibile durante sessione attiva => UNSAFE
        // (mai disconnect-to-SAFE).
        bool AgentLostUnsafeEnabled { get; }
        int AgentLostUnsafePolls { get; }
        // §68 — OSSERVABILITA' del canale di guida (non la sua qualita': quella resta
        // del motore adattivo, §65). Il canale tace mentre PHD2 non ha annunciato
        // alcuna pausa => l'ultimo `guiding_state` noto e' una bugia che invecchia.
        bool GuideUnobservableEnabled { get; }
        int GuideSilenceSeconds { get; }        // silenzio oltre il quale il canale e' sospetto
        int GuideUnobservablePolls { get; }     // consolidamento (accumulatore leaky)
    }
}
