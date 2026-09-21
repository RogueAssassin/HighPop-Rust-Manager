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

using (var disconnectedRcon = new RconService())
{
    await disconnectedRcon.DisconnectAsync();
    Check(!disconnectedRcon.IsConnected,
        "WebRCON disconnect is safe and awaitable when no socket is active");
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

var firstPortSet = new ServerPortSet(28015, 28017, 28016, 28083);
var secondPortSet = ServerPortAllocator.FindAvailable(
    firstPortSet, [firstPortSet]);
Check(secondPortSet == new ServerPortSet(28018, 28020, 28019, 28086),
    "new servers receive a complete non-conflicting Game/Query/WebRCON/Rust+ port set");
Check(ServerPortAllocator.Validate(
        new ServerPortSet(29015, 29017, 29016, 29083), [firstPortSet]).Count == 0
      && ServerPortAllocator.Validate(
        new ServerPortSet(28015, 29017, 29016, 29083), [firstPortSet]).Count > 0,
    "manual port editing accepts unique sets and rejects ports reserved by another profile");
var profilePortErrors = ServerPortAllocator.ValidateProfiles([
    ("server-a", "Server A", firstPortSet),
    ("server-b", "Server B", new ServerPortSet(28015, 28117, 28116, 28183)),
]);
Check(profilePortErrors.Any(error => error.Contains("Port 28015", StringComparison.Ordinal)),
    "profile-wide validation rejects a port collision before autosave");
var allocatedPortSets = new List<ServerPortSet>();
for (var index = 0; index < 4; index++)
{
    var allocated = ServerPortAllocator.FindAvailable(firstPortSet, allocatedPortSets);
    if (allocated.HasValue) allocatedPortSets.Add(allocated.Value);
}
Check(allocatedPortSets.Count == 4
      && ServerPortAllocator.ValidateProfiles(allocatedPortSets.Select((ports, index) =>
          ($"server-{index}", $"Server {index + 1}", ports))).Count == 0,
    "multi-server allocation matrix produces independent four-port profiles");

var normalStderr = ConsoleOutputParser.Parse("Server startup complete", fromStandardError: true);
var exceptionLine = ConsoleOutputParser.Parse(
    "NullReferenceException: object reference not set", fromStandardError: false);
var stackLine = ConsoleOutputParser.Parse(
    "   at Oxide.Plugins.Test.Run()", fromStandardError: false, ConsoleMessageType.Error);
var rogueLine = ConsoleOutputParser.Parse(
    "\u001b[31m[RogueRust] readiness probe failed\u001b[0m", fromStandardError: false);
Check(normalStderr?.Type == ConsoleMessageType.Info
      && normalStderr.Source == "Rust stderr"
      && exceptionLine?.Type == ConsoleMessageType.Error
      && stackLine?.Type == ConsoleMessageType.Error
      && rogueLine?.Source == "RogueRust"
      && rogueLine.Text == "[RogueRust] readiness probe failed",
    "console parsing preserves stream source, stack severity, and strips ANSI output");
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

var lifecycleServer = new GameServer
{
    Id = Guid.NewGuid().ToString(),
    KeepOnline = true,
    AutoRestart = true,
    DesiredState = ServerDesiredState.Unspecified,
    LifecyclePhase = ServerLifecyclePhase.Unknown,
};
ServerLifecycleRules.InitializeAfterLoad(lifecycleServer, reattached: false);
Check(lifecycleServer.DesiredState == ServerDesiredState.Stopped
      && lifecycleServer.LifecyclePhase == ServerLifecyclePhase.StoppedByOperator
      && !ServerLifecycleRules.CanRecover(lifecycleServer),
    "unattached profiles migrate to durable stopped intent and cannot recover implicitly");

lifecycleServer.DesiredState = ServerDesiredState.Running;
lifecycleServer.LifecyclePhase = ServerLifecyclePhase.Online;
lifecycleServer.RunningPid = 0;
ServerLifecycleRules.InitializeAfterLoad(lifecycleServer, reattached: false);
Check(lifecycleServer.DesiredState == ServerDesiredState.Running
      && lifecycleServer.LifecyclePhase == ServerLifecyclePhase.Recovering
      && ServerLifecycleRules.CanRecover(lifecycleServer),
    "persisted running intent survives a stale process identity and resumes recovery");

lifecycleServer.RunningPid = 1234;
ServerLifecycleRules.InitializeAfterLoad(lifecycleServer, reattached: true);
Check(lifecycleServer.DesiredState == ServerDesiredState.Running
      && lifecycleServer.LifecyclePhase == ServerLifecyclePhase.Online
      && ServerLifecycleRules.CanRecover(lifecycleServer),
    "verified process reattachment establishes desired running state");

lifecycleServer.LifecycleGeneration = 7;
Check(ServerLifecycleRules.IsCurrent(lifecycleServer, 7)
      && !ServerLifecycleRules.IsCurrent(lifecycleServer, 6),
    "lifecycle generations reject callbacks from superseded operations");
lifecycleServer.DesiredState = ServerDesiredState.Stopped;
Check(!ServerLifecycleRules.CanRecover(lifecycleServer),
    "manual stopped intent overrides Always-on and Auto-restart");

Check(ServerLifecycleRules.DefaultDeadline(lifecycleServer, ServerLifecyclePhase.Stopping)
          >= TimeSpan.FromSeconds(lifecycleServer.GracefulStopTimeoutSeconds)
      && ServerLifecycleRules.StatusForPhase(ServerLifecyclePhase.Faulted)
          == LifecycleOperationStatus.Failed
      && ServerLifecycleRules.StatusForPhase(ServerLifecyclePhase.RconReady)
          == LifecycleOperationStatus.Succeeded,
    "lifecycle operation deadlines and terminal results are deterministic");
var firstRconDelay = RconReconnectPolicy.GetDelay(1, 1.0);
var lateRconDelay = RconReconnectPolicy.GetDelay(20, 1.2);
Check(firstRconDelay == TimeSpan.FromSeconds(5)
      && lateRconDelay <= TimeSpan.FromSeconds(54),
    "WebRCON reconnect backoff starts promptly and remains bounded with jitter");
var diagnosticText = "password=rust-secret token:api-secret "
    + "https://discord.com/api/webhooks/123/secret "
    + "ws://127.0.0.1:28016/rcon-secret dpapi:YWJjZA==";
var redactedDiagnosticText = SupportBundleService.Redact(diagnosticText);
Check(!redactedDiagnosticText.Contains("rust-secret", StringComparison.Ordinal)
      && !redactedDiagnosticText.Contains("api-secret", StringComparison.Ordinal)
      && !redactedDiagnosticText.Contains("/123/secret", StringComparison.Ordinal)
      && !redactedDiagnosticText.Contains("rcon-secret", StringComparison.Ordinal)
      && !redactedDiagnosticText.Contains("YWJjZA", StringComparison.Ordinal),
    "support bundle redacts passwords, tokens, webhooks, WebRCON credentials, and DPAPI values");

var signalServer = new GameServer { DisplayName = "Signal Test" };
var signalInstance = new ServerInstance(signalServer);
signalInstance.MarkProcessObserved();
signalInstance.TryMarkReady("Rust startup log");
signalInstance.TryMarkReady("WebRCON connected");
signalInstance.MarkPlayerSample();
Check(signalInstance.ProcessObservedUtc.HasValue
      && signalInstance.RustReadyUtc.HasValue
      && signalInstance.RconReadyUtc.HasValue
      && signalInstance.LastPlayerSampleUtc.HasValue,
    "process, Rust, WebRCON, and player freshness signals are tracked independently");

Check(WindowsStartupTaskService.BuildTaskAction(@"C:\Program Files\HighPop\HighPop.exe")
          == "\"C:\\Program Files\\HighPop\\HighPop.exe\" --background",
    "Windows logon task safely quotes the executable and starts in background mode");

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

Check(ServerMaintenancePolicy.ClampGracefulStopTimeout(1) == 15
      && ServerMaintenancePolicy.ClampGracefulStopTimeout(120) == 120
      && ServerMaintenancePolicy.ClampGracefulStopTimeout(5000) == 600,
    "safe-stop deadlines remain within the supported 15–600 second range");
var fiveMinuteUpdateCountdown = ServerMaintenancePolicy.GetUpdateCountdownSeconds(5);
Check(fiveMinuteUpdateCountdown.SequenceEqual(new[] { 300, 180, 60, 30, 10 })
      && ServerMaintenancePolicy.FormatCountdown(60) == "1 minute"
      && ServerMaintenancePolicy.FormatCountdown(30) == "30 seconds",
    "safe-update countdown emits deterministic in-game warning checkpoints");

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
    var legacyServerRoot = Path.Combine(testRoot, "assets", "servers");
    var portableServerRoot = Path.Combine(testRoot, "Servers");
    var legacyServerPath = Path.Combine(legacyServerRoot, "server-one");
    Directory.CreateDirectory(legacyServerPath);
    File.WriteAllText(Path.Combine(legacyServerPath, "identity.txt"), "server-one");
    var layoutMigration = PortableLayoutService.MigrateServerRoot(
        legacyServerRoot, portableServerRoot);
    Check(layoutMigration.MovedEntries == 1
          && layoutMigration.Conflicts == 0
          && File.Exists(Path.Combine(portableServerRoot, "server-one", "identity.txt"))
          && PortableLayoutService.ResolveInstallPath(
              legacyServerPath, legacyServerRoot, portableServerRoot)
              == Path.Combine(portableServerRoot, "server-one"),
        "portable layout safely migrates assets/servers profiles into top-level Servers");

    var conflictingLegacyPath = Path.Combine(legacyServerRoot, "conflict");
    var conflictingPortablePath = Path.Combine(portableServerRoot, "conflict");
    Directory.CreateDirectory(conflictingLegacyPath);
    Directory.CreateDirectory(conflictingPortablePath);
    var conflictMigration = PortableLayoutService.MigrateServerRoot(
        legacyServerRoot, portableServerRoot);
    Check(conflictMigration.Conflicts == 1
          && Directory.Exists(conflictingLegacyPath)
          && PortableLayoutService.ResolveInstallPath(
              conflictingLegacyPath, legacyServerRoot, portableServerRoot)
              == conflictingLegacyPath,
        "portable layout preserves both installations and the legacy path on a name conflict");

    var nestedGeneratedPath = Path.Combine(portableServerRoot, "rust", "OxideTest");
    Directory.CreateDirectory(nestedGeneratedPath);
    File.WriteAllText(Path.Combine(nestedGeneratedPath, "RustDedicated.exe"), "test");
    var flattenedGeneratedPath = PortableLayoutService.FlattenGeneratedGamePath(
        nestedGeneratedPath, portableServerRoot, "rust");
    Check(flattenedGeneratedPath == Path.Combine(portableServerRoot, "OxideTest")
          && File.Exists(Path.Combine(flattenedGeneratedPath, "RustDedicated.exe"))
          && !Directory.Exists(Path.Combine(portableServerRoot, "rust")),
        "generated Servers/rust/name paths flatten to Servers/name without data loss");

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
    var oxidePaths = ModManagerService.GetActiveFrameworkPaths(server.InstallPath);
    var missingCarbonPlugins = Path.Combine(server.InstallPath, "carbon", "plugins");
    Check(oxidePaths?.Framework == "Oxide / uMod"
          && oxidePaths.PluginDirectory == Path.Combine(server.InstallPath, "oxide", "plugins")
          && !ModManagerService.OpenExistingFolder(missingCarbonPlugins)
          && !Directory.Exists(missingCarbonPlugins),
        "framework folder shortcuts select Oxide and never create a missing Carbon folder");
    var carbonManaged = Path.Combine(server.InstallPath, "carbon", "managed");
    Directory.CreateDirectory(carbonManaged);
    File.Copy(Environment.ProcessPath!, Path.Combine(carbonManaged, "Carbon.Common.dll"));
    var dualTargets = ModManagerService.GetRogueRustInstallTargets(server.InstallPath);
    Check(dualTargets.Count == 2
          && dualTargets.Any(target => target.Framework == "Carbon"
              && target.Directory == Path.Combine(server.InstallPath, "carbon", "extensions")),
        "RogueRust targets Carbon extensions and handles dual-framework detection");
    Check(ModManagerService.GetActiveFrameworkPaths(server.InstallPath) == null,
        "framework folder shortcuts refuse an ambiguous dual-framework installation");

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
    const string originalServerAuto =
        "server.writecfg \"true\"\n\n" +
        "// HighPop managed variables — begin\n" +
        "bear.population \"9\"\n" +
        "wolf.population \"4\"\n" +
        "// HighPop managed variables — end\n";
    await File.WriteAllTextAsync(serverConfigPath, originalServerConfig);
    await File.WriteAllTextAsync(serverAutoPath, originalServerAuto);

    var loadedVariables = RustPlugin.LoadServerConfigVariables(server);
    var loadedBear = server.RustServerVariables.First(v => v.Name == "bear.population");
    var loadedBoar = server.RustServerVariables.First(v => v.Name == "boar.population");
    Check(loadedVariables == 3
          && loadedBear is { Enabled: true, Value: "3" }
          && loadedBoar is { Enabled: true, Value: "7" },
        "server.cfg variables are loaded into the Rust workspace");

    server.RustServerVariables.Add(new RustServerVariable
    {
        Enabled = true,
        Name = "BEAR.POPULATION",
        Value = "999",
        Description = "Persisted duplicate",
    });
    await File.WriteAllTextAsync(serverConfigPath,
        originalServerConfig +
        "bear.population \"11\"\n" +
        "BEAR.POPULATION \"12\"\n");
    loadedVariables = RustPlugin.LoadServerConfigVariables(server);
    var bearRows = server.RustServerVariables
        .Where(v => v.Name.Equals("bear.population", StringComparison.OrdinalIgnoreCase))
        .ToList();
    Check(loadedVariables == 3
          && bearRows.Count == 1
          && bearRows[0] is { Enabled: true, Value: "12", LoadedConfigValue: "12" },
        "server.cfg reload collapses duplicate rows and uses the latest active assignment");

    bearRows[0].Value = "2";
    RustPlugin.WriteManagedServerConfig(server);
    var deduplicatedConfig = await File.ReadAllLinesAsync(serverConfigPath);
    Check(deduplicatedConfig.Count(line =>
              line.TrimStart().StartsWith("bear.population ", StringComparison.OrdinalIgnoreCase)) == 1
          && deduplicatedConfig.Any(line => line == "BEAR.POPULATION \"2\"")
          && deduplicatedConfig.Count(line => line.Contains("Duplicate removed by HighPop:",
              StringComparison.Ordinal)) == 2,
        "server.cfg save leaves one authoritative active assignment and comments older duplicates");

    // Restore the original fixture so the rollback/idempotence assertions below remain exact.
    await File.WriteAllTextAsync(serverConfigPath, originalServerConfig);
    await File.WriteAllTextAsync(serverAutoPath, originalServerAuto);
    RustPlugin.LoadServerConfigVariables(server);
    loadedBear = server.RustServerVariables.First(v =>
        v.Name.Equals("bear.population", StringComparison.OrdinalIgnoreCase));
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

    server.RustServerVariables.Add(new RustServerVariable
    {
        Enabled = true,
        Name = "BEAR.POPULATION",
        Value = "10",
    });
    Check(rust.ValidateBeforeStart(server)?.Contains("only once") == true,
        "duplicate server.cfg variable rows are rejected case-insensitively");
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
