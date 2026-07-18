using HighPop.Models;

namespace HighPop.Games;

public class RustPlugin : GamePluginBase, IWipePlugin
{
    public override string GameId        => "rust";
    public override string GameName      => "Rust";
    public override string Description   => "Multiplayer survival with base building and PvP";
    public override string Category      => "Survival";
    public override int    SteamAppId    => 258550;
    public override int    GameStoreAppId => 252490;
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

        var args = $"-batchmode -nographics +server.ip {Safe(s.ServerIp)} +server.port {s.ServerPort} " +
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
