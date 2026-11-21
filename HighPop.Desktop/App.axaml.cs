using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HighPop.Desktop.Platform;
using HighPop.Desktop.Views;
using HighPop.Services;
using HighPop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HighPop.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            UiDispatcher.Thread = new AvaloniaUiThread();
            Dialogs.Platform = new AvaloniaPlatform();

            var collection = new ServiceCollection();
            ConfigureServices(collection);
            Services = collection.BuildServiceProvider();

            LinuxSelfUpdateService.CleanupLeftovers();

            var config = Services.GetRequiredService<ConfigService>();
            LocalizationService.Instance.Load(config);
            StartBackgroundServices(config);

            var main = Services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = main };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => OnExit(main);

            var tray = (AppTray)Services.GetRequiredService<ITrayStatus>();
            tray.Attach(window.ShowFromTray, window.ExitApp);
            TrayIcon.SetIcons(this, tray.Icons);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void StartBackgroundServices(ConfigService config)
    {
        var webApi = Services.GetRequiredService<WebApiService>();
        webApi.DashboardEnabled = config.WebApiEnabled;
        if (config.WebApiRequired && config.WebApiPort > 0)
        {
            var port = config.WebApiPort;
            var token = config.WebApiToken;
            _ = Task.Run(() =>
            {
                try { webApi.Start(port, token); }
                catch (Exception ex) { Console.Error.WriteLine($"[HighPop] Web API could not start: {ex.Message}"); }
            });
        }
        Services.GetRequiredService<RemoteMachineService>().Initialize();
        Services.GetRequiredService<CrashPredictionService>().Initialize();
    }

    /// <summary>A full exit detaches from live Rust processes; only Stop terminates a server.</summary>
    private static void OnExit(MainViewModel main)
    {
        try
        {
            Services.GetRequiredService<ServerManagerService>().DetachAllForManagerExit();
            main.Save();
        }
        catch { }
        (Services as IDisposable)?.Dispose();
    }

    private static void ConfigureServices(IServiceCollection s)
    {
        s.AddSingleton<ConfigService>();
        s.AddSingleton<SteamCmdService>();
        s.AddSingleton<RustTelemetryService>();
        s.AddSingleton<ServerLifecycleCoordinator>();
        s.AddSingleton<ServerManagerService>();
        s.AddSingleton<BackupService>();
        s.AddSingleton<NotificationService>();
        s.AddSingleton<PerformanceMonitorService>();
        s.AddSingleton<ScheduledTaskService>();
        s.AddSingleton<ITrayStatus, AppTray>();
        s.AddSingleton<SystemMetricsService>();
        s.AddSingleton<ModManagerService>();
        s.AddSingleton<DiscordBotService>();
        s.AddSingleton<ConfigEditorService>();
        s.AddSingleton<ConfigPresetService>();
        s.AddSingleton<ServerProfileTransferService>();
        s.AddSingleton<PlayerStatsService>();
        s.AddSingleton<PerfHistoryService>();
        s.AddSingleton<UPnPService>();
        s.AddSingleton<TemplateService>();
        s.AddSingleton<NetworkMonitorService>();
        s.AddSingleton<UserService>();
        s.AddSingleton<ServerGroupService>();
        s.AddSingleton<GroupBanListService>();
        s.AddSingleton<RustModerationService>();
        s.AddSingleton<ServerHygieneService>();
        s.AddSingleton<RustReferenceService>();
        s.AddSingleton<RogueRustPluginCatalogService>();
        s.AddSingleton<WebApiService>();
        s.AddSingleton<RemoteMachineService>();
        s.AddSingleton<CrashPredictionService>();
        s.AddSingleton<LogWatcherService>();
        s.AddSingleton<ServerHealthService>();
        s.AddSingleton<SupportBundleService>();
        s.AddSingleton<WakeOnDemandService>();
        s.AddSingleton<MainViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<DashboardViewModel>(sp => sp.GetRequiredService<MainViewModel>().Dashboard);
    }
}
