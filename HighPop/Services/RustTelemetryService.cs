using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using HighPop.Models;
using Newtonsoft.Json;

namespace HighPop.Services;

public sealed class RustTelemetryEvent
{
    public string SchemaVersion { get; init; } = "highpop.rust.event/v1";
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public string ServerId { get; init; } = string.Empty;
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
    public string Name { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Data { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>
/// Stage 5's local, versioned event foundation. It records bounded JSONL files
/// beside HighPop's portable data and is intentionally disabled per server by default.
/// Future Carbon/uMod and live-map adapters can emit into the same schema.
/// </summary>
public sealed class RustTelemetryService
{
    private static readonly Regex EventNameRegex =
        new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private readonly string _root;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, int> _appendCounts = new();

    public RustTelemetryService(ConfigService config)
        : this(Path.Combine(config.AppDataPath, "telemetry"))
    {
    }

    public RustTelemetryService(string root)
    {
        _root = Path.GetFullPath(root);
        try { Directory.CreateDirectory(_root); } catch { }
    }

    public string GetServerDirectory(GameServer server)
    {
        if (!Guid.TryParse(server.Id, out var serverId))
            throw new InvalidOperationException("Telemetry requires a valid server profile ID.");
        return Path.Combine(_root, serverId.ToString("D"));
    }

    public async Task AppendAsync(
        GameServer server,
        string name,
        string source,
        IReadOnlyDictionary<string, string>? data = null)
    {
        if (!server.RustTelemetryEnabled) return;
        if (!EventNameRegex.IsMatch(name))
            throw new ArgumentException("Telemetry event name is invalid.", nameof(name));

        var serverDirectory = GetServerDirectory(server);
        var gate = _gates.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(serverDirectory);
            var count = _appendCounts.AddOrUpdate(server.Id, 1, (_, current) => current + 1);
            if (count == 1 || count % 100 == 0) PruneCore(server, serverDirectory);

            var evt = new RustTelemetryEvent
            {
                ServerId = server.Id,
                Name = name,
                Source = Trim(source, 64),
                Data = SanitizeData(data),
            };
            var path = Path.Combine(serverDirectory, $"{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            var json = JsonConvert.SerializeObject(evt, Formatting.None);
            var maximumBytes =
                Math.Clamp(server.RustTelemetryMaxMegabytes, 16, 4096) * 1024L * 1024L;
            var existingBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (existingBytes + Encoding.UTF8.GetByteCount(json) + Environment.NewLine.Length
                > maximumBytes)
                return;
            await File.AppendAllTextAsync(path, json + Environment.NewLine);
        }
        catch (IOException)
        {
            // Telemetry is optional and must never interrupt live server management.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only portable folder disables recording without affecting Rust.
        }
        finally
        {
            gate.Release();
        }
    }

    public void Prune(GameServer server)
    {
        var serverDirectory = GetServerDirectory(server);
        if (!Directory.Exists(serverDirectory)) return;
        PruneCore(server, serverDirectory);
    }

    private static IReadOnlyDictionary<string, string> SanitizeData(
        IReadOnlyDictionary<string, string>? data)
    {
        if (data == null || data.Count == 0)
            return new Dictionary<string, string>();

        var sanitized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in data.Take(32))
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            sanitized[Trim(pair.Key, 64)] = Trim(pair.Value, 512);
        }
        return sanitized;
    }

    private static string Trim(string? value, int maxLength)
    {
        var safe = new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .ToArray());
        return safe.Length <= maxLength ? safe : safe[..maxLength];
    }

    private static void PruneCore(GameServer server, string serverDirectory)
    {
        var files = Directory.GetFiles(serverDirectory, "*.jsonl")
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(server.RustTelemetryRetentionDays, 1, 365));

        foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff).ToList())
        {
            try { file.Delete(); files.Remove(file); } catch { }
        }

        var maximumBytes = Math.Clamp(server.RustTelemetryMaxMegabytes, 16, 4096) * 1024L * 1024L;
        var totalBytes = files.Sum(file => file.Exists ? file.Length : 0);
        foreach (var file in files.Take(Math.Max(0, files.Count - 1)))
        {
            if (totalBytes <= maximumBytes) break;
            try
            {
                var length = file.Length;
                file.Delete();
                totalBytes -= length;
            }
            catch { }
        }
    }
}
