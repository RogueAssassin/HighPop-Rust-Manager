using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace HighPop.Services;

public sealed class RustPlayerModerationRecord
{
    public string ServerId       { get; set; } = string.Empty;
    public string SteamId        { get; set; } = string.Empty;
    public string LastKnownName  { get; set; } = string.Empty;
    public string Notes          { get; set; } = string.Empty;
    public bool   Whitelisted    { get; set; }
    public string LastBanReason  { get; set; } = string.Empty;
    public string LastBanDuration { get; set; } = string.Empty;
    public DateTime? BanExpiresAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string WhitelistText => Whitelisted ? "Whitelist allowed" : "Not whitelisted";

    [JsonIgnore]
    public string NotesPreview => string.IsNullOrWhiteSpace(Notes) ? "No staff notes" : Notes;

    [JsonIgnore]
    public string BanStatusText => BanExpiresAtUtc switch
    {
        null when string.IsNullOrWhiteSpace(LastBanReason) => "No HighPop ban record",
        null => "Permanent ban recorded",
        { } expiry when expiry > DateTime.UtcNow => $"Banned until {expiry.ToLocalTime():g}",
        _ => "Timed ban expired",
    };
}

/// <summary>
/// Local, portable staff metadata for Rust players. This does not replace Rust's own users.cfg
/// or bans database; it records the actions HighPop issued and keeps notes under assets/data.
/// </summary>
public sealed class RustModerationService
{
    private readonly string _file;
    private readonly object _sync = new();
    private List<RustPlayerModerationRecord> _records = [];

    public RustModerationService(ConfigService config) : this(config.AppDataPath) { }

    public RustModerationService(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        _file = Path.Combine(dataDirectory, "rust_player_moderation.json");
        Load();
    }

    public List<RustPlayerModerationRecord> GetRecords(string serverId)
    {
        lock (_sync)
            return _records
                .Where(r => r.ServerId == serverId)
                .OrderByDescending(r => r.UpdatedAtUtc)
                .Select(Clone)
                .ToList();
    }

    public RustPlayerModerationRecord? GetRecord(string serverId, string steamId)
    {
        if (!RustModerationCommands.IsSteamId(steamId)) return null;
        lock (_sync)
        {
            var record = _records.FirstOrDefault(r =>
                r.ServerId == serverId && r.SteamId == steamId);
            return record == null ? null : Clone(record);
        }
    }

    public void SetNote(string serverId, string steamId, string playerName, string notes) =>
        Update(serverId, steamId, playerName, record => record.Notes = CleanNotes(notes));

    public void SetWhitelisted(string serverId, string steamId, string playerName, bool allowed) =>
        Update(serverId, steamId, playerName, record => record.Whitelisted = allowed);

    public void RecordBan(
        string serverId,
        string steamId,
        string playerName,
        string reason,
        string duration,
        DateTime? expiresAtUtc) =>
        Update(serverId, steamId, playerName, record =>
        {
            record.LastBanReason   = CleanNotes(reason);
            record.LastBanDuration = duration;
            record.BanExpiresAtUtc = expiresAtUtc;
        });

    public void RecordUnban(string serverId, string steamId, string playerName) =>
        Update(serverId, steamId, playerName, record =>
        {
            record.LastBanReason   = string.Empty;
            record.LastBanDuration = string.Empty;
            record.BanExpiresAtUtc = null;
        });

    private void Update(
        string serverId,
        string steamId,
        string playerName,
        Action<RustPlayerModerationRecord> update)
    {
        if (string.IsNullOrWhiteSpace(serverId))
            throw new ArgumentException("A server ID is required.", nameof(serverId));
        if (!RustModerationCommands.IsSteamId(steamId))
            throw new ArgumentException("A valid 17-digit Steam64 ID is required.", nameof(steamId));

        lock (_sync)
        {
            var record = _records.FirstOrDefault(r =>
                r.ServerId == serverId && r.SteamId == steamId);
            if (record == null)
            {
                record = new RustPlayerModerationRecord
                {
                    ServerId = serverId,
                    SteamId  = steamId,
                };
                _records.Add(record);
            }

            if (!string.IsNullOrWhiteSpace(playerName))
                record.LastKnownName = RustModerationCommands.CleanQuoted(playerName);
            update(record);
            record.UpdatedAtUtc = DateTime.UtcNow;
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_file)) return;
        try
        {
            _records = JsonConvert.DeserializeObject<List<RustPlayerModerationRecord>>(
                File.ReadAllText(_file)) ?? [];
        }
        catch
        {
            _records = [];
        }
    }

    private void Save()
    {
        var temp = _file + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonConvert.SerializeObject(_records, Formatting.Indented));
            File.Move(temp, _file, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static RustPlayerModerationRecord Clone(RustPlayerModerationRecord source) => new()
    {
        ServerId        = source.ServerId,
        SteamId         = source.SteamId,
        LastKnownName   = source.LastKnownName,
        Notes           = source.Notes,
        Whitelisted     = source.Whitelisted,
        LastBanReason   = source.LastBanReason,
        LastBanDuration = source.LastBanDuration,
        BanExpiresAtUtc = source.BanExpiresAtUtc,
        UpdatedAtUtc    = source.UpdatedAtUtc,
    };

    private static string CleanNotes(string? value)
    {
        var cleaned = (value ?? string.Empty).Trim();
        return cleaned.Length <= 2000 ? cleaned : cleaned[..2000];
    }
}

/// <summary>Validated Rust console commands used by the Stage 2 moderation workspace.</summary>
public static class RustModerationCommands
{
    private static readonly Regex SteamIdPattern = new("^[0-9]{17}$", RegexOptions.Compiled);
    private static readonly Regex DurationTokenPattern = new("([0-9]+)([yMdhms])", RegexOptions.Compiled);

    public static bool IsSteamId(string? value) =>
        value != null && SteamIdPattern.IsMatch(value.Trim());

    public static bool TryBuildBan(
        string steamId,
        string playerName,
        string reason,
        string? duration,
        DateTime nowUtc,
        out string command,
        out string normalizedDuration,
        out DateTime? expiresAtUtc,
        out string error)
    {
        command = normalizedDuration = error = string.Empty;
        expiresAtUtc = null;
        steamId = steamId.Trim();
        if (!IsSteamId(steamId))
        {
            error = "Timed and SteamID bans require a valid 17-digit Steam64 ID.";
            return false;
        }

        if (!TryNormalizeDuration(duration, nowUtc, out normalizedDuration, out expiresAtUtc, out error))
            return false;

        var name = string.IsNullOrWhiteSpace(playerName) ? steamId : CleanQuoted(playerName);
        var why  = string.IsNullOrWhiteSpace(reason) ? "Banned by admin" : CleanQuoted(reason);
        command = $"banid {steamId} \"{name}\" \"{why}\"";
        if (!string.IsNullOrEmpty(normalizedDuration)) command += " " + normalizedDuration;
        return true;
    }

    public static bool TryNormalizeDuration(
        string? input,
        DateTime nowUtc,
        out string normalized,
        out DateTime? expiresAtUtc,
        out string error)
    {
        normalized = error = string.Empty;
        expiresAtUtc = null;
        input = (input ?? string.Empty).Trim();
        if (input.Length == 0
            || input.Equals("permanent", StringComparison.OrdinalIgnoreCase)
            || input.Equals("perm", StringComparison.OrdinalIgnoreCase)
            || input.Equals("forever", StringComparison.OrdinalIgnoreCase))
            return true;
        if (input.Length > 32)
        {
            error = "Ban duration is too long.";
            return false;
        }

        var values = new Dictionary<char, int>();
        var cursor = 0;
        foreach (Match match in DurationTokenPattern.Matches(input))
        {
            if (match.Index != cursor
                || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value <= 0)
            {
                error = "Use a duration such as 30m, 12h, 7d, or 1M7d.";
                return false;
            }
            var unit = match.Groups[2].Value[0];
            if (!values.TryAdd(unit, value))
            {
                error = "Each duration unit can only be used once.";
                return false;
            }
            cursor += match.Length;
        }
        if (cursor != input.Length || values.Count == 0)
        {
            error = "Use a duration such as 30m, 12h, 7d, or 1M7d.";
            return false;
        }

        try
        {
            var expiry = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
            if (values.TryGetValue('y', out var years))   expiry = expiry.AddYears(years);
            if (values.TryGetValue('M', out var months))  expiry = expiry.AddMonths(months);
            if (values.TryGetValue('d', out var days))    expiry = expiry.AddDays(days);
            if (values.TryGetValue('h', out var hours))   expiry = expiry.AddHours(hours);
            if (values.TryGetValue('m', out var minutes)) expiry = expiry.AddMinutes(minutes);
            if (values.TryGetValue('s', out var seconds)) expiry = expiry.AddSeconds(seconds);
            if (expiry > DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).AddYears(10))
            {
                error = "Timed bans are limited to ten years; leave duration blank for a permanent ban.";
                return false;
            }
            expiresAtUtc = expiry;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "Ban duration is outside the supported range.";
            return false;
        }

        var builder = new StringBuilder();
        foreach (var unit in new[] { 'y', 'M', 'd', 'h', 'm', 's' })
            if (values.TryGetValue(unit, out var value)) builder.Append(value).Append(unit);
        normalized = builder.ToString();
        return true;
    }

    public static string RemainingDuration(DateTime expiresAtUtc, DateTime nowUtc)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling((expiresAtUtc - nowUtc).TotalSeconds));
        return $"{seconds}s";
    }

    public static string Unban(string steamId) =>
        IsSteamId(steamId)
            ? $"unban {steamId.Trim()}"
            : throw new ArgumentException("A valid 17-digit Steam64 ID is required.", nameof(steamId));

    public static string GrantWhitelist(string steamId) =>
        IsSteamId(steamId)
            ? $"o.grant user {steamId.Trim()} whitelist.allow"
            : throw new ArgumentException("A valid 17-digit Steam64 ID is required.", nameof(steamId));

    public static string RevokeWhitelist(string steamId) =>
        IsSteamId(steamId)
            ? $"o.revoke user {steamId.Trim()} whitelist.allow"
            : throw new ArgumentException("A valid 17-digit Steam64 ID is required.", nameof(steamId));

    public static string CleanQuoted(string? value)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(c => !char.IsControl(c))
            .ToArray())
            .Replace('"', '\'')
            .Replace(';', ',')
            .Trim();
        return cleaned.Length <= 256 ? cleaned : cleaned[..256];
    }
}
