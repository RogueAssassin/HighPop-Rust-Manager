using HighPop.Games;
using HighPop.Models;
using HighPop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HighPop.Server;

internal static class Installer
{
    private const int RustAppId = 258550;

    public static async Task EnsureInstalledAsync(IServiceProvider sp, GameServer server, Action<string> log)
    {
        var exe = Path.Combine(server.InstallPath, HostPlatform.RustExecutableName);
        if (File.Exists(exe)) return;
        await InstallAsync(sp, server, log);
    }

    public static async Task InstallAsync(IServiceProvider sp, GameServer server, Action<string> log)
    {
        var config = sp.GetRequiredService<ConfigService>();
        var steam = sp.GetRequiredService<SteamCmdService>();
        Directory.CreateDirectory(server.InstallPath);
        log($"{server.DisplayName}: installing or updating Rust into {server.InstallPath} (this can take a while)");
        await steam.DownloadSteamCmdAsync(server.Id);
        await steam.InstallOrUpdateAsync(server.Id, RustAppId, server.InstallPath,
            config.SteamLogin, config.SteamPassword, null);
        log($"{server.DisplayName}: install finished");
    }
}
