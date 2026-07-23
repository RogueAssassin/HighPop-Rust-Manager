using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HighPop.Models;
using HighPop.Services;

namespace HighPop.Services;

public class ConfigService
{
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
    public bool   OptimizeRamBeforeStart { get; set; } = false;
    public bool   HealthCheckEnabled     { get; set; } = true;
    public int    HealthCheckFailThreshold { get; set; } = 3;   // consecutive failures before action
    public HealthCheckAction HealthCheckAction { get; set; } = HealthCheckAction.Notify;

    /// <summary>True when the Web API must be started — either by user choice or slave mode.</summary>
    public bool WebApiRequired => WebApiEnabled || SlaveMode;

    public ConfigService()
    {
        // HighPop is intentionally portable. Every mutable dependency and user-owned file
        // lives beside HighPop.exe under assets/** so an installation can be moved, backed up,
        // or removed without leaving state in AppData.
        AppDataPath        = Path.Combine(ExeDir, "assets", "data");
        ServersFile        = Path.Combine(AppDataPath, "servers.json");
        SettingsFile       = Path.Combine(AppDataPath, "settings.json");
        DefaultInstallRoot = Path.Combine(ExeDir, "assets", "servers");
        BackupPath         = Path.Combine(ExeDir, "assets", "backups");
        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(DefaultInstallRoot);
        LoadSettings();
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
        bool   OptimizeRamBeforeStart = false,
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
            OptimizeRamBeforeStart   = d.OptimizeRamBeforeStart;
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
            OptimizeRamBeforeStart, HealthCheckEnabled, HealthCheckFailThreshold, HealthCheckAction);
        AtomicWrite(SettingsFile, JsonConvert.SerializeObject(d, Formatting.Indented));
    }

    public List<GameServer> LoadServers()
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
            foreach (var server in servers)
            {
                if (!Guid.TryParse(server.Id, out _) || !ids.Add(server.Id))
                {
                    server.Id = Guid.NewGuid().ToString();
                    ids.Add(server.Id);
                }
                server.GameSpecificSettings ??= new Dictionary<string, string>();
                server.QuickCommands ??= [];
                server.LogWatchRules ??= [];
                server.RustServerVariables ??= RustServerVariable.CreateDefaults();
            }
            return servers;
        }
        catch { return []; }
    }

    public void SaveServers(IEnumerable<GameServer> servers)
    {
        var array = JArray.FromObject(servers);
        foreach (var server in array.OfType<JObject>())
        {
            ProtectProperty(server, nameof(GameServer.RconPassword));
            ProtectProperty(server, nameof(GameServer.ServerPassword));
            ProtectProperty(server, nameof(GameServer.DiscordWebhookUrl));
        }
        AtomicWrite(ServersFile, array.ToString(Formatting.Indented));
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
}
