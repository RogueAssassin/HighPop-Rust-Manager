using System.Text.Json.Serialization;

namespace HighPop.Models;

public class GameServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string GameId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string InstallPath { get; set; } = string.Empty;
    public string ServerIp { get; set; } = "0.0.0.0";
    public int ServerPort { get; set; }
    public int QueryPort { get; set; }
    public int RconPort { get; set; }
    public string RconPassword { get; set; } = string.Empty;
    /// <summary>Connect HighPop's WebRCON client automatically after Rust starts.</summary>
    public bool AutoConnectRcon { get; set; } = true;
    public string ServerPassword { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public int MaxPlayers { get; set; }
    public bool AutoRestart            { get; set; } = false;
    public int  AutoRestartMaxRetries  { get; set; } = 5;    // per 10-min window
    public int  AutoRestartDelaySec    { get; set; } = 10;   // seconds before restart
    public bool AutoUpdate             { get; set; } = false;
    public int  AutoUpdateIntervalMin  { get; set; } = 30;   // minutes between update checks
    public bool AutoStart              { get; set; } = false;
    public bool WakeOnDemand           { get; set; } = false;
    public bool WakeOnDemandPortTrigger { get; set; } = true;
    public bool ShutDownWhenEmpty      { get; set; } = false;
    public int  ShutDownIdleMinutes    { get; set; } = 10;
    public bool UpdateOnStart          { get; set; } = false;
    public bool BackupOnStart          { get; set; } = false;
    public bool BackupOnShutdown       { get; set; } = false;
    public bool   DiscordAlertsEnabled    { get; set; } = true;
    /// <summary>Server-specific Discord webhook URL. Falls back to global setting when empty.</summary>
    public string DiscordWebhookUrl       { get; set; } = string.Empty;
    /// <summary>Server-specific channel for the live Discord status message/board. Falls back to the bot's global status channel when empty.</summary>
    public string DiscordStatusChannelId  { get; set; } = string.Empty;
    /// <summary>Enables Start/Stop/Restart/Backup/Update buttons for this server, posted to DiscordAdminChannelId — a separate, presumably restricted channel, never the public status board.</summary>
    public bool DiscordAdminControls      { get; set; } = false;
    /// <summary>Channel for the admin control buttons. Keep this private/staff-only — anyone who can press these buttons can stop or update the server.</summary>
    public string DiscordAdminChannelId   { get; set; } = string.Empty;
    public bool DailyRestartEnabled    { get; set; } = false;
    public TimeSpan DailyRestartTime   { get; set; } = TimeSpan.FromHours(4); // 04:00 default
    public string CustomArgs { get; set; } = string.Empty;
    public long   CpuAffinityMask  { get; set; } = 0; // 0 = all cores
    public string ProcessPriority  { get; set; } = "Normal";
    public long   MaxRamMb         { get; set; } = 0; // 0 = unlimited
    public bool BackupEnabled      { get; set; } = false;
    public int  BackupRetention    { get; set; } = 5;
    /// <summary>Delete backups older than this many days. 0 = disabled (age-based deletion off).</summary>
    public int  BackupMaxAgeDays   { get; set; } = 0;
    /// <summary>When true, only changed files are backed up after the first full backup.</summary>
    public bool UseIncrementalBackups { get; set; } = false;
    /// <summary>Force a full backup every N backups (bounds how long an incremental chain gets).</summary>
    public int  FullBackupEveryN   { get; set; } = 7;
    /// <summary>
    /// Relative path(s) within InstallPath to backup, separated by semicolons.
    /// If empty, HighPop backs up Rust's server directory.
    /// Example: "server\highpop" or "server\highpop;oxide\data"
    /// </summary>
    public string BackupSavePath   { get; set; } = string.Empty;
    public bool FirewallAutoManage { get; set; } = true;
    /// <summary>
    /// Optional absolute directory for Rust log output. When empty, HighPop uses
    /// server/&lt;identity&gt;/logs beneath the Rust installation.
    /// </summary>
    public string LogDirectory { get; set; } = string.Empty;
    /// <summary>
    /// Operator-facing profile. This does not claim Facepunch "Official" status;
    /// it documents whether this installation is community vanilla, modded, or development.
    /// </summary>
    public string RustServerProfile { get; set; } = "Community (Vanilla)";
    public List<RustServerVariable> RustServerVariables { get; set; } = RustServerVariable.CreateDefaults();
    public Dictionary<string, string> GameSpecificSettings { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastStarted { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    /// <summary>Saved console command shortcuts shown as one-click buttons in the Console tab.</summary>
    public List<QuickCommand> QuickCommands { get; set; } = [];
    /// <summary>Log watcher rules — triggers actions when a keyword appears in the server log.</summary>
    public List<LogWatchRule> LogWatchRules { get; set; } = [];

    /// <summary>
    /// PID of the launched process, persisted to disk so HighPop can reattach to a still-running
    /// server after HighPop itself was closed and reopened. 0 = not running (or not tracked).
    /// </summary>
    public int RunningPid { get; set; }

    [JsonIgnore]
    public ServerStatus Status { get; set; } = ServerStatus.NotInstalled;

    [JsonIgnore]
    public int CurrentPlayers { get; set; }

    [JsonIgnore]
    public TimeSpan Uptime { get; set; }
}

public class RustServerVariable
{
    public bool Enabled { get; set; }
    public string Name  { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    [JsonIgnore]
    public bool LoadedFromServerConfig { get; set; }

    [JsonIgnore]
    public string LoadedConfigValue { get; set; } = string.Empty;

    public static List<RustServerVariable> CreateDefaults() =>
    [
        new() { Name = "bear.population", Value = "2", Description = "Target bear population multiplier." },
        new() { Name = "wolf.population", Value = "2", Description = "Target wolf population multiplier." },
        new() { Name = "server.pve", Value = "false", Description = "Disables player-vs-player damage when true." },
        new() { Name = "decay.scale", Value = "1", Description = "Global building decay multiplier." },
        new() { Name = "server.itemdespawn", Value = "300", Description = "Seconds before dropped items despawn." },
        new() { Name = "server.corpses", Value = "1", Description = "Controls corpse persistence." },
    ];
}

public class QuickCommand
{
    public string Label   { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
}

public enum LogWatchAction { Restart, Stop, SendRcon, Notify }

public class LogWatchRule
{
    public string         Keyword     { get; set; } = string.Empty;
    public LogWatchAction Action      { get; set; } = LogWatchAction.Notify;
    /// <summary>RCON command to send when Action == SendRcon.</summary>
    public string         RconCommand { get; set; } = string.Empty;
    /// <summary>Minimum minutes between triggers for this rule (0 = no cooldown).</summary>
    public int            CooldownMin { get; set; } = 5;
    public bool           Enabled     { get; set; } = true;
}
