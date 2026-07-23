using HighPop.Models;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HighPop.Games;

public class RustPlugin : GamePluginBase, IWipePlugin
{
    private const string ManagedCfgStart       = "# HighPop managed variables - begin";
    private const string ManagedCfgEnd         = "# HighPop managed variables - end";
    private const string LegacyManagedCfgStart = "// HighPop managed variables — begin";
    private const string LegacyManagedCfgEnd   = "// HighPop managed variables — end";
    private static readonly Regex SafeVariableName =
        new(@"^[A-Za-z0-9_.]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ConfigAssignment =
        new(@"^\s*(?<name>[A-Za-z0-9_.]+)\s+(?<value>.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public override string GameId        => "rust";
    public override string GameName      => "Rust";
    public override string Description   => "Multiplayer survival with base building and PvP";
    public override string Category      => "Survival";
    public override int    SteamAppId    => 258550;
    public override string Executable    => "RustDedicated.exe";
    public override int    DefaultPort   => 28015;
    public override int    DefaultQueryPort => 28017;
    public override int    DefaultMaxPlayers => 500;
    public override string SteamBranch   => "public";
    public override bool   HasRcon        => true;
    public override bool   SupportsOxide  => true;

    protected override bool FilterUnityShaderNoise => true;

    public override bool IsNoiseLine(string line) =>
        base.IsNoiseLine(line) ||
        line.StartsWith("Setting breakpad minidump") ||
        line.StartsWith("SteamInternal_SetMinidumpSteamID");

    public override string EngineFamily => RustRcon.Family;
    public override string? GetKickCommand(string p)                         => RustRcon.Kick(p);
    public override string? GetKickCommand(string p, string reason)          => RustRcon.Kick(p, reason);
    public override string? GetBanCommand(string p)                          => RustRcon.Ban(p);
    public override string? GetBanCommand(string p, string reason)           => RustRcon.Ban(p, reason);
    public override string? GetUnbanCommand(string p)                        => RustRcon.Unban(p);
    public override string? GetPlayersCommand()                              => RustRcon.Players();

    public override string BuildStartArguments(GameServer s)
    {
        var seed      = S(s, "seed", "12345");
        var worldSz   = S(s, "worldSize", "4500");
        var tickrate  = S(s, "tickRate", "30");
        var desc      = Safe(S(s, "description", "A HighPop Rust Server"));
        var identity  = SafeIdentity(S(s, "identity", "highpop"));
        var level     = Safe(S(s, "level", "Procedural Map"));
        var levelUrl  = Safe(S(s, "levelUrl", ""));
        var website   = Safe(S(s, "website", ""));
        var header    = Safe(S(s, "headerImage", ""));
        var tags      = Safe(S(s, "tags", "monthly,vanilla"));
        var saveEvery = S(s, "saveInterval", "300");
        var appPort   = S(s, "appPort", (s.RconPort > 0 ? s.RconPort + 67 : s.ServerPort + 68).ToString());

        var fps = S(s, "fpsLimit", "60");
        var logFile = Path.Combine(GetEffectiveLogDirectory(s), "RustDedicated.log");

        var args = $"-batchmode -nographics -logfile \"{Safe(logFile)}\" +server.ip {Safe(s.ServerIp)} +server.port {s.ServerPort} " +
                   $"+server.queryport {s.QueryPort} +app.port {appPort} " +
                   $"+fps.limit {fps} " +
                   $"+server.tickrate {tickrate} +server.hostname \"{Safe(s.ServerName)}\" " +
                   $"+server.maxplayers {s.MaxPlayers} +server.worldsize {worldSz} " +
                   $"+server.seed {seed} +server.identity \"{identity}\" " +
                   $"+server.level \"{level}\" +server.description \"{desc}\" " +
                   $"+server.tags \"{tags}\" +server.saveinterval {saveEvery} +rcon.web 1";

        if (!string.IsNullOrWhiteSpace(website)) args += $" +server.url \"{website}\"";
        if (!string.IsNullOrWhiteSpace(header))  args += $" +server.headerimage \"{header}\"";
        if (!string.IsNullOrWhiteSpace(levelUrl)) args += $" +server.levelurl \"{levelUrl}\"";

        if (s.RconPort > 0)
            args += $" +rcon.port {s.RconPort}";
        if (!string.IsNullOrWhiteSpace(s.RconPassword))
            args += $" +rcon.enabled 1 +rcon.password \"{Safe(s.RconPassword)}\"";

        return args;
    }

    public override Task PreStartAsync(GameServer server)
    {
        Directory.CreateDirectory(GetEffectiveLogDirectory(server));
        WriteManagedServerConfig(server);
        return Task.CompletedTask;
    }

    public static string GetEffectiveLogDirectory(GameServer server)
    {
        if (!string.IsNullOrWhiteSpace(server.LogDirectory))
            return Path.GetFullPath(server.LogDirectory.Trim());

        return Path.Combine(server.InstallPath, "server", GetIdentity(server), "logs");
    }

    public static string GetServerConfigPath(GameServer server) =>
        Path.Combine(server.InstallPath, "server", GetIdentity(server), "cfg", "server.cfg");

    public static string GetLegacyServerAutoPath(GameServer server) =>
        Path.Combine(server.InstallPath, "server", GetIdentity(server), "cfg", "serverauto.cfg");

    /// <summary>
    /// Makes server.cfg authoritative in the Rust workspace. Existing variables are merged into
    /// the HighPop rows and their last active assignment wins, matching Rust's normal cfg behavior.
    /// </summary>
    public static int LoadServerConfigVariables(GameServer server)
    {
        server.RustServerVariables ??= RustServerVariable.CreateDefaults();
        foreach (var variable in server.RustServerVariables)
        {
            variable.LoadedFromServerConfig = false;
            variable.LoadedConfigValue = string.Empty;
        }

        var path = GetServerConfigPath(server);
        if (!File.Exists(path)) return 0;

        var assignments = ParseAssignments(File.ReadAllLines(path)).ToList();
        var legacyManagedBlockExists = HasManagedBlock(
            GetLegacyServerAutoPath(server), LegacyManagedCfgStart);

        // After the v0.3 legacy block is gone, server.cfg is the source of truth. During the
        // one-time migration, retain saved enabled rows so they can move across safely.
        if (!legacyManagedBlockExists)
        {
            foreach (var variable in server.RustServerVariables)
                variable.Enabled = false;
        }

        foreach (var assignment in assignments
                     .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.Last()))
        {
            var variable = server.RustServerVariables.FirstOrDefault(item =>
                string.Equals(item.Name, assignment.Name, StringComparison.OrdinalIgnoreCase));
            if (variable == null)
            {
                variable = new RustServerVariable
                {
                    Name = assignment.Name,
                    Description = "Read from server.cfg",
                };
                server.RustServerVariables.Add(variable);
            }

            variable.Enabled = true;
            variable.Value = assignment.Value;
            variable.LoadedFromServerConfig = true;
            variable.LoadedConfigValue = assignment.Value;
        }

        return assignments
            .Select(a => a.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    /// <summary>
    /// Synchronizes HighPop's variable rows to server.cfg without replacing the operator's file.
    /// Existing comments and unrelated lines remain in place. Only HighPop's previous managed
    /// block is removed from serverauto.cfg during the v0.3 migration.
    /// </summary>
    public static int WriteManagedServerConfig(GameServer server)
    {
        var validation = ValidateServerVariables(server);
        if (validation != null) throw new InvalidDataException(validation);

        var migrated = MigrateLegacyManagedVariables(server);
        var path = GetServerConfigPath(server);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var newline = DetectNewline(existing);
        var lines = StripManagedBlock(
            SplitLines(existing), ManagedCfgStart, ManagedCfgEnd);
        var variables = (server.RustServerVariables ?? [])
            .Where(v => SafeVariableName.IsMatch(v.Name?.Trim() ?? string.Empty))
            .GroupBy(v => v.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(),
                StringComparer.OrdinalIgnoreCase);
        var existingActiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < lines.Count; index++)
        {
            if (!TryParseAssignment(lines[index], out var name, out var currentValue)
                || !variables.TryGetValue(name, out var variable))
                continue;

            if (!variable.Enabled)
            {
                lines[index] = $"# Disabled by HighPop: {lines[index].Trim()}";
                continue;
            }

            existingActiveNames.Add(name);
            if (!string.Equals(currentValue, variable.Value, StringComparison.Ordinal))
                lines[index] = FormatAssignment(variable);
        }

        var append = variables.Values
            .Where(v => v.Enabled && !existingActiveNames.Contains(v.Name.Trim()))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
        if (append.Count > 0)
        {
            if (lines.Count > 0) lines.Add(string.Empty);
            lines.Add(ManagedCfgStart);
            lines.AddRange(append.Select(FormatAssignment));
            lines.Add(ManagedCfgEnd);
        }

        AtomicWrite(path, lines.Count == 0 ? string.Empty : string.Join(newline, lines) + newline);
        LoadServerConfigVariables(server);
        return migrated;
    }

    public override string? GetStopCommand(GameServer server) => "quit";

    public override string? ValidateBeforeStart(GameServer server)
    {
        if (server.ServerPort is <= 0 or > 65535
            || server.QueryPort is <= 0 or > 65535
            || server.RconPort is <= 0 or > 65535)
            return "Game, query, and RCON ports must all be configured.";
        if (server.ServerPort == server.QueryPort || server.ServerPort == server.RconPort || server.QueryPort == server.RconPort)
            return "Rust game, query, and RCON ports must be different.";
        if (string.IsNullOrWhiteSpace(server.RconPassword) || server.RconPassword.Length < 12)
            return "Use an RCON password of at least 12 characters before starting the server.";
        if (!System.Net.IPAddress.TryParse(server.ServerIp, out _))
            return "Server IP must be a valid IPv4 or IPv6 address.";
        if (server.MaxPlayers is < 1 or > 1000)
            return "Maximum players must be between 1 and 1000.";
        if (!int.TryParse(S(server, "worldSize", "4500"), out var world) || world is < 1000 or > 6000)
            return "World size must be between 1000 and 6000.";
        if (!int.TryParse(S(server, "seed", "12345"), out _))
            return "World seed must be a whole number.";
        if (!int.TryParse(S(server, "tickRate", "30"), out var tick) || tick is < 5 or > 60)
            return "Tick rate must be between 5 and 60.";
        if (!int.TryParse(S(server, "fpsLimit", "60"), out var fps) || fps is < 10 or > 256)
            return "FPS limit must be between 10 and 256.";
        if (!int.TryParse(S(server, "saveInterval", "300"), out var saveEvery) || saveEvery is < 60 or > 3600)
            return "Save interval must be between 60 and 3600 seconds.";
        if (!int.TryParse(S(server, "appPort", "28083"), out var appPort) || appPort is <= 0 or > 65535)
            return "Rust+ companion port must be between 1 and 65535.";
        if (appPort == server.ServerPort || appPort == server.QueryPort || appPort == server.RconPort)
            return "Rust+ companion port must differ from game, query, and RCON ports.";
        var levelUrl = S(server, "levelUrl", "");
        if (!string.IsNullOrWhiteSpace(levelUrl)
            && (!Uri.TryCreate(levelUrl, UriKind.Absolute, out var mapUri)
                || mapUri.Scheme is not ("http" or "https")))
            return "Custom map URL must be an absolute HTTP or HTTPS URL.";
        if (!string.IsNullOrWhiteSpace(server.LogDirectory)
            && !Path.IsPathRooted(server.LogDirectory))
            return "Custom log directory must be an absolute path.";
        if (!string.IsNullOrWhiteSpace(server.LogDirectory))
        {
            try { _ = Path.GetFullPath(server.LogDirectory); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return "Custom log directory is not a valid Windows path.";
            }
        }
        return ValidateServerVariables(server);
    }

    public static string? ValidateServerVariables(GameServer server)
    {
        if ((server.RustServerVariables ?? []).Any(v =>
                v.Enabled && !SafeVariableName.IsMatch(v.Name?.Trim() ?? string.Empty)))
            return "Enabled server.cfg variable names may only contain letters, numbers, dots, and underscores.";
        return null;
    }

    // IWipePlugin — Rust saves under server/<identity>/
    public IEnumerable<string> GetMapWipePaths(GameServer server)
    {
        var identity = GetIdentity(server);
        // Map-only wipe preserves blueprint/player databases.
        return [$@"server\{identity}\proceduralmap.*.map",
                $@"server\{identity}\proceduralmap.*.sav"];
    }

    public IEnumerable<string> GetFullWipePaths(GameServer server)
    {
        var identity = GetIdentity(server);
        return GetMapWipePaths(server).Concat([
                $@"server\{identity}\player.blueprints.*.db",
                $@"server\{identity}\player.deaths.*.db",
                $@"server\{identity}\player.identities.*.db",
                $@"server\{identity}\player.states.*.db",
                $@"server\{identity}\player.tokens.*.db"]);
    }

    public override Dictionary<string, string> GetDefaultSettings() => new()
    {
        ["steamBranch"] = "public",
        ["seed"]        = "12345",
        ["worldSize"]   = "4500",
        ["tickRate"]    = "30",
        ["fpsLimit"]    = "60",
        ["description"] = "A HighPop Rust Server",
        ["identity"]    = "highpop",
        ["level"]       = "Procedural Map",
        ["levelUrl"]    = "",
        ["website"]     = "",
        ["headerImage"] = "",
        ["tags"]        = "monthly,vanilla",
        ["saveInterval"] = "300",
        ["appPort"]     = "28083",
    };

    public override List<ConfigField> GetConfigFields()
    {
        var fields = BaseFields();
        fields.AddRange([
            new() { Key = "seed",        Label = "World seed",        FieldType = ConfigFieldType.Number, DefaultValue = "12345" },
            new() { Key = "worldSize",   Label = "World size",        FieldType = ConfigFieldType.Slider, DefaultValue = "4500", Min = 1000, Max = 6000 },
            new() { Key = "tickRate",    Label = "Tick rate",         FieldType = ConfigFieldType.Slider, DefaultValue = "30", Min = 5, Max = 60 },
            new() { Key = "fpsLimit",    Label = "FPS limit (server)", FieldType = ConfigFieldType.Slider, DefaultValue = "60", Min = 10, Max = 256 },
            new() { Key = "description", Label = "Description",       FieldType = ConfigFieldType.Text,   DefaultValue = "A HighPop Rust Server" },
            new() { Key = "identity",    Label = "Server identity (save folder)", FieldType = ConfigFieldType.Text, DefaultValue = "highpop" },
            new() { Key = "level",       Label = "Map level", FieldType = ConfigFieldType.Dropdown, DefaultValue = "Procedural Map", Options = ["Procedural Map", "Barren", "HapisIsland", "SavasIsland_koth"] },
            new() { Key = "levelUrl",    Label = "Custom map URL (optional)", FieldType = ConfigFieldType.Text, DefaultValue = "" },
            new() { Key = "website",     Label = "Community website URL", FieldType = ConfigFieldType.Text, DefaultValue = "" },
            new() { Key = "headerImage", Label = "Server header image URL (512×256)", FieldType = ConfigFieldType.Text, DefaultValue = "" },
            new() { Key = "tags",        Label = "Browser tags", FieldType = ConfigFieldType.Text, DefaultValue = "monthly,vanilla" },
            new() { Key = "saveInterval", Label = "Save interval (seconds)", FieldType = ConfigFieldType.Number, DefaultValue = "300", Min = 60, Max = 3600 },
            new() { Key = "appPort", Label = "Rust+ companion port (TCP)", FieldType = ConfigFieldType.Number, DefaultValue = "28083", Min = 10000, Max = 65535 },
        ]);
        return fields;
    }

    private static string Safe(string? value) => (value ?? string.Empty)
        .Replace("\r", " ").Replace("\n", " ").Replace("\"", "'").Trim();

    private static string SafeCfgValue(string? value) => new string((value ?? string.Empty)
        .Where(c => !char.IsControl(c) && c != ';')
        .ToArray())
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Trim();

    private static int MigrateLegacyManagedVariables(GameServer server)
    {
        var path = GetLegacyServerAutoPath(server);
        if (!File.Exists(path)) return 0;

        var existing = File.ReadAllText(path);
        if (!existing.Contains(LegacyManagedCfgStart, StringComparison.Ordinal)) return 0;

        var newline = DetectNewline(existing);
        var lines = SplitLines(existing);
        var retained = new List<string>();
        var managed = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            if (line.Trim().Equals(LegacyManagedCfgStart, StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }
            if (inside && line.Trim().Equals(LegacyManagedCfgEnd, StringComparison.Ordinal))
            {
                inside = false;
                continue;
            }

            if (inside) managed.Add(line);
            else retained.Add(line);
        }

        var serverConfigNames = File.Exists(GetServerConfigPath(server))
            ? ParseAssignments(File.ReadAllLines(GetServerConfigPath(server)))
                .Select(a => a.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var migrated = 0;
        foreach (var assignment in ParseAssignments(managed)
                     .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.Last()))
        {
            if (serverConfigNames.Contains(assignment.Name)) continue;
            var variable = server.RustServerVariables.FirstOrDefault(item =>
                string.Equals(item.Name, assignment.Name, StringComparison.OrdinalIgnoreCase));
            if (variable == null)
            {
                variable = new RustServerVariable
                {
                    Name = assignment.Name,
                    Description = "Migrated from HighPop's legacy serverauto.cfg block",
                };
                server.RustServerVariables.Add(variable);
            }
            variable.Enabled = true;
            variable.Value = assignment.Value;
            migrated++;
        }

        while (retained.Count > 0 && string.IsNullOrWhiteSpace(retained[^1]))
            retained.RemoveAt(retained.Count - 1);
        AtomicWrite(path, retained.Count == 0
            ? string.Empty
            : string.Join(newline, retained) + newline);
        return migrated;
    }

    private static bool HasManagedBlock(string path, string marker)
    {
        try
        {
            return File.Exists(path)
                   && File.ReadAllText(path).Contains(marker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static List<string> StripManagedBlock(
        IEnumerable<string> source, string startMarker, string endMarker)
    {
        var result = new List<string>();
        var inside = false;
        foreach (var line in source)
        {
            if (line.Trim().Equals(startMarker, StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }
            if (inside && line.Trim().Equals(endMarker, StringComparison.Ordinal))
            {
                inside = false;
                continue;
            }
            if (!inside) result.Add(line);
        }
        return result;
    }

    private static IEnumerable<(string Name, string Value)> ParseAssignments(
        IEnumerable<string> lines)
    {
        foreach (var line in lines)
            if (TryParseAssignment(line, out var name, out var value))
                yield return (name, value);
    }

    private static bool TryParseAssignment(string line, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//"))
            return false;

        var match = ConfigAssignment.Match(line);
        if (!match.Success) return false;
        name = match.Groups["name"].Value;
        var raw = match.Groups["value"].Value.Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            raw = raw[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }
        value = raw;
        return true;
    }

    private static string FormatAssignment(RustServerVariable variable) =>
        $"{variable.Name.Trim()} \"{SafeCfgValue(variable.Value)}\"";

    private static List<string> SplitLines(string content)
    {
        if (content.Length == 0) return [];
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string DetectNewline(string content) =>
        content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    private static string SafeIdentity(string? value)
    {
        var safe = new string(Safe(value).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "highpop" : safe;
    }

    private static string GetIdentity(GameServer server)
        => SafeIdentity(server.GameSpecificSettings.TryGetValue("identity", out var identity)
            ? identity
            : "highpop");
}
