#nullable enable
using NINA.Core.Utility;
using System.Globalization;
using System.Resources;

namespace AdaptiveAgentForPHD2.NinaPlugin.Localization
{
    /// <summary>
    /// §60 — localizzazione del SOLO plugin, indipendente dalla lingua di N.I.N.A.
    ///
    /// Design (deviazione deliberata dai satellite assemblies): entrambe le lingue
    /// vivono come resx neutri EMBEDDED nella DLL principale (Strings_en / Strings_it,
    /// underscore per NON innescare la generazione di satellite) e questo Loc sceglie
    /// il ResourceManager. Motivo: il plugin si distribuisce come SINGOLA DLL
    /// (install-plugin.ps1 oggi, ARCHIVE del Plugin Manager domani) — una cartella
    /// it\ dimenticata significherebbe italiano silenziosamente rotto.
    ///
    /// Pattern di binding identico a quello di NINA (indexer + PropertyChanged su
    /// "Item[]"): {Binding [Chiave], Source={x:Static loc:Loc.Instance}} — il cambio
    /// lingua aggiorna la UI LIVE, senza riavvio e senza toccare la cultura di NINA.
    ///
    /// Confini: gli ExportMetadata del sequencer (nome/descrizione della Recovery
    /// probe nella sidebar) sono costanti compile-time -> restano in inglese; il Name
    /// del device Safety Monitor resta in inglese (riconoscibilita'); i LOG restano
    /// SEMPRE in inglese (sono per il supporto, non per l'utente).
    /// </summary>
    public sealed class Loc : BaseINPC
    {
        public static Loc Instance { get; } = new Loc();

        private static readonly ResourceManager En = new(
            "AdaptiveAgentForPHD2.NinaPlugin.Localization.Strings_en", typeof(Loc).Assembly);
        private static readonly ResourceManager It = new(
            "AdaptiveAgentForPHD2.NinaPlugin.Localization.Strings_it", typeof(Loc).Assembly);

        // "" = Follow N.I.N.A. (CurrentUICulture), "en", "it".
        private string _mode = "";

        private Loc() { }

        /// <summary>Impostata dal setting PluginLanguage; aggiorna la UI live.</summary>
        public void SetLanguage(string? mode)
        {
            _mode = (mode ?? "").Trim().ToLowerInvariant();
            RaisePropertyChanged("Item[]");
        }

        public string this[string key]
        {
            get
            {
                var useItalian = _mode == "it"
                    || (_mode == "" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it");
                var primary = useItalian ? It : En;
                // fallback: inglese, poi la chiave stessa (mai stringhe vuote in UI)
                return primary.GetString(key) ?? En.GetString(key) ?? key;
            }
        }

        /// <summary>Accesso dal code-behind/servizi: Loc.T("Chiave").</summary>
        public static string T(string key) => Instance[key];
    }
}
