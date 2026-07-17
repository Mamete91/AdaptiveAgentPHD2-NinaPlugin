using AdaptiveAgentForPHD2.NinaPlugin.Health;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AdaptiveAgentForPHD2.NinaPlugin.Dashboard
{
    public partial class AdaptiveAgentDashboardView : UserControl
    {
        public AdaptiveAgentDashboardView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Cartella dati dedicata per questo plugin: evita conflitti con
                // la istanza WebView2 interna di NINA (Browser panel e altri plugin).
                // Se due istanze WebView2 usano la stessa UserDataFolder nel
                // medesimo processo, quella secondaria fallisce la navigazione
                // in modo silenzioso (IsSuccess = false senza eccezione).
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NINA", "WebView2", "AdaptiveAgentDashboard");

                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder:         userDataFolder);

                await WebViewControl.EnsureCoreWebView2Async(env);
                WebViewControl.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                NavigateToDashboard();
            }
            catch (Exception ex)
            {
                ShowFallback($"WebView2 initialization error: {ex.Message}");
            }

            // v1.2.1: il poller (badge) e il WebView avevano due meccanismi indipendenti per capire
            // se l'Agente e' raggiungibile. Sottoscrivendoci alle transizioni del poller, quando
            // l'Agente torna su rinfreschiamo automaticamente il WebView (prima restava nel fallback
            // finche' l'utente non premeva "Riprova"). Il -= difensivo evita doppie sottoscrizioni
            // se Loaded scatta piu' volte senza un Unloaded intermedio.
            AgentServices.Instance.HealthChecker.StatusChanged -= OnAgentHealthChanged;
            AgentServices.Instance.HealthChecker.StatusChanged += OnAgentHealthChanged;

            // v1.2.2: tornando sul pannello dopo un cambio schermata, il Loaded rifa' il primo
            // Navigate che a volte fallisce (IsSuccess == false) => fallback. Ma il poller dice
            // gia' "online" (stato condiviso, nessuna transizione) quindi OnAgentHealthChanged non
            // scatta mai. Se il poller e' gia' online, schedula un reload ritardato che corregge
            // il fallback dando prima tempo al primo Navigate di completare.
            if (AgentServices.Instance.HealthChecker.Current.IsOnline)
            {
                await Task.Delay(500);
                if (IsLoaded)
                {
                    // Discard: non attendiamo la DispatcherOperation (evita CS4014 nel metodo async).
                    _ = Dispatcher.BeginInvoke(new Action(NavigateToDashboard), DispatcherPriority.Background);
                }
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            AgentServices.Instance.HealthChecker.StatusChanged -= OnAgentHealthChanged;
        }

        // StatusChanged e' un Action<AgentHealth> (NON un EventHandler): firma a singolo parametro.
        // L'evento e' gia' garantito (v1.1) di essere emesso SOLO sulle transizioni, quindi
        // health.IsOnline == true qui significa che siamo appena passati offline -> online.
        private void OnAgentHealthChanged(AgentHealth health)
        {
            if (!health.IsOnline) { return; }
            // Guard difensivo: l'evento puo' arrivare su un View gia' scaricato (fuori visual tree).
            if (!IsLoaded) { return; }

            // L'evento arriva dal thread del timer del poller: marshaling sul UI thread.
            Dispatcher.Invoke(() => NavigateToDashboard());
        }

        private void NavigateToDashboard()
        {
            FallbackPanel.Visibility = Visibility.Collapsed;
            WebViewControl.Visibility = Visibility.Visible;

            var url = (DataContext as AdaptiveAgentDashboardVM)?.DashboardUrl
                      ?? AdaptiveAgentDashboardVM.DefaultDashboardUrl;
            WebViewControl.Source = new Uri(url);
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                // WebErrorStatus da leggere in Visual Studio Output o tramite debugger
                // per diagnosticare il tipo di errore (es: ConnectionRefused, NameNotResolved...)
                ShowFallback($"NavigationFailed — WebErrorStatus: {e.WebErrorStatus}");
            }
        }

        private void OnReloadClick(object sender, RoutedEventArgs e)
        {
            if (WebViewControl?.CoreWebView2 != null)
                NavigateToDashboard();
        }

        private void ShowFallback(string debugMessage)
        {
            WebViewControl.Visibility = Visibility.Collapsed;
            FallbackPanel.Visibility = Visibility.Visible;
            if (debugMessage != null)
                System.Diagnostics.Debug.WriteLine(
                    $"[AdaptiveAgentDashboard] {debugMessage}");
        }
    }
}
