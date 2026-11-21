using HighPop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HighPop.Server;

/// <summary>Builds the same Core services the desktop app uses, without any window or tray.</summary>
internal static class HostServices
{
    public static ServiceProvider Build()
    {
        var s = new ServiceCollection();
        s.AddSingleton<ConfigService>();
        s.AddSingleton<SteamCmdService>();
        s.AddSingleton<RustTelemetryService>();
        s.AddSingleton<ServerLifecycleCoordinator>();
        s.AddSingleton<NetworkMonitorService>();
        s.AddSingleton<ServerHygieneService>();
        s.AddSingleton<ServerManagerService>();
        s.AddSingleton<NotificationService>();
        s.AddSingleton<SystemMetricsService>();
        s.AddSingleton<UserService>();
        s.AddSingleton<WebApiService>();
        s.AddSingleton<ServerHealthService>();
        return s.BuildServiceProvider();
    }
}
