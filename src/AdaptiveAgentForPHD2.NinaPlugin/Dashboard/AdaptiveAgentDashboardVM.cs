#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Localization;
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using AdaptiveAgentForPHD2.NinaPlugin.Launch;
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AdaptiveAgentForPHD2.NinaPlugin.Dashboard
{
    [Export(typeof(IDockableVM))]
    public class AdaptiveAgentDashboardVM : DockableVM
    {
        public const string DefaultDashboardUrl = PluginSettings.DefaultDashboardUrl;
        private static string LaunchLabel => Loc.T("Dash_Launch");

        // §60 — versione del footer letta dall'assembly: il vecchio literal XAML era
        // rimasto fermo a "v1.5". Identità/brand: resta in inglese by design.
        public string FooterText { get; } =
            "Adaptive Agent for PHD2 — Dashboard v"
            + (typeof(AdaptiveAgentDashboardVM).Assembly.GetName().Version?.ToString() ?? "?")
            + "  |  by Alessandro Curci  |  Copyright (c) 2026";

        // Brush frozen => sicuri da usare cross-thread (il poller gira sul thread del timer).
        private static readonly Brush OnlineBrush = Frozen(Color.FromRgb(0x2E, 0x7D, 0x32));
        private static readonly Brush OfflineBrush = Frozen(Color.FromRgb(0x61, 0x61, 0x61));

        private readonly PluginSettings _settings;
        private readonly AgentLauncher _launcher;
        private readonly AgentHealthChecker _health;

        private string _statusBadgeText = Loc.T("Dash_AgentOffline");
        private Brush _statusBadgeBackground = OfflineBrush;
        private string _launchButtonText = LaunchLabel;
        private bool _launchButtonEnabled;
        private string _launchButtonTooltip = "";

        [ImportingConstructor]
        public AdaptiveAgentDashboardVM(IProfileService profileService)
            : base(profileService)
        {
            Title = "Adaptive Agent for PHD2";
            CanClose = true;
            ImageGeometry = null!;

            var services = AgentServices.Instance;
            _settings = services.Settings;
            _launcher = services.Launcher;
            _health = services.HealthChecker;

            LaunchAgentCommand = new AsyncRelayCommand(LaunchAgentAsync);

            _health.StatusChanged += OnAgentStatusChanged;
            _settings.PropertyChanged += OnSettingsChanged;
            ApplyHealth(_health.Current);
        }

        public override bool IsTool => true;

        /// <summary>URL letto dalle settings (override del default v1.0).</summary>
        public string DashboardUrl => _settings.DashboardUrl;

        public ICommand LaunchAgentCommand { get; }

        public string StatusBadgeText
        {
            get => _statusBadgeText;
            private set { _statusBadgeText = value; RaisePropertyChanged(); }
        }

        public Brush StatusBadgeBackground
        {
            get => _statusBadgeBackground;
            private set { _statusBadgeBackground = value; RaisePropertyChanged(); }
        }

        public string LaunchButtonText
        {
            get => _launchButtonText;
            private set { _launchButtonText = value; RaisePropertyChanged(); }
        }

        public bool LaunchButtonEnabled
        {
            get => _launchButtonEnabled;
            private set { _launchButtonEnabled = value; RaisePropertyChanged(); }
        }

        public string LaunchButtonTooltip
        {
            get => _launchButtonTooltip;
            private set { _launchButtonTooltip = value; RaisePropertyChanged(); }
        }

        private async Task LaunchAgentAsync()
        {
            var result = await _launcher.LaunchAsync(_settings.AgentBatPath);
            switch (result.Level)
            {
                case LaunchLevel.Info:
                    Notification.ShowInformation(result.Message);
                    break;
                case LaunchLevel.Warning:
                    Notification.ShowWarning(result.Message);
                    break;
                default:
                    Notification.ShowError(result.Message);
                    break;
            }
        }

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Un cambio di AgentBatPath puo' abilitare/disabilitare il pulsante.
            if (e.PropertyName == nameof(PluginSettings.AgentBatPath))
            {
                OnAgentStatusChanged(_health.Current);
            }
        }

        private void OnAgentStatusChanged(AgentHealth health)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new System.Action(() => ApplyHealth(health)));
            }
            else
            {
                ApplyHealth(health);
            }
        }

        private void ApplyHealth(AgentHealth health)
        {
            var configured = !string.IsNullOrWhiteSpace(_settings.AgentBatPath);

            if (health.IsOnline)
            {
                StatusBadgeText = string.IsNullOrEmpty(health.Version)
                    ? Loc.T("Dash_AgentOnline")
                    : string.Format(Loc.T("Dash_AgentOnlineV"), health.Version);
                StatusBadgeBackground = OnlineBrush;
                LaunchButtonText = LaunchLabel;
                LaunchButtonEnabled = false;
                LaunchButtonTooltip = Loc.T("Dash_Tip_Running");
            }
            else
            {
                StatusBadgeText = Loc.T("Dash_AgentOffline");
                StatusBadgeBackground = OfflineBrush;
                if (configured)
                {
                    LaunchButtonText = LaunchLabel;
                    LaunchButtonEnabled = true;
                    LaunchButtonTooltip = Loc.T("Dash_Tip_Start");
                }
                else
                {
                    LaunchButtonText = Loc.T("Dash_SetPath");
                    LaunchButtonEnabled = false;
                    LaunchButtonTooltip = Loc.T("Dash_Tip_SetPath");
                }
            }
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
