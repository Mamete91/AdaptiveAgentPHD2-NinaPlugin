#nullable enable

namespace AdaptiveAgentForPHD2.NinaPlugin.Safety
{
    /// <summary>
    /// Vista in sola lettura delle impostazioni consumate dal SafetyDecisionEngine.
    /// Estratta da PluginSettings (che la implementa) per rendere l'engine unit-testabile
    /// con uno stub, senza il costruttore con I/O su disco di PluginSettings.
    /// </summary>
    public interface ISafetySettings
    {
        int HealthCheckIntervalSeconds { get; }
        int StarLostConsolidationSeconds { get; }

        // §49 N6 — cloud safety (condivise tra logica legacy e logica a indice)
        bool CloudSafetyEnabled { get; }
        int CloudUnsafePolls { get; }
        int ClearSafePolls { get; }

        // Fix N6 §2 — logica a indice (leaky accumulator). false = logica legacy
        // (poll consecutivi sullo stato discreto, comportamento pre-fix).
        bool UseIndexCloudLogic { get; }
        double CloudIndexAccumulateBelow { get; }
        double CloudIndexDrainAbove { get; }

        // Fix N6 §1 — telemetria stantia oltre la finestra adattiva §43 durante
        // sessione attiva con ultimo contesto degradato => UNSAFE.
        bool StaleUnsafeEnabled { get; }
        int StaleUnsafePolls { get; }

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
