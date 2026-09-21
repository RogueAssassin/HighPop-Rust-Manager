using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HighPop.Models;
using HighPop.Services;

namespace HighPop.Services;

public class ConfigService
{
    private readonly object _serversFileGate = new();
    private string? _lastServersSnapshotHash;
    private readonly string _legacyDefaultInstallRoot;
    private readonly string _portableDefaultInstallRoot;
    static readonly string ExeDir =
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
        ?? AppContext.BaseDirectory;

    public string AppDataPath { get; }
    public string ServersFile { get; }
    public string SettingsFile { get; }
    public string DefaultInstallRoot { get; set; }
    public string BackupPath  { get; set; }
    public string SteamLogin    { get; set; } = string.Empty;
    public string SteamPassword { get; set; } = string.Empty;
    public bool   WebApiEnabled          { get; set; } = false;
    public int    WebApiPort             { get; set; } = 8765;
    public string WebApiToken            { get; set; } = string.Empty;
    public bool   SlaveMode              { get; set; } = false;
    public string SlaveName              { get; set; } = "This Machine";
    public bool   CrashPredictionDiscord { get; set; } = false;
    /// <summary>When true, skip the per-server CPU/RAM heuristics and only warn when system-wide RAM is critically low.</summary>
    public bool   CrashPredictionLowMemOnly { get; set; } = false;
    /// <summary>Free-RAM percentage below which the low-memory warning fires.</summary>
    public double CrashPredictionLowMemPercent { get; set; } = 5.0;
    /// <summary>When true, also warns when overall system CPU usage is critically high.</summary>
    public bool   CrashPredictionHighCpuOnly { get; set; } = false;
    /// <summary>System CPU percentage above which the high-CPU warning fires.</summary>
    public double CrashPredictionHighCpuPercent { get; set; } = 98.0;
    public bool   EnableUPnP             { get; set; } = false;
    public string SortMode               { get; set; } = "name-asc";
    public bool   HasSeenOnboarding      { get; set; } = false;
    public bool   HealthCheckEnabled     { get; set; } = true;
    public int    HealthCheckFailThreshold { get; set; } = 3;   // consecutive failures before action
    public HealthCheckAction HealthCheckAction { get; set; } = HealthCheckAction.Notify;

    /// <summary>True when the Web API must be started — either by user choice or slave mode.</summary>
    public bool WebApiRequired => WebApiEnabled || SlaveMode;

    public ConfigService()
    {
        // HighPop is intentionally portable. Application state lives under assets/** and
        // managed Rust installations live under Servers/** beside HighPop.exe, so the HPRM
        // directory can be moved, backed up, or removed without leaving state in AppData.
        AppDataPath        = Path.Combine(ExeDir, "assets", "data");
        ServersFile        = Path.Combine(AppDataPath, "servers.json");
        SettingsFile       = Path.Combine(AppDataPath, "settings.json");
        _legacyDefaultInstallRoot = Path.Combine(ExeDir, "assets", "servers");
        _portableDefaultInstallRoot = Path.Combine(ExeDir, "Servers");
        DefaultInstallRoot = _portableDefaultInstallRoot;
        BackupPath         = Path.Combine(ExeDir, "assets", "backups");
        Directory.CreateDirectory(AppDataPath);
        PortableLayoutService.MigrateServerRoot(
            _legacyDefaultInstallRoot, _portableDefaultInstallRoot);
        LoadSettings();
        var migratedDefaultRoot = PathsEqual(DefaultInstallRoot, _legacyDefaultInstallRoot);
        if (migratedDefaultRoot) DefaultInstallRoot = _portableDefaultInstallRoot;
        try { Directory.CreateDirectory(DefaultInstallRoot); } catch { }
        // Migrate older configurations that enabled remote control without a token. An empty
        // token would otherwise authenticate an empty Authorization header.
        if (WebApiRequired && string.IsNullOrWhiteSpace(WebApiToken))
        {
            WebApiToken = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            Save();
        }
        Directory.CreateDirectory(BackupPath);
        if (migratedDefaultRoot) Save();
    }

    private record SettingsData(
        string DefaultInstallRoot,
        string SteamLogin,
        string SteamPasswordEncrypted,
        string BackupPath              = "",
        bool   WebApiEnabled           = false,
        int    WebApiPort              = 8765,
        string WebApiTokenEncrypted    = "",
        bool   SlaveMode               = false,
        string SlaveName               = "This Machine",
        bool   CrashPredictionDiscord  = false,
        bool   EnableUPnP             = false,
        string SortMode               = "name-asc",
        bool   CrashPredictionLowMemOnly = false,
        double CrashPredictionLowMemPercent = 5.0,
        bool   CrashPredictionHighCpuOnly = false,
        double CrashPredictionHighCpuPercent = 98.0,
        bool   HasSeenOnboarding = false,
        bool   HealthCheckEnabled = true,
        int    HealthCheckFailThreshold = 3,
        HealthCheckAction HealthCheckAction = HealthCheckAction.Notify);

    private void LoadSettings()
    {
        if (!File.Exists(SettingsFile)) return;
        try
        {
            var d = JsonConvert.DeserializeObject<SettingsData>(File.ReadAllText(SettingsFile));
            if (d == null) return;
            if (!string.IsNullOrEmpty(d.DefaultInstallRoot) && Path.IsPathRooted(d.DefaultInstallRoot))
                DefaultInstallRoot = d.DefaultInstallRoot;
            if (!string.IsNullOrEmpty(d.BackupPath) && Directory.Exists(d.BackupPath))
                BackupPath = d.BackupPath;
            SteamLogin    = d.SteamLogin;
            SteamPassword = string.IsNullOrEmpty(d.SteamPasswordEncrypted)
                ? string.Empty
                : EncryptionService.Decrypt(d.SteamPasswordEncrypted);
            WebApiEnabled          = d.WebApiEnabled;
            WebApiPort             = d.WebApiPort > 0 ? d.WebApiPort : 8765;
            WebApiToken            = Unprotect(d.WebApiTokenEncrypted);
            SlaveMode              = d.SlaveMode;
            SlaveName              = string.IsNullOrEmpty(d.SlaveName) ? "This Machine" : d.SlaveName;
            CrashPredictionDiscord = d.CrashPredictionDiscord;
            EnableUPnP             = d.EnableUPnP;
            SortMode               = string.IsNullOrEmpty(d.SortMode) ? "name-asc" : d.SortMode;
            CrashPredictionLowMemOnly = d.CrashPredictionLowMemOnly;
            CrashPredictionLowMemPercent = d.CrashPredictionLowMemPercent > 0 ? d.CrashPredictionLowMemPercent : 5.0;
            CrashPredictionHighCpuOnly = d.CrashPredictionHighCpuOnly;
            CrashPredictionHighCpuPercent = d.CrashPredictionHighCpuPercent > 0 ? d.CrashPredictionHighCpuPercent : 98.0;
            HasSeenOnboarding = d.HasSeenOnboarding;
            HealthCheckEnabled       = d.HealthCheckEnabled;
            HealthCheckFailThreshold = d.HealthCheckFailThreshold > 0 ? d.HealthCheckFailThreshold : 3;
            HealthCheckAction        = d.HealthCheckAction;
        }
        catch { }
    }

    public void Save()
    {
        var encryptedPassword = string.IsNullOrEmpty(SteamPassword)
            ? string.Empty
            : EncryptionService.Encrypt(SteamPassword);
        var d = new SettingsData(DefaultInstallRoot, SteamLogin, encryptedPassword, BackupPath,
            WebApiEnabled, WebApiPort, Protect(WebApiToken), SlaveMode, SlaveName, CrashPredictionDiscord,
            EnableUPnP, SortMode, CrashPredictionLowMemOnly, CrashPredictionLowMemPercent,
            CrashPredictionHighCpuOnly, CrashPredictionHighCpuPercent, HasSeenOnboarding,
            HealthCheckEnabled, HealthCheckFailThreshold, HealthCheckAction);
        AtomicWrite(SettingsFile, JsonConvert.SerializeObject(d, Formatting.Indented));
    }

    public List<GameServer> LoadServers()
    {
        lock (_serversFileGate)
        {
            if (!File.Exists(ServersFile)) return [];
            try
            {
                var array = JArray.Parse(File.ReadAllText(ServersFile));
                foreach (var server in array.OfType<JObject>())
                {
                    UnprotectProperty(server, nameof(GameServer.RconPassword));
                    UnprotectProperty(server, nameof(GameServer.ServerPassword));
                    UnprotectProperty(server, nameof(GameServer.DiscordWebhookUrl));
                }
                var servers = array.ToObject<List<GameServer>>() ?? [];
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var installPathMigrated = false;
                foreach (var server in servers)
                {
                    if (!Guid.TryParse(server.Id, out _) || !ids.Add(server.Id))
                    {
                        server.Id = Guid.NewGuid().ToString();
                        ids.Add(server.Id);
                    }
                    server.GameSpecificSettings ??= new Dictionary<string, string>();
                    server.RogueRustChannel = ModManagerService.NormalizeRogueRustChannel(server.RogueRustChannel);
                    server.QuickCommands ??= [];
                    server.LogWatchRules ??= [];
                    server.LifecycleOperationHistory ??= [];
                    server.RustServerVariables ??= RustServerVariable.CreateDefaults();
                    var resolvedInstallPath = PortableLayoutService.ResolveInstallPath(
                        server.InstallPath,
                        _legacyDefaultInstallRoot,
                        _portableDefaultInstallRoot);
                    resolvedInstallPath = PortableLayoutService.FlattenGeneratedGamePath(
                        resolvedInstallPath,
                        _portableDefaultInstallRoot,
                        server.GameId);
                    if (!string.Equals(resolvedInstallPath, server.InstallPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        server.InstallPath = resolvedInstallPath;
                        installPathMigrated = true;
                    }
                }
                _lastServersSnapshotHash = installPathMigrated
                    ? null
                    : ComputeServersSnapshotHash(servers);
                return servers;
            }
            catch { return []; }
        }
    }

    public void SaveServers(IEnumerable<GameServer> servers)
    {
        lock (_serversFileGate)
        {
            var snapshot = servers.ToList();
            var snapshotHash = ComputeServersSnapshotHash(snapshot);
            if (string.Equals(snapshotHash, _lastServersSnapshotHash, StringComparison.Ordinal)) return;

            var array = JArray.FromObject(snapshot);
            foreach (var server in array.OfType<JObject>())
            {
                ProtectProperty(server, nameof(GameServer.RconPassword));
                ProtectProperty(server, nameof(GameServer.ServerPassword));
                ProtectProperty(server, nameof(GameServer.DiscordWebhookUrl));
            }
            AtomicWrite(ServersFile, array.ToString(Formatting.Indented));
            _lastServersSnapshotHash = snapshotHash;
        }
    }

    /// <summary>
    /// Persists only lifecycle fields without rewriting unrelated profile data. Stop uses this
    /// synchronously before touching the Rust process so a manager crash cannot resurrect it.
    /// </summary>
    public void PersistServerLifecycle(GameServer server)
    {
        lock (_serversFileGate)
        {
            try
            {
                var array = File.Exists(ServersFile)
                    ? JArray.Parse(File.ReadAllText(ServersFile))
                    : new JArray();
                var target = array.OfType<JObject>().FirstOrDefault(item =>
                    string.Equals(item[nameof(GameServer.Id)]?.Value<string>(), server.Id,
                        StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    target = JObject.FromObject(server);
                    ProtectProperty(target, nameof(GameServer.RconPassword));
                    ProtectProperty(target, nameof(GameServer.ServerPassword));
                    ProtectProperty(target, nameof(GameServer.DiscordWebhookUrl));
                    array.Add(target);
                }

                target[nameof(GameServer.DesiredState)] = (int)server.DesiredState;
                target[nameof(GameServer.LifecyclePhase)] = (int)server.LifecyclePhase;
                target[nameof(GameServer.LifecycleGeneration)] = server.LifecycleGeneration;
                target[nameof(GameServer.LastLifecycleOperationId)] = server.LastLifecycleOperationId;
                target[nameof(GameServer.LastLifecycleReason)] = server.LastLifecycleReason;
                target[nameof(GameServer.LastLifecycleInitiator)] = (int)server.LastLifecycleInitiator;
                target[nameof(GameServer.LastLifecycleTransitionUtc)] =
                    server.LastLifecycleTransitionUtc.HasValue
                        ? JToken.FromObject(server.LastLifecycleTransitionUtc.Value)
                        : JValue.CreateNull();
                target[nameof(GameServer.LifecycleOperationHistory)] =
                    JToken.FromObject(server.LifecycleOperationHistory);
                AtomicWrite(ServersFile, array.ToString(Formatting.Indented));
                _lastServersSnapshotHash = null;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "HighPop could not persist lifecycle intent because servers.json is invalid.", ex);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    "HighPop could not persist lifecycle intent because servers.json is unavailable.", ex);
            }
            catch (UnauthorizedAccessException)
            {
                // Do not let a persistence permission failure turn Stop into a process kill
                // with non-durable intent; RequestStop surfaces this through the coordinator.
                throw;
            }
        }
    }

    private static string Protect(string? value) => string.IsNullOrEmpty(value)
        ? string.Empty
        : "dpapi:" + EncryptionService.Encrypt(value);

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.StartsWith("dpapi:", StringComparison.Ordinal)
            ? EncryptionService.Decrypt(value[6..])
            : value;
    }

    private static void ProtectProperty(JObject obj, string name)
        => obj[name] = Protect(obj[name]?.Value<string>());

    private static void UnprotectProperty(JObject obj, string name)
        => obj[name] = Unprotect(obj[name]?.Value<string>());

    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static string ComputeServersSnapshotHash(IEnumerable<GameServer> servers)
    {
        var json = JArray.FromObject(servers).ToString(Formatting.None);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
