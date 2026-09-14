using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using HighPop.Models;

namespace HighPop.Services;

/// <summary>Creates a local-only diagnostic archive with an explicit secret allow-list.</summary>
public sealed class SupportBundleService
{
    private static readonly Regex SensitiveAssignment = new(
        """(?i)(password|token|secret|api[_-]?key)(\s*[=:]\s*|"\s*:\s*")([^\s,;"}]+)""",
        RegexOptions.Compiled);
    private static readonly Regex DiscordWebhook = new(
        """https://(?:canary\.|ptb\.)?discord(?:app)?\.com/api/webhooks/[^\s"]+""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WebRconCredential = new(
        """(wss?://[^/\s]+/)[^\s"]+""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ProtectedValue = new(
        @"dpapi:[A-Za-z0-9+/=]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ConfigService _config;
    private readonly ServerManagerService _manager;

    public SupportBundleService(ConfigService config, ServerManagerService manager)
    {
        _config = config;
        _manager = manager;
    }

    public async Task CreateAsync(string destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var staging = Path.Combine(Path.GetTempPath(), "highpop-support-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var servers = _config.LoadServers();
            var profiles = servers.Select(server => new
            {
                server.Id,
                server.DisplayName,
                server.GameId,
                server.ServerPort,
                server.QueryPort,
                server.RconPort,
                server.AutoConnectRcon,
                server.RconAutoConnectDelaySeconds,
                server.RconAutoConnectTimeoutMinutes,
                server.StartupGraceMinutes,
                server.KeepOnline,
                server.AutoRestart,
                server.AutoStart,
                server.DesiredState,
                server.LifecyclePhase,
                server.LastLifecycleInitiator,
                server.LastLifecycleReason,
                server.LastLifecycleTransitionUtc,
            });
            await WriteRedactedAsync(Path.Combine(staging, "profiles.json"),
                JsonSerializer.Serialize(profiles, options), cancellationToken);
            await WriteRedactedAsync(Path.Combine(staging, "health.json"),
                JsonSerializer.Serialize(_manager.GetHealthSnapshots(), options), cancellationToken);
            await WriteRedactedAsync(Path.Combine(staging, "lifecycle.json"),
                JsonSerializer.Serialize(servers.Select(server => new
                {
                    server.Id,
                    server.DisplayName,
                    Operations = server.LifecycleOperationHistory.TakeLast(ServerLifecycleRules.MaxOperationHistory),
                }), options), cancellationToken);

            var environment = $"HighPop: {AppInfo.VersionDisplay}\n"
                + $"Created UTC: {DateTime.UtcNow:O}\n"
                + $"OS: {Environment.OSVersion}\n"
                + $".NET: {Environment.Version}\n"
                + $"64-bit process: {Environment.Is64BitProcess}\n";
            await WriteRedactedAsync(Path.Combine(staging, "environment.txt"), environment, cancellationToken);

            var logDirectory = Path.Combine(staging, "logs");
            Directory.CreateDirectory(logDirectory);
            foreach (var snapshot in _manager.GetHealthSnapshots())
            {
                var instance = _manager.GetInstance(snapshot.ServerId);
                if (instance == null) continue;
                var safeName = string.Concat(snapshot.DisplayName.Select(ch =>
                    Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                var lines = instance.GetLogSnapshot().TakeLast(500).Select(message => message.Text);
                await WriteRedactedAsync(Path.Combine(logDirectory, safeName + ".log"),
                    string.Join(Environment.NewLine, lines), cancellationToken);
            }

            var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);
            if (File.Exists(destination)) File.Delete(destination);
            ZipFile.CreateFromDirectory(staging, destination, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        value = DiscordWebhook.Replace(value, "[REDACTED DISCORD WEBHOOK]");
        value = WebRconCredential.Replace(value, "$1[REDACTED]");
        value = ProtectedValue.Replace(value, "dpapi:[REDACTED]");
        return SensitiveAssignment.Replace(value, match => match.Groups[1].Value
            + match.Groups[2].Value + "[REDACTED]");
    }

    private static Task WriteRedactedAsync(string path, string content, CancellationToken token) =>
        File.WriteAllTextAsync(path, Redact(content), token);
}
