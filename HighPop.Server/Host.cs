using System.Runtime.InteropServices;
using HighPop.Games;
using HighPop.Models;
using HighPop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HighPop.Server;

/// <summary>
/// Headless supervisor: reattaches to running servers, resumes crash recovery, auto-starts,
/// serves the Web API and waits for SIGTERM/Ctrl+C. Rust keeps running when the host exits,
/// exactly like closing the desktop app.
/// </summary>
internal sealed class Host
{
    private readonly ServiceProvider _sp;
    private readonly ConfigService _config;
    private readonly ServerManagerService _manager;
    private readonly List<GameServer> _servers = [];

    public Host(ServiceProvider sp)
    {
        _sp = sp;
        _config = sp.GetRequiredService<ConfigService>();
        _manager = sp.GetRequiredService<ServerManagerService>();
    }

    public async Task<int> RunAsync()
    {
        Log($"HighPop {AppInfo.Version} headless host starting ({RuntimeInformation.OSDescription})");
        Log($"Data folder: {_config.AppDataPath}");

        _manager.StatusChanged += (id, status) => Log($"{Name(id)}: {status}");
        _manager.LifecycleChanged += t => Log($"{Name(t.ServerId)}: lifecycle {t.Phase} ({t.Reason})");
        _manager.CrashLimitReached += id => Log($"{Name(id)}: crash limit reached, automatic restarts paused");

        _servers.AddRange(_config.LoadServers()
            .Where(s => s.GameId.Equals("rust", StringComparison.OrdinalIgnoreCase)));
        Log($"{_servers.Count} server profile(s) loaded");

        StartWebApi();
        await ResumeServersAsync();
        _sp.GetRequiredService<ServerHealthService>().StartMonitoring();

        using var quit = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; quit.Cancel(); });
        try { await Task.Delay(Timeout.Infinite, quit.Token); }
        catch (OperationCanceledException) { }

        Log("Shutting down. Running servers keep running and are reattached next start.");
        try { _manager.DetachAllForManagerExit(); } catch (Exception ex) { Log($"Detach failed: {ex.Message}"); }
        try { _config.SaveServers(_servers); } catch (Exception ex) { Log($"Saving profiles failed: {ex.Message}"); }
        _sp.GetRequiredService<WebApiService>().Stop();
        return 0;
    }

    private void StartWebApi()
    {
        var api = _sp.GetRequiredService<WebApiService>();
        var metrics = _sp.GetRequiredService<SystemMetricsService>();
        var net = _sp.GetRequiredService<NetworkMonitorService>();
        api.Users = _sp.GetRequiredService<UserService>();
        api.GetServers = () => _servers.ToList();
        api.GetMetrics = () => metrics.Current;
        api.GetNetwork = () => (net.CurrentBytesInPerSec / 1024.0, net.CurrentBytesOutPerSec / 1024.0);
        api.StartServer = id => Find(id) is { } s
            ? _manager.StartAsync(s, LifecycleInitiator.WebApi, "Web API start request") : Task.CompletedTask;
        api.StopServer = id => Find(id) is { } s
            ? _manager.StopAsync(s, "Web API stop request", LifecycleInitiator.WebApi) : Task.CompletedTask;
        api.RestartServer = async id =>
        {
            if (Find(id) is not { } s) return;
            await _manager.StopAsync(s, "Web API restart request", LifecycleInitiator.WebApi);
            await _manager.StartAsync(s, LifecycleInitiator.WebApi, "Web API restart request");
        };
        api.GetUptime = id => _manager.GetInstance(id)?.StartTime is { } t ? (DateTime.Now - t).ToString(@"d\.hh\:mm\:ss") : null;

        api.DashboardEnabled = _config.WebApiEnabled;
        if (!_config.WebApiRequired || _config.WebApiPort <= 0)
        {
            Log("Web API is off. Run 'highpop-server setup' to enable it.");
            return;
        }
        try
        {
            api.Start(_config.WebApiPort, _config.WebApiToken);
            Log($"Web API listening on port {_config.WebApiPort}");
        }
        catch (Exception ex) { Log($"Web API could not start: {ex.Message}"); }
    }

    private async Task ResumeServersAsync()
    {
        foreach (var srv in _servers)
        {
            srv.Status = ServerStatus.Stopped;
            bool reattached = _manager.TryReattach(srv);
            if (reattached) { Log($"{srv.DisplayName}: reattached to the running server"); continue; }

            if (ServerLifecycleRules.CanRecover(srv))
            {
                Log($"{srv.DisplayName}: resuming after an unexpected stop");
                _manager.QueueAlwaysOnRecovery(srv, "host launch found persisted running intent");
            }
            else if (ServerStartupPolicy.ShouldStartOnManagerLaunch(srv, reattached))
            {
                try
                {
                    await Installer.EnsureInstalledAsync(_sp, srv, Log);
                    await _manager.StartAsync(srv, LifecycleInitiator.AutoStart, "Auto-start on host launch");
                }
                catch (Exception ex) { Log($"{srv.DisplayName}: auto-start failed: {ex.Message}"); }
            }
        }
    }

    private GameServer? Find(string id) => _servers.FirstOrDefault(s => s.Id == id);
    private string Name(string id) => Find(id)?.DisplayName ?? id;
    private static void Log(string text) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}");
}
