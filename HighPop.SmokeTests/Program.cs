using System.IO.Compression;
using HighPop.Games;
using HighPop.Models;
using HighPop.Services;

var failures = new List<string>();

void Check(bool condition, string name)
{
    if (!condition) failures.Add(name);
}

var rust = new RustPlugin();
Check(rust.SteamAppId == 258550, "Rust dedicated server AppID");
Check(rust.DefaultPort == 28015, "Rust game port default");
Check(rust.DefaultQueryPort == 28017, "Rust query port default");
Check(rust.DefaultMaxPlayers == 500, "High-pop player default");

var server = new GameServer
{
    GameId = "rust",
    DisplayName = "HighPop Test",
    ServerName = "HighPop Test",
    ServerIp = "0.0.0.0",
    ServerPort = 28015,
    RconPort = 28016,
    QueryPort = 28017,
    RconPassword = "highpop-test-only-rcon-password",
    MaxPlayers = 500,
    GameSpecificSettings = rust.GetDefaultSettings(),
};

var argsLine = rust.BuildStartArguments(server);
Check(argsLine.Contains("+server.port 28015"), "start args include game port");
Check(argsLine.Contains("+rcon.port 28016"), "start args include WebRCON port");
Check(argsLine.Contains("+server.queryport 28017"), "start args include query port");
Check(argsLine.Contains("+app.port 28083"), "start args include Rust+ port");
Check(argsLine.Contains("+rcon.web 1"), "WebRCON enabled");
Check(rust.ValidateBeforeStart(server) == null, "valid Rust profile accepted");

server.RconPassword = "short";
Check(rust.ValidateBeforeStart(server)?.Contains("12 characters") == true,
    "weak RCON password rejected");
server.RconPassword = "highpop-test-only-rcon-password";

var players = PlayerParserService.ParseRustPlayerList(
    "[{\"DisplayName\":\"Ferris\",\"SteamID\":76561198000000000,\"Ping\":42,\"ConnectedSeconds\":120}]");
Check(players.Count == 1 && players[0].Name == "Ferris" && players[0].Ping == 42,
    "Rust playerlist parser");

var testRoot = Path.Combine(Path.GetTempPath(), "highpop-smoke-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(testRoot);
    server.InstallPath = Path.Combine(testRoot, "server");
    Directory.CreateDirectory(server.InstallPath);

    var banStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var banBuilt = RustModerationCommands.TryBuildBan(
        "76561198000000000", "Ferris", "Cheating; quit", "1M7d5m", banStart,
        out var banCommand, out var banDuration, out var banExpiry, out _);
    Check(banBuilt
          && banCommand == "banid 76561198000000000 \"Ferris\" \"Cheating, quit\" 1M7d5m"
          && banDuration == "1M7d5m"
          && banExpiry == new DateTime(2026, 2, 8, 0, 5, 0, DateTimeKind.Utc),
        "validated native Rust timed-ban command");

    var invalidBan = RustModerationCommands.TryBuildBan(
        "not-a-steamid", "Ferris", "reason", "7d", banStart,
        out _, out _, out _, out _);
    Check(!invalidBan, "invalid SteamID rejected for timed ban");
    Check(RustModerationCommands.GrantWhitelist("76561198000000000")
              == "o.grant user 76561198000000000 whitelist.allow",
        "uMod whitelist grant command");

    var moderationDir = Path.Combine(testRoot, "moderation");
    var moderation = new RustModerationService(moderationDir);
    moderation.SetNote(server.Id, "76561198000000000", "Ferris", "Watch for ban evasion");
    moderation.SetWhitelisted(server.Id, "76561198000000000", "Ferris", allowed: true);
    moderation.RecordBan(server.Id, "76561198000000000", "Ferris", "Cheating",
        banDuration, banExpiry);
    var reloadedModeration = new RustModerationService(moderationDir)
        .GetRecord(server.Id, "76561198000000000");
    Check(reloadedModeration is
          {
              Notes: "Watch for ban evasion",
              Whitelisted: true,
              LastBanDuration: "1M7d5m",
          }, "portable moderation records persist");

    var presetService = new ConfigPresetService();
    var preset = new ConfigPreset
    {
        GameId = "rust",
        Name = "Smoke",
        ConfigFile = @"server\{identity}\cfg\server.cfg",
        Values = new() { ["server.maxplayers"] = "500" },
    };
    var applied = presetService.ApplyPreset(server, preset);
    Check(applied != null && File.Exists(applied), "preset creates config");

    var traversalRejected = false;
    try
    {
        preset.ConfigFile = @"..\escaped.cfg";
        presetService.ApplyPreset(server, preset);
    }
    catch (InvalidDataException) { traversalRejected = true; }
    Check(traversalRejected, "preset path traversal rejected");

    var config = new ConfigService { BackupPath = Path.Combine(testRoot, "backups") };
    var backups = new BackupService(config);
    var maliciousZip = Path.Combine(testRoot, "malicious.zip");
    using (var archive = ZipFile.Open(maliciousZip, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("../escaped.txt");
        await using var writer = new StreamWriter(entry.Open());
        await writer.WriteAsync("blocked");
    }

    var restoreRejected = false;
    try { await backups.RestoreBackupAsync(server, maliciousZip); }
    catch (InvalidDataException) { restoreRejected = true; }
    Check(restoreRejected && !File.Exists(Path.Combine(testRoot, "escaped.txt")),
        "backup ZIP traversal rejected");
}
finally
{
    try { Directory.Delete(testRoot, recursive: true); } catch { }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("HighPop smoke tests failed:");
    foreach (var failure in failures) Console.Error.WriteLine(" - " + failure);
    return 1;
}

Console.WriteLine("HighPop smoke tests passed.");
return 0;
