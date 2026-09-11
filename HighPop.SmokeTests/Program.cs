using System.IO.Compression;
using System.Security.Cryptography;
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
var variableNotifications = 0;
server.RustServerVariables[0].PropertyChanged += (_, _) => variableNotifications++;
server.RustServerVariables[0].Enabled = true;
Check(variableNotifications == 1 && server.RustServerVariables[0].HasUnsavedChanges,
    "Rust variable rows notify the workspace immediately when edited");
server.RustServerVariables[0].Enabled = false;
Check(server.GameSpecificSettings["steamBranch"] == "public",
    "normal Rust profiles default to the public SteamCMD branch");
Check(server.RconAutoConnectDelaySeconds == 60
      && server.RconAutoConnectTimeoutMinutes == 15
      && server.StartupGraceMinutes == 15,
    "slow Rust startup and WebRCON timing defaults");
Check(server.KeepOnline && server.AutoRestart && !server.ShutDownWhenEmpty,
    "production profiles default to always-on recovery without empty-player shutdown");
foreach (var keepOnline in new[] { false, true })
{
    server.KeepOnline = keepOnline;
    server.AutoStart = false;
    Check(!ServerStartupPolicy.ShouldStartOnManagerLaunch(server, reattached: false),
        $"AutoStart off never launches a stopped profile (Always-on: {keepOnline})");
    server.AutoStart = true;
    Check(ServerStartupPolicy.ShouldStartOnManagerLaunch(server, reattached: false)
          && !ServerStartupPolicy.ShouldStartOnManagerLaunch(server, reattached: true),
        $"AutoStart launches only profiles not already reattached (Always-on: {keepOnline})");
}
server.AutoStart = false;
server.KeepOnline = true;

var scheduleReference = new DateTime(2026, 7, 24, 15, 30, 0);
var onceSchedule = new ScheduledTask
{
    Frequency = ScheduleFrequency.Once,
    TimeOfDay = new TimeSpan(16, 0, 0),
};
var dailySchedule = new ScheduledTask
{
    Frequency = ScheduleFrequency.Daily,
    TimeOfDay = new TimeSpan(4, 0, 0),
};
var weeklySchedule = new ScheduledTask
{
    Frequency = ScheduleFrequency.Weekly,
    DayOfWeek = DayOfWeek.Monday,
    TimeOfDay = new TimeSpan(4, 0, 0),
};
var intervalSchedule = new ScheduledTask
{
    Frequency = ScheduleFrequency.Interval,
    IntervalMinutes = 30,
};
Check(ScheduledTaskService.ComputeNextRun(onceSchedule, scheduleReference)
          == new DateTime(2026, 7, 24, 16, 0, 0)
      && ScheduledTaskService.ComputeNextRun(dailySchedule, scheduleReference)
          == new DateTime(2026, 7, 25, 4, 0, 0)
      && ScheduledTaskService.ComputeNextRun(weeklySchedule, scheduleReference)
          == new DateTime(2026, 7, 27, 4, 0, 0)
      && ScheduledTaskService.ComputeNextRun(intervalSchedule, scheduleReference)
          == new DateTime(2026, 7, 24, 16, 0, 0),
    "once, daily, weekly, and interval scheduler next-run calculations");

var argsLine = rust.BuildStartArguments(server);
Check(argsLine.Contains("+server.port 28015"), "start args include game port");
Check(argsLine.Contains("+rcon.port 28016"), "start args include WebRCON port");
Check(argsLine.Contains("+server.queryport 28017"), "start args include query port");
Check(argsLine.Contains("+app.port 28083"), "start args include Rust+ port");
Check(argsLine.Contains("+rcon.web 1"), "WebRCON enabled");
Check(rust.ValidateBeforeStart(server) == null, "valid Rust profile accepted");

const string steamAppInfo =
    "\"branches\"\n" +
    "{\n" +
    "  \"public\"\n" +
    "  {\n" +
    "    \"buildid\" \"20481122\"\n" +
    "    \"timeupdated\" \"1780000000\"\n" +
    "  }\n" +
    "  \"staging\"\n" +
    "  {\n" +
    "    \"buildid\" \"20490001\"\n" +
    "  }\n" +
    "}\n";
Check(SteamCmdService.TryParseBranchBuildId(steamAppInfo, "public", out var publicBuild)
      && publicBuild == "20481122"
      && SteamCmdService.TryParseBranchBuildId(steamAppInfo, "staging", out var stagingBuild)
      && stagingBuild == "20490001",
    "SteamCMD branch build IDs are parsed before auto-update restart");

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

    var oxideManaged = Path.Combine(server.InstallPath, "RustDedicated_Data", "Managed");
    Directory.CreateDirectory(oxideManaged);
    File.Copy(Environment.ProcessPath!, Path.Combine(oxideManaged, "Oxide.Core.dll"));
    var oxideTargets = ModManagerService.GetRogueRustInstallTargets(server.InstallPath);
    Check(oxideTargets.Count == 1
          && oxideTargets[0].Framework == "Oxide/uMod"
          && oxideTargets[0].Directory == oxideManaged,
        "RogueRust targets Oxide's managed directory");
    var carbonManaged = Path.Combine(server.InstallPath, "carbon", "managed");
    Directory.CreateDirectory(carbonManaged);
    File.Copy(Environment.ProcessPath!, Path.Combine(carbonManaged, "Carbon.Common.dll"));
    var dualTargets = ModManagerService.GetRogueRustInstallTargets(server.InstallPath);
    Check(dualTargets.Count == 2
          && dualTargets.Any(target => target.Framework == "Carbon"
              && target.Directory == Path.Combine(server.InstallPath, "carbon", "extensions")),
        "RogueRust targets Carbon extensions and handles dual-framework detection");

    var staleCarbonRoot = Path.Combine(testRoot, "stale-carbon");
    Directory.CreateDirectory(Path.Combine(staleCarbonRoot, "carbon"));
    Check(!ModManagerService.IsCarbonInstalled(staleCarbonRoot),
        "an empty or stale carbon directory is not treated as an active framework");

    var rogueRustBytes = "verified RogueRust test payload"u8.ToArray();
    var rogueRustHash = Convert.ToHexString(SHA256.HashData(rogueRustBytes));
    var originalDlls = dualTargets.ToDictionary(
        target => target.Framework,
        target => System.Text.Encoding.UTF8.GetBytes("original " + target.Framework));
    foreach (var target in dualTargets)
    {
        Directory.CreateDirectory(target.Directory);
        await File.WriteAllBytesAsync(target.DllPath, originalDlls[target.Framework]);
    }

    await ModManagerService.InstallVerifiedRogueRustFilesAsync(
        dualTargets, rogueRustBytes, rogueRustHash);
    Check(dualTargets.All(target => File.ReadAllBytes(target.DllPath).SequenceEqual(rogueRustBytes)),
        "verified RogueRust payload is installed for every detected framework");
    Check(dualTargets.All(target => Directory.GetFiles(
            target.Directory, "Oxide.Ext.RogueRust.dll.bak-*").Any(path =>
                File.ReadAllBytes(path).SequenceEqual(originalDlls[target.Framework]))),
        "RogueRust replacement retains exact rollback copies for every target");

    var checksumRejected = false;
    try
    {
        await ModManagerService.InstallVerifiedRogueRustFilesAsync(
            dualTargets, "tampered"u8.ToArray(), rogueRustHash);
    }
    catch (InvalidDataException) { checksumRejected = true; }
    Check(checksumRejected
          && dualTargets.All(target => File.ReadAllBytes(target.DllPath).SequenceEqual(rogueRustBytes)),
        "RogueRust checksum failure leaves every installed target unchanged");

    foreach (var target in dualTargets)
        await File.WriteAllBytesAsync(target.DllPath, originalDlls[target.Framework]);
    var rollbackReported = false;
    try
    {
        await ModManagerService.InstallVerifiedRogueRustFilesAsync(
            dualTargets, rogueRustBytes, rogueRustHash,
            beforeReplaceForTest: index =>
            {
                if (index == 1) throw new IOException("Injected second-target failure");
            });
    }
    catch (InvalidOperationException ex)
    {
        rollbackReported = ex.Message.Contains("restored", StringComparison.OrdinalIgnoreCase);
    }
    Check(rollbackReported
          && dualTargets.All(target => File.ReadAllBytes(target.DllPath)
              .SequenceEqual(originalDlls[target.Framework]))
          && !dualTargets.SelectMany(target => Directory.GetFiles(
                  target.Directory, "*.highpop-*.tmp"))
              .Any(),
        "a failed dual-framework install restores every target and removes staged files");

    server.RustServerVariables =
    [
        new RustServerVariable
        {
            Enabled = true,
            Name = "bear.population",
            Value = "2",
            Description = "Smoke test",
        },
        new RustServerVariable
        {
            Enabled = false,
            Name = "wolf.population",
            Value = "4",
            Description = "Disabled smoke test",
        },
    ];
    var serverConfigPath = RustPlugin.GetServerConfigPath(server);
    var serverAutoPath = RustPlugin.GetLegacyServerAutoPath(server);
    Directory.CreateDirectory(Path.GetDirectoryName(serverConfigPath)!);
    const string originalServerConfig =
        "# Owner comment is preserved\n" +
        "server.hostname \"Preserved\"\n" +
        "bear.population \"3\"\n" +
        "boar.population \"7\"\n";
    await File.WriteAllTextAsync(serverConfigPath, originalServerConfig);
    await File.WriteAllTextAsync(serverAutoPath,
        "server.writecfg \"true\"\n\n" +
        "// HighPop managed variables — begin\n" +
        "bear.population \"9\"\n" +
        "wolf.population \"4\"\n" +
        "// HighPop managed variables — end\n");

    var loadedVariables = RustPlugin.LoadServerConfigVariables(server);
    var loadedBear = server.RustServerVariables.First(v => v.Name == "bear.population");
    var loadedBoar = server.RustServerVariables.First(v => v.Name == "boar.population");
    Check(loadedVariables == 3
          && loadedBear is { Enabled: true, Value: "3" }
          && loadedBoar is { Enabled: true, Value: "7" },
        "server.cfg variables are loaded into the Rust workspace");

    loadedBear.Value = "2";
    await rust.PreStartAsync(server);
    var serverConfig = await File.ReadAllTextAsync(serverConfigPath);
    var serverAuto = await File.ReadAllTextAsync(serverAutoPath);
    Check(serverConfig.Contains("# Owner comment is preserved")
          && serverConfig.Contains("server.hostname \"Preserved\"")
          && serverConfig.Contains("bear.population \"2\"")
          && serverConfig.Contains("boar.population \"7\"")
          && serverConfig.Contains("wolf.population \"4\""),
        "server.cfg preserves owner content, updates changed rows, and migrates legacy variables");
    Check(serverAuto.Contains("server.writecfg \"true\"")
          && !serverAuto.Contains("HighPop managed variables")
          && !serverAuto.Contains("wolf.population"),
        "legacy HighPop block is removed without replacing owner serverauto.cfg content");

    var configBackupDirectory = Path.Combine(Path.GetDirectoryName(serverConfigPath)!, ".highpop-backups");
    var initialBackups = Directory.GetFiles(configBackupDirectory, "server-*.cfg");
    Check(initialBackups.Any(path => File.ReadAllText(path) == originalServerConfig),
        "server.cfg rollback copy exactly matches the pre-change file");

    var firstWrite = serverConfig;
    var backupCountBeforeNoOp = initialBackups.Length;
    RustPlugin.WriteManagedServerConfig(server);
    Check(await File.ReadAllTextAsync(serverConfigPath) == firstWrite
          && Directory.GetFiles(configBackupDirectory, "server-*.cfg").Length
              == backupCountBeforeNoOp,
        "idempotent server.cfg synchronization does not create another rollback copy");

    for (var index = 0; index < 24; index++)
    {
        server.RustServerVariables.First(v => v.Name == "bear.population").Value = (index + 10).ToString();
        RustPlugin.WriteManagedServerConfig(server);
    }
    Check(Directory.GetFiles(configBackupDirectory, "server-*.cfg").Length == 20,
        "server.cfg rollback retention is capped at 20 copies");

    loadedBoar = server.RustServerVariables.First(v => v.Name == "boar.population");
    loadedBoar.Enabled = false;
    RustPlugin.WriteManagedServerConfig(server);
    var disabledLines = await File.ReadAllLinesAsync(serverConfigPath);
    Check(!disabledLines.Any(line =>
            line.TrimStart().StartsWith("boar.population ", StringComparison.OrdinalIgnoreCase)),
        "disabling a server.cfg variable removes its active assignment");

    server.RustServerVariables.Add(new RustServerVariable
    {
        Enabled = true,
        Name = "unsafe;quit",
        Value = "1",
    });
    Check(rust.ValidateBeforeStart(server)?.Contains("variable names") == true,
        "unsafe server.cfg variable names are rejected");
    server.RustServerVariables.RemoveAt(server.RustServerVariables.Count - 1);

    var customLogs = Path.Combine(testRoot, "custom-logs");
    server.LogDirectory = customLogs;
    await rust.PreStartAsync(server);
    var customLogArgs = rust.BuildStartArguments(server);
    Check(Directory.Exists(customLogs)
          && customLogArgs.Contains(Path.Combine(customLogs, "RustDedicated.log")),
        "custom Rust log directory is created and passed to RustDedicated");
    server.LogDirectory = string.Empty;

    var telemetryRoot = Path.Combine(testRoot, "telemetry");
    var telemetry = new RustTelemetryService(telemetryRoot);
    server.RustTelemetryEnabled = true;
    server.RustTelemetryRetentionDays = 14;
    server.RustTelemetryMaxMegabytes = 16;
    await telemetry.AppendAsync(
        server,
        "server.ready",
        "smoke",
        new Dictionary<string, string> { ["startupSeconds"] = "90" });
    var telemetryDirectory = telemetry.GetServerDirectory(server);
    var telemetryFile = Directory.GetFiles(telemetryDirectory, "*.jsonl").Single();
    var telemetryLine = await File.ReadAllTextAsync(telemetryFile);
    Check(telemetryLine.Contains("\"SchemaVersion\":\"highpop.rust.event/v1\"")
          && telemetryLine.Contains("\"Name\":\"server.ready\""),
        "versioned local Rust telemetry event schema");

    var expiredTelemetryFile = Path.Combine(telemetryDirectory, "2000-01-01.jsonl");
    await File.WriteAllTextAsync(expiredTelemetryFile, "{}");
    File.SetLastWriteTimeUtc(expiredTelemetryFile, DateTime.UtcNow.AddDays(-30));
    telemetry.Prune(server);
    Check(!File.Exists(expiredTelemetryFile),
        "local Rust telemetry retention removes expired event files");
    server.RustTelemetryEnabled = false;

    var oxidePlugins = Path.Combine(server.InstallPath, "oxide", "plugins");
    var carbonPlugins = Path.Combine(server.InstallPath, "carbon", "plugins");
    Directory.CreateDirectory(oxidePlugins);
    Directory.CreateDirectory(carbonPlugins);
    await File.WriteAllTextAsync(Path.Combine(oxidePlugins, "Kits.cs"), "// smoke");
    await File.WriteAllTextAsync(Path.Combine(oxidePlugins, "Inactive.cs.off"), "// smoke");
    await File.WriteAllTextAsync(Path.Combine(carbonPlugins, "Economics.dll"), "smoke");
    var plugins = ModManagerService.GetInstalledPlugins(server.InstallPath);
    Check(plugins.Count == 3
          && plugins.Any(p => p.Name == "Kits" && p.Framework == "Oxide" && p.IsEnabled)
          && plugins.Any(p => p.Name == "Inactive" && !p.IsEnabled)
          && ModManagerService.GetDetectedFramework(server.InstallPath).Contains("Carbon"),
        "Oxide/Carbon framework and plugin inventory");

    var oxideConfig = Path.Combine(server.InstallPath, "oxide", "config");
    var carbonConfig = Path.Combine(server.InstallPath, "carbon", "configs");
    Directory.CreateDirectory(oxideConfig);
    Directory.CreateDirectory(carbonConfig);
    await File.WriteAllTextAsync(Path.Combine(oxideConfig, "Kits.json"), "{}");
    await File.WriteAllTextAsync(Path.Combine(carbonConfig, "Economics.json"), "{}");
    var configFiles = new ConfigEditorService(new ConfigService()).FindConfigs(server, rust);
    Check(configFiles.Any(c => c.DisplayName == "Oxide plugin • Kits.json"
                               && c.ReloadCommand == "o.reload Kits")
          && configFiles.Any(c => c.DisplayName == "Carbon plugin • Economics.json"
                                  && c.ReloadCommand == "c.reload Economics"),
        "plugin config discovery and framework-specific reload commands");

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
