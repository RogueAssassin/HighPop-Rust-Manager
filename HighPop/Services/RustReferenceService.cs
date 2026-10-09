using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HighPop.Services;

public sealed class RustReferenceEntry
{
    public string Name { get; set; } = string.Empty;
    public string? Help { get; set; }
    public string? Type { get; set; }
    public bool ServerUser { get; set; }
    public bool Client { get; set; }
    public bool Server { get; set; }
    public bool Saved { get; set; }
    public bool ServerAdmin { get; set; }
    public bool Clientside { get; set; }
    public bool Serverside { get; set; }
    public JsonElement DefaultValue { get; set; }

    public string Kind { get; set; } = "Command";
    [JsonIgnore] public string Description => string.IsNullOrWhiteSpace(Help)
        ? "No description is currently supplied by Rust metadata."
        : UiTextSanitizer.Normalize(Help, 2_048);
    [JsonIgnore] public string DefaultText => DefaultValue.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
        JsonValueKind.String => DefaultValue.GetString() ?? string.Empty,
        _ => DefaultValue.GetRawText(),
    };
    [JsonIgnore] public string Scope
    {
        get
        {
            var scopes = new List<string>();
            if (Server || Serverside) scopes.Add("Server");
            if (Client || Clientside) scopes.Add("Client");
            if (ServerAdmin) scopes.Add("Admin");
            if (ServerUser) scopes.Add("User");
            if (Saved) scopes.Add("Saved");
            return scopes.Count == 0 ? "Rust metadata" : string.Join(" · ", scopes.Distinct());
        }
    }
}

public sealed record RustReferenceSnapshot(
    IReadOnlyList<RustReferenceEntry> Entries,
    DateTimeOffset? UpdatedAt,
    bool FromCache);

/// <summary>
/// Lazy, shared client for Rogue Gaming's read-only Rust metadata mirror. Only commands and
/// convars are downloaded; the larger item/entity/prefab datasets remain on the website.
/// </summary>
public sealed class RustReferenceService
{
    public const string ReferenceUrl = "https://api.roguegaming.com.au";
    private const string CommandsPath = "data/current/commands.json";
    private const string ConVarsPath = "data/current/convars.json";
    private const string MetadataPath = "data/metadata.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(ReferenceUrl + "/"),
        Timeout = TimeSpan.FromSeconds(12),
    };
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private RustReferenceSnapshot? _memory;

    public RustReferenceService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HighPop-Rust-Manager/1.0.5");
    }

    public async Task<RustReferenceSnapshot> LoadAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!refresh && _memory != null) return _memory;
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _memory != null) return _memory;
            try
            {
                var commandsTask = ReadEntriesAsync(CommandsPath, "Command", cancellationToken);
                var convarsTask = ReadEntriesAsync(ConVarsPath, "ConVar", cancellationToken);
                var metadataTask = ReadMetadataAsync(cancellationToken);
                await Task.WhenAll(commandsTask, convarsTask, metadataTask);

                var entries = commandsTask.Result
                    .Concat(convarsTask.Result)
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var snapshot = new RustReferenceSnapshot(entries, metadataTask.Result, FromCache: false);
                _memory = snapshot;
                await TryWriteCacheAsync(snapshot, cancellationToken);
                return snapshot;
            }
            catch (Exception ex) when (ex is HttpRequestException
                                       or TaskCanceledException
                                       or JsonException
                                       or IOException)
            {
                var cached = await TryReadCacheAsync(cancellationToken);
                if (cached != null)
                {
                    _memory = cached;
                    return cached;
                }
                throw new InvalidOperationException(
                    "The Rogue Rust Reference is unavailable and no local cache exists yet.", ex);
            }
        }
        finally { _loadGate.Release(); }
    }

    private async Task<List<RustReferenceEntry>> ReadEntriesAsync(
        string path,
        string kind,
        CancellationToken cancellationToken)
    {
        await using var stream = await _http.GetStreamAsync(path, cancellationToken);
        var entries = await JsonSerializer.DeserializeAsync<List<RustReferenceEntry>>(
            stream, JsonOptions, cancellationToken) ?? [];
        foreach (var entry in entries)
        {
            entry.Name = UiTextSanitizer.Normalize(entry.Name, UiTextSanitizer.MaxLabelLength);
            entry.Help = UiTextSanitizer.Normalize(entry.Help, 2_048);
            entry.Type = UiTextSanitizer.Normalize(entry.Type, UiTextSanitizer.MaxLabelLength);
            entry.Kind = kind;
            if (entry.DefaultValue.ValueKind == JsonValueKind.Undefined)
                entry.DefaultValue = JsonSerializer.SerializeToElement<object?>(null);
        }
        return entries;
    }

    private async Task<DateTimeOffset?> ReadMetadataAsync(CancellationToken cancellationToken)
    {
        await using var stream = await _http.GetStreamAsync(MetadataPath, cancellationToken);
        var metadata = await JsonSerializer.DeserializeAsync<ReferenceMetadata>(
            stream, JsonOptions, cancellationToken);
        return metadata?.UpdatedAt;
    }

    private static string CachePath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory,
        "assets", "cache", "rogue-rust-reference.json");

    private static async Task TryWriteCacheAsync(
        RustReferenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var cache = new ReferenceCache(snapshot.UpdatedAt, snapshot.Entries.ToList());
            await File.WriteAllTextAsync(CachePath,
                JsonSerializer.Serialize(cache, JsonOptions), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<RustReferenceSnapshot?> TryReadCacheAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var cache = JsonSerializer.Deserialize<ReferenceCache>(
                await File.ReadAllTextAsync(CachePath, cancellationToken), JsonOptions);
            if (cache?.Entries == null || cache.Entries.Count == 0) return null;
            return new RustReferenceSnapshot(cache.Entries, cache.UpdatedAt, FromCache: true);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException) { return null; }
    }

    private sealed record ReferenceCache(
        DateTimeOffset? UpdatedAt,
        List<RustReferenceEntry> Entries);

    private sealed class ReferenceMetadata
    {
        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
