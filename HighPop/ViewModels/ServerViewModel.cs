using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighPop.Games;
using HighPop.Models;
using HighPop.Services;

namespace HighPop.ViewModels;

public partial class ServerViewModel : BaseViewModel, IDisposable
{
    private readonly ServerManagerService  _manager;
    private readonly SteamCmdService       _steamCmd;
    private readonly BackupService         _backup;
    private readonly NotificationService   _notifications;
    private readonly PerformanceMonitorService _perfMonitor;
    private readonly ConfigService         _config;
    private readonly ModManagerService     _mods;
    private readonly ConfigEditorService   _configEditor;
    private readonly PlayerStatsService    _playerStats;
    private readonly PerfHistoryService    _perfHistory;
    private readonly TemplateService        _templates;
    private readonly ScheduledTaskService   _scheduler;
    private readonly NetworkMonitorService  _network;
    private readonly GroupBanListService    _groupBans;
    private readonly RustModerationService  _moderation;
    private readonly ServerHygieneService   _hygiene;
    private readonly ConfigPresetService    _presets;
    private RconService? _rcon;
    private readonly SemaphoreSlim _rconLock  = new(1, 1);
    private readonly object        _perfLock  = new();
    private System.Timers.Timer?   _updateTimer;

    public GameServer Server { get; }
    public IGamePlugin? Plugin { get; }
    private const int MaxConsoleLines = 2000;
    public ObservableCollection<ConsoleMessage> Log { get; } = [];
    public ObservableCollection<BackupEntry> Backups { get; } = [];
    public ObservableCollection<string> ActionLog { get; } = [];

    [ObservableProperty] private int _serverNumber;

    [ObservableProperty] private string _consoleInput = string.Empty;
    [ObservableProperty] private int _installProgress;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private long _memoryMb;
    [ObservableProperty] private bool _rconConnected;
    [ObservableProperty] private string _consoleFilter   = string.Empty;
    [ObservableProperty] private string _modStatusText   = string.Empty;
    [ObservableProperty] private bool   _modBusy;

    // Config editor
    [ObservableProperty] private List<Services.ConfigFileEntry> _configFiles = [];
    [ObservableProperty] private Services.ConfigFileEntry? _selectedConfigFile;
    [ObservableProperty] private string _configContent = string.Empty;

    // Config Presets
    [ObservableProperty] private List<Services.ConfigPreset> _availablePresets = [];
    [ObservableProperty] private Services.ConfigPreset? _selectedPreset;

    // Players — reaaliaikainen lista
    [ObservableProperty] private List<Models.OnlinePlayer>    _onlinePlayers  = [];
    [ObservableProperty] private List<Services.PlayerSession> _playerHistory  = [];
    [ObservableProperty] private List<Services.PlayerStats>   _playerStatsList = [];
    [ObservableProperty] private List<Services.PlayerStats>   _mostActivePlayers = [];
    [ObservableProperty] private List<Services.RustPlayerModerationRecord> _moderationProfiles = [];
    [ObservableProperty] private List<HourBar> _hourlyActivity = [];

    public class HourBar
    {
        public int Hour    { get; set; }
        public int Count   { get; set; }
        public double HeightFraction { get; set; } // 0..1, relative to the busiest hour
        public string Label => $"{Hour:D2}";
    }

    private void RefreshActivityStats()
    {
        MostActivePlayers = _playerStats.GetMostActivePlayers(Server.Id, 30, 10);
        var hourly = _playerStats.GetHourlyActivity(Server.Id);
        var max = Math.Max(1, hourly.Max());
        HourlyActivity = Enumerable.Range(0, 24)
            .Select(h => new HourBar { Hour = h, Count = hourly[h], HeightFraction = hourly[h] / (double)max })
            .ToList();
    }
    [ObservableProperty] private string _kickReason  = string.Empty;
    [ObservableProperty] private string _banReason   = string.Empty;
    [ObservableProperty] private string _banDuration = string.Empty;
    [ObservableProperty] private string _playerNote  = string.Empty;
    [ObservableProperty] private string _moderationStatus = string.Empty;

    private System.Timers.Timer? _playerRefreshTimer;

    // Performance history
    [ObservableProperty] private OxyPlot.PlotModel _perfPlot = CreateEmptyPlot();
    private OxyPlot.PlotModel?          _perfModel;
    private OxyPlot.Series.LineSeries?  _cpuSeries;
    private OxyPlot.Series.LineSeries?  _memSeries;
    [ObservableProperty] private int _perfRangeMinutes = 15;
    partial void OnPerfRangeMinutesChanged(int _) => UpdatePerfChart();
    public IReadOnlyList<int> PerfRangeOptions { get; } = [5, 15, 30, 60];

    // Scheduled tasks
    [ObservableProperty] private List<Services.ScheduledTask> _scheduledTasks = [];

    // Resource limits
    [ObservableProperty] private long _maxRamMb;
    partial void OnMaxRamMbChanged(long value) => Server.MaxRamMb = value;

    // Backup retention
    [ObservableProperty] private int _backupRetention;
    partial void OnBackupRetentionChanged(int value) => Server.BackupRetention = value;
    [ObservableProperty] private int _backupMaxAgeDays;
    partial void OnBackupMaxAgeDaysChanged(int value) => Server.BackupMaxAgeDays = value;
    [ObservableProperty] private bool _useIncrementalBackups;
    partial void OnUseIncrementalBackupsChanged(bool value) => Server.UseIncrementalBackups = value;
    [ObservableProperty] private int _fullBackupEveryN;
    partial void OnFullBackupEveryNChanged(int value) => Server.FullBackupEveryN = value;

    public bool HasPlayerCommands => Plugin?.GetPlayersCommand() != null
                                  || Plugin?.GetKickCommand("") != null;

    public FileBrowserViewModel FileBrowser { get; } = new();
    public List<Models.ServerTemplate> GameTemplates => FilteredTemplates;

    // ── Template filter ───────────────────────────────────────────────────────
    [ObservableProperty] private string _templateFilterCategory = string.Empty;
    [ObservableProperty] private string _templateFilterTag      = string.Empty;

    partial void OnTemplateFilterCategoryChanged(string _) => OnPropertyChanged(nameof(FilteredTemplates));
    partial void OnTemplateFilterTagChanged(string _)      => OnPropertyChanged(nameof(FilteredTemplates));

    public List<Models.ServerTemplate> FilteredTemplates
    {
        get
        {
            var all = _templates.ForGame(Server.GameId);
            if (!string.IsNullOrWhiteSpace(TemplateFilterCategory))
                all = all.Where(t => t.Category.Equals(TemplateFilterCategory,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(TemplateFilterTag))
                all = all.Where(t => t.Tags.Contains(TemplateFilterTag,
                    StringComparer.OrdinalIgnoreCase)).ToList();
            return all.ToList();
        }
    }

    public List<string> AvailableCategories =>
        _templates.ForGame(Server.GameId)
                  .Select(t => t.Category)
                  .Where(c => !string.IsNullOrWhiteSpace(c))
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .Prepend("(all)")
                  .ToList();

    private System.Timers.Timer? _perfTimer;

    public bool HasModSupport       => Plugin?.SupportsOxide == true;
    public bool IsRust              => Plugin?.GameId == "rust";

    public List<CpuCoreItem> CpuCores { get; }
    public string[] PriorityOptions { get; } = ["Normal", "AboveNormal", "High", "BelowNormal", "RealTime"];

    // ── Batch selection ───────────────────────────────────────────────────────
    [ObservableProperty] private bool _isBatchSelected;
    internal Action? BatchSelectionChanged { get; set; }
    partial void OnIsBatchSelectedChanged(bool _) => BatchSelectionChanged?.Invoke();

    public string StatusColor => Server.Status switch
    {
        ServerStatus.Running      => "#3FB950",
        ServerStatus.Starting     => "#F05A28",
        ServerStatus.Stopping     => "#F05A28",
        ServerStatus.Stopped      => "#8B949E",
        ServerStatus.Installing   => "#F05A28",
        ServerStatus.Updating     => "#F05A28",
        ServerStatus.Error        => "#F85149",
        ServerStatus.NotInstalled => "#8B949E",
        _                         => "#8B949E",
    };

    public bool IsRunning    => Server.Status == ServerStatus.Running;
    public bool IsStopped    => Server.Status is ServerStatus.Stopped or ServerStatus.NotInstalled;
    public bool CanStart     => Server.Status is ServerStatus.Stopped or ServerStatus.Error or ServerStatus.NotInstalled;
    public bool CanStop      => Server.Status is ServerStatus.Running or ServerStatus.Starting;
    public bool HasRcon => Plugin?.HasRcon == true;

    public bool ShowVersionInfo => Plugin?.SteamAppId > 0;

    public string InstalledVersionText
    {
        get
        {
            if (Plugin == null) return "—";
            return Plugin.GetSteamInstalledBuildId(Server) ?? "Not installed yet";
        }
    }

    private string CustomImagePath => Path.Combine(
        _config.AppDataPath, "server_images", Server.Id + ".png");

    public bool HasCustomImage => File.Exists(CustomImagePath);

    public string GameImageUrl
    {
        get
        {
            if (HasCustomImage)
                return CustomImagePath;
            return "pack://application:,,,/HighPop;component/assets/brand/highpop.png";
        }
    }

    [RelayCommand]
    private void PickCustomImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select server image (recommended: 300×110 px landscape)",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.webp",
        };
        if (dlg.ShowDialog() != true) return;
        var dir = Path.GetDirectoryName(CustomImagePath)!;
        Directory.CreateDirectory(dir);
        File.Copy(dlg.FileName, CustomImagePath, overwrite: true);
        OnPropertyChanged(nameof(GameImageUrl));
        OnPropertyChanged(nameof(HasCustomImage));
    }

    [RelayCommand]
    private void RemoveCustomImage()
    {
        if (File.Exists(CustomImagePath)) File.Delete(CustomImagePath);
        OnPropertyChanged(nameof(GameImageUrl));
        OnPropertyChanged(nameof(HasCustomImage));
    }

    internal void CopyCustomImageTo(string targetServerId)
    {
        if (!HasCustomImage) return;
        var targetDir  = Path.Combine(_config.AppDataPath, "server_images");
        Directory.CreateDirectory(targetDir);
        File.Copy(CustomImagePath, Path.Combine(targetDir, targetServerId + ".png"), overwrite: true);
    }
    public string UptimeText => _manager.GetInstance(Server.Id)?.Uptime is TimeSpan t && t > TimeSpan.Zero
        ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
        : "--:--:--";

    public string DailyRestartTimeText
    {
        get => Server.DailyRestartTime.ToString(@"hh\:mm");
        set
        {
            if (TimeSpan.TryParseExact(value, @"hh\:mm", null, out var ts))
                Server.DailyRestartTime = ts;
        }
    }

    // ── Kaistaseuranta ────────────────────────────────────────────────────────

    [ObservableProperty] private string _netIn          = "—";
    [ObservableProperty] private string _netOut         = "—";
    [ObservableProperty] private int    _connectionCount = 0;
    [ObservableProperty] private IReadOnlyList<double> _netInHistory  = [];
    [ObservableProperty] private IReadOnlyList<double> _netOutHistory = [];

    public bool HasNetworkStats => _network.GetServerStats(Server.Id) != null;

    private void OnServerStatsUpdated(string serverId)
    {
        if (serverId != Server.Id) return;
        var stats = _network.GetServerStats(serverId);
        if (stats == null) return;

        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            NetIn           = NetworkMonitorService.FormatSpeed(stats.BytesInPerSec);
            NetOut          = NetworkMonitorService.FormatSpeed(stats.BytesOutPerSec);
            ConnectionCount = stats.ConnectionCount;
            NetInHistory    = stats.HistoryIn.ToList();
            NetOutHistory   = stats.HistoryOut.ToList();
        });
    }

    public ObservableCollection<ConsoleMessage> FilteredLog
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ConsoleFilter)) return Log;
            var filter = ConsoleFilter.ToLowerInvariant();
            return new ObservableCollection<ConsoleMessage>(
                Log.Where(m => m.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public ServerViewModel(GameServer server, ServerManagerService manager, SteamCmdService steamCmd,
        BackupService backup, NotificationService notifications, PerformanceMonitorService perfMonitor,
        ConfigService config, ModManagerService mods,
        ConfigEditorService configEditor, PlayerStatsService playerStats,
        PerfHistoryService perfHistory,
        TemplateService templates, ScheduledTaskService scheduler, NetworkMonitorService network,
        GroupBanListService groupBans, RustModerationService moderation, ServerHygieneService hygiene,
        ConfigPresetService presets)
    {
        Server         = server;
        Plugin         = GameRegistry.Get(server.GameId);
        _manager       = manager;
        _steamCmd      = steamCmd;
        _backup        = backup;
        _notifications = notifications;
        _perfMonitor   = perfMonitor;
        _config        = config;
        _mods          = mods;
        _configEditor  = configEditor;
        _playerStats   = playerStats;
        _perfHistory   = perfHistory;
        _templates     = templates;
        _scheduler     = scheduler;
        _network       = network;
        _groupBans     = groupBans;
        _moderation    = moderation;
        _hygiene       = hygiene;
        _presets       = presets;
        AvailablePresets = _presets.GetPresetsForGame(server.GameId);
        SelectedPreset   = AvailablePresets.FirstOrDefault();

        _network.ServerStatsUpdated += OnServerStatsUpdated;
        FileBrowser.Initialize(server.InstallPath);

        _manager.LogReceived      += OnLogReceived;
        _manager.StatusChanged    += OnStatusChanged;
        _manager.CrashLimitReached += OnCrashLimitReached;
        _manager.PortsReassigned  += OnPortsReassigned;
        _steamCmd.OutputReceived  += OnSteamOutput;
        _steamCmd.ProgressChanged += OnSteamProgress;

        // Build CPU core checkboxes
        var coreCount = Environment.ProcessorCount;
        CpuCores = Enumerable.Range(0, coreCount).Select(i =>
        {
            var bit = 1L << i;
            var enabled = Server.CpuAffinityMask == 0 || (Server.CpuAffinityMask & bit) != 0;
            return new CpuCoreItem(i, enabled);
        }).ToList();

        foreach (var item in CpuCores)
        {
            item.Changed += () =>
            {
                long mask = 0;
                foreach (var c in CpuCores)
                    if (c.IsEnabled) mask |= (1L << c.Index);
                // If all cores selected, store 0 (= no restriction)
                Server.CpuAffinityMask = mask == ((1L << coreCount) - 1) ? 0 : mask;
            };
        }

        _maxRamMb = Server.MaxRamMb; // initialize without triggering OnMaxRamMbChanged
        _backupRetention   = Server.BackupRetention;
        _backupMaxAgeDays  = Server.BackupMaxAgeDays;
        _useIncrementalBackups = Server.UseIncrementalBackups;
        _fullBackupEveryN  = Server.FullBackupEveryN;
        foreach (var qc in Server.QuickCommands) QuickCommands.Add(qc);
        foreach (var r in Server.LogWatchRules)  LogWatchRules.Add(r);

        PluginFields = Plugin?.GetConfigFields()
            .Where(f => f.Key is not ("serverName" or "maxPlayers" or "serverPass"))
            .Select(f => new PluginFieldVm(server, f))
            .ToList() ?? [];

        RefreshStatus();
        RefreshBackups();
        RefreshModerationProfiles();

        // If the server was already running when this ViewModel was created (e.g. reattached
        // to a process that survived a HighPop restart), the StatusChanged(Running) event already
        // fired before we subscribed to it. Start monitoring directly in that case.
        if (IsRunning)
        {
            StartPerfMonitoring();
            StartPlayerRefresh();
            StartUpdateTimer();
        }
    }

    // ── Start / Stop / Restart ──────────────────────────────────────────────

    [RelayCommand]
    private async Task StartAsync()
    {
        try
        {
            if ((Server.UpdateOnStart || Server.AutoUpdate) && Plugin?.SteamAppId > 0)
                await InstallAsync();

            if (Server.BackupOnStart)
            {
                try
                {
                    AppendLog("[HighPop] Creating backup before start...", ConsoleMessageType.System);
                    await _backup.CreateBackupAsync(Server);
                    AppendLog("[HighPop] Backup created.", ConsoleMessageType.System);
                    RefreshBackups();
                }
                catch (Exception ex) { AppendLog($"[HighPop] Backup before start failed: {ex.Message}", ConsoleMessageType.Warning); }
            }

            await _manager.StartAsync(Server);
            // StartPerfMonitoring() and StartUpdateTimer() are called from OnStatusChanged(Running)
        }
        catch (FileNotFoundException ex)
        {
            AppendLog("[ERR] Server executable not found. Try clicking Install/Update again — if that doesn't help, check the Files tab to confirm the game actually downloaded, or check Settings → Install Path.", ConsoleMessageType.Error);
            AppendLog("[ERR] " + ex.Message, ConsoleMessageType.Error);
        }
        catch (InvalidOperationException ex)
        {
            AppendLog("[ERR] " + ex.Message, ConsoleMessageType.Error);
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] Unexpected error: " + ex.Message, ConsoleMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        try
        {
            StopUpdateTimer();
            await _manager.StopAsync(Server);
            // StopPerfMonitoring() called from OnStatusChanged(Stopped)

            if (Server.BackupOnShutdown)
            {
                try
                {
                    AppendLog("[HighPop] Creating backup after shutdown...", ConsoleMessageType.System);
                    await _backup.CreateBackupAsync(Server);
                    AppendLog("[HighPop] Backup created.", ConsoleMessageType.System);
                    RefreshBackups();
                }
                catch (Exception ex) { AppendLog($"[HighPop] Backup after shutdown failed: {ex.Message}", ConsoleMessageType.Warning); }
            }
        }
        catch (Exception ex) { AppendLog("[ERR] " + ex.Message, ConsoleMessageType.Error); }
    }

    [RelayCommand]
    private async Task KillAsync()
    {
        try
        {
            await _manager.KillAsync(Server);
            AppendLog("[HighPop] Process killed.", ConsoleMessageType.System);
            StopPerfMonitoring();
        }
        catch (Exception ex) { AppendLog("[ERR] " + ex.Message, ConsoleMessageType.Error); }
    }

    [RelayCommand]
    private void ShowWindow() => _manager.ShowWindow(Server);

    [RelayCommand]
    private async Task RestartAsync()
    {
        AppendLog("[HighPop] " + Loc.StatusStopping, ConsoleMessageType.System);
        await StopAsync();
        await Task.Delay(3000);
        await StartAsync();
    }

    // ── Install / Update ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Plugin == null) return;

        var expectedExecutable = Path.Combine(Server.InstallPath, Plugin.Executable);
        var hadExistingInstall = File.Exists(expectedExecutable);

        IsInstalling      = true;
        Server.Status     = ServerStatus.Installing;
        RefreshStatus();
        AppendLog($"[HighPop] {Loc.InstallingText} {Plugin.GameName}...", ConsoleMessageType.System);

        // Auto-backup before update (only when there is something to back up)
        if (Server.BackupEnabled && hadExistingInstall)
        {
            try { await _backup.CreateBackupAsync(Server); AppendLog("[Backup] Auto-backup created before update.", ConsoleMessageType.System); RefreshBackups(); }
            catch (Exception ex) { AppendLog($"[Backup] Pre-update backup failed: {ex.Message}", ConsoleMessageType.Warning); }
        }

        try
        {
            var branch = Server.GameSpecificSettings.TryGetValue("steamBranch", out var b) && !string.IsNullOrWhiteSpace(b) ? b : Plugin.SteamBranch;
            await _steamCmd.InstallOrUpdateAsync(Server.Id, Plugin.SteamAppId, Server.InstallPath, null, null, branch);
            if (!File.Exists(expectedExecutable))
                throw new FileNotFoundException("SteamCMD completed but the Rust server executable was not installed.", expectedExecutable);
            Server.Status = ServerStatus.Stopped;
            OnPropertyChanged(nameof(InstalledVersionText));
            AppendLog("[HighPop] " + Loc.InstallDone, ConsoleMessageType.System);
            await _notifications.NotifyAsync($"✅ {Server.DisplayName} {Loc.InstallDone}", Plugin.GameName, "#3FB950");
        }
        catch (FileNotFoundException ex)
        {
            AppendLog("[ERR] Server executable not found. Try clicking Install/Update again — if that doesn't help, check the Files tab to confirm the game actually downloaded, or check Settings → Install Path.", ConsoleMessageType.Error);
            AppendLog("[ERR] " + ex.Message, ConsoleMessageType.Error);
            Server.Status = ServerStatus.Error;
        }
        catch (InvalidOperationException ex)
        {
            AppendLog("[ERR] " + ex.Message, ConsoleMessageType.Error);
            Server.Status = ServerStatus.Error;
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] Unexpected error: " + ex.Message, ConsoleMessageType.Error);
            Server.Status = ServerStatus.Error;
        }
        finally { IsInstalling = false; RefreshStatus(); }
    }


    [RelayCommand]
    private async Task UpdateAsync()
    {
        AppendLog("[HighPop] " + Loc.StatusUpdating, ConsoleMessageType.System);
        await InstallAsync();
    }

    // ── Console ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SendConsoleCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(ConsoleInput)) return;
        var cmd = ConsoleInput;
        ConsoleInput = string.Empty;
        await RunCommandTextAsync(cmd);
    }

    [RelayCommand]
    private async Task RunQuickCommandAsync(QuickCommand? qc)
    {
        if (qc == null || string.IsNullOrWhiteSpace(qc.Command)) return;
        await RunCommandTextAsync(qc.Command);
    }

    private async Task RunCommandTextAsync(string cmd)
    {
        AppendLog("> " + cmd, ConsoleMessageType.Input);

        if (RconConnected)
        {
            await _rconLock.WaitAsync();
            try
            {
                if (_rcon != null)
                {
                    var resp = await _rcon.SendCommandAsync(cmd);
                    if (!string.IsNullOrEmpty(resp))
                        AppendLog(resp, ConsoleMessageType.Info);
                }
            }
            finally { _rconLock.Release(); }
        }
        else
        {
            _manager.SendCommand(Server.Id, cmd);
        }
    }

    [RelayCommand]
    private void ClearConsole() => Log.Clear();

    // ── Quick commands ───────────────────────────────────────────────────────

    [ObservableProperty] private string _newQuickCommandLabel   = string.Empty;
    [ObservableProperty] private string _newQuickCommandCommand = string.Empty;
    public ObservableCollection<QuickCommand> QuickCommands { get; } = [];

    [RelayCommand]
    private void AddQuickCommand()
    {
        if (string.IsNullOrWhiteSpace(NewQuickCommandLabel) || string.IsNullOrWhiteSpace(NewQuickCommandCommand)) return;
        var qc = new QuickCommand { Label = NewQuickCommandLabel, Command = NewQuickCommandCommand };
        QuickCommands.Add(qc);
        Server.QuickCommands.Add(qc);
        NewQuickCommandLabel   = string.Empty;
        NewQuickCommandCommand = string.Empty;
    }

    [RelayCommand]
    private void RemoveQuickCommand(QuickCommand? qc)
    {
        if (qc == null) return;
        QuickCommands.Remove(qc);
        Server.QuickCommands.Remove(qc);
    }

    partial void OnConsoleFilterChanged(string value) => OnPropertyChanged(nameof(FilteredLog));

    // ── Log Watcher rules ────────────────────────────────────────────────────

    [ObservableProperty] private string _newWatchKeyword    = string.Empty;
    [ObservableProperty] private Models.LogWatchAction _newWatchAction = Models.LogWatchAction.Notify;
    [ObservableProperty] private string _newWatchRcon       = string.Empty;
    [ObservableProperty] private int    _newWatchCooldown   = 5;

    public Models.LogWatchAction[] LogWatchActions { get; } = Enum.GetValues<Models.LogWatchAction>();
    public ObservableCollection<Models.LogWatchRule> LogWatchRules { get; } = [];

    [RelayCommand]
    private void AddLogWatchRule()
    {
        if (string.IsNullOrWhiteSpace(NewWatchKeyword)) return;
        var rule = new Models.LogWatchRule
        {
            Keyword     = NewWatchKeyword,
            Action      = NewWatchAction,
            RconCommand = NewWatchRcon,
            CooldownMin = Math.Max(0, NewWatchCooldown),
            Enabled     = true
        };
        LogWatchRules.Add(rule);
        Server.LogWatchRules.Add(rule);
        NewWatchKeyword  = string.Empty;
        NewWatchRcon     = string.Empty;
        NewWatchCooldown = 5;
    }

    [RelayCommand]
    private void RemoveLogWatchRule(Models.LogWatchRule? rule)
    {
        if (rule == null) return;
        LogWatchRules.Remove(rule);
        Server.LogWatchRules.Remove(rule);
    }

    // ── RCON ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ConnectRconAsync()
    {
        // Swap in a fresh RconService under lock so SendConsoleCommandAsync never sees a half-constructed state
        RconService newRcon;
        await _rconLock.WaitAsync();
        try
        {
            _rcon?.Dispose();
            _rcon = new RconService();
            newRcon = _rcon;
        }
        finally { _rconLock.Release(); }

        var ip = string.IsNullOrEmpty(Server.ServerIp) || Server.ServerIp == "0.0.0.0" ? "127.0.0.1" : Server.ServerIp;
        var port = Server.RconPort > 0 ? Server.RconPort : Server.ServerPort + 1;
        var ok   = await newRcon.ConnectAsync(ip, port, Server.RconPassword);

        RconConnected = ok;
        AppendLog(ok ? "[RCON] " + Loc.RconConnectedMsg : "[RCON] " + Loc.RconFailedMsg,
            ok ? ConsoleMessageType.System : ConsoleMessageType.Error);
    }

    [RelayCommand]
    private async Task DisconnectRconAsync()
    {
        await _rconLock.WaitAsync();
        try
        {
            _rcon?.Dispose();
            _rcon = null;
        }
        finally { _rconLock.Release(); }

        RconConnected = false;
        AppendLog("[RCON] " + Loc.RconDisconnectedMsg, ConsoleMessageType.System);
    }

    // ── Backups ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        try
        {
            AppendLog("[Backup] " + Loc.BackupCreating, ConsoleMessageType.System);
            var entry = await _backup.CreateBackupAsync(Server);
            AppendLog($"[Backup] {Loc.BackupDone}: {entry.SizeText}", ConsoleMessageType.System);
            await _notifications.NotifyAsync($"💾 {Server.DisplayName} {Loc.BackupDone}", entry.SizeText, "#D29922");
            RefreshBackups();
        }
        catch (Exception ex) { AppendLog("[ERR] " + ex.Message, ConsoleMessageType.Error); }
    }

    [RelayCommand]
    private void CleanupBackupsNow()
    {
        var toDelete = _backup.GetBackupsToDelete(Server, Server.BackupRetention);
        if (toDelete.Count == 0)
        {
            WpfMsgBox.Show("No backups currently match the retention/age limits — nothing to delete.",
                "Clean up backups", WpfMsgBoxButton.OK, WpfMsgBoxImage.Information);
            return;
        }

        var totalSize = toDelete.Sum(b => b.SizeBytes);
        var sizeText  = totalSize >= 1024 * 1024 * 1024
            ? $"{totalSize / (1024.0 * 1024 * 1024):F2} GB"
            : $"{totalSize / (1024.0 * 1024):F1} MB";

        var result = WpfMsgBox.Show(
            $"This will permanently delete {toDelete.Count} backup(s) totaling {sizeText}, based on the current " +
            $"\"Keep at most {Server.BackupRetention}\" / \"older than {Server.BackupMaxAgeDays} days\" settings.\n\n" +
            "This cannot be undone. Continue?",
            "Clean up backups", WpfMsgBoxButton.YesNo, WpfMsgBoxImage.Warning);
        if (result != WpfMsgBoxResult.Yes) return;

        foreach (var b in toDelete)
            _backup.DeleteBackup(b.FilePath);

        AppendLog($"[Backup] Cleaned up {toDelete.Count} old backup(s) ({sizeText}).", ConsoleMessageType.System);
        RefreshBackups();
    }

    [RelayCommand]
    private async Task RestoreBackupAsync(BackupEntry? entry)
    {
        if (entry == null) return;
        if (IsRunning)
        {
            AppendLog("[ERR] " + Loc.RestoreStopFirst, ConsoleMessageType.Error);
            return;
        }
        AppendLog($"[Restore] {Loc.RestoreStarting} {entry.CreatedAt:dd.MM.yyyy HH:mm}...", ConsoleMessageType.System);
        await _backup.RestoreBackupAsync(Server, entry.FilePath);
        AppendLog("[Restore] " + Loc.RestoreDone, ConsoleMessageType.System);
    }

    [RelayCommand]
    private void DeleteBackup(BackupEntry? entry)
    {
        if (entry == null) return;
        _backup.DeleteBackup(entry.FilePath);
        RefreshBackups();
    }

    public void RefreshBackups()
    {
        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            Backups.Clear();
            foreach (var b in _backup.GetBackupsForServer(Server))
                Backups.Add(b);
        });
    }

    // ── Port check ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CheckPortsAsync()
    {
        AppendLog("[Ports] " + Loc.PortChecking, ConsoleMessageType.System);
        var results = PortCheckerService.CheckServerPorts(Server);
        foreach (var r in results)
            AppendLog($"[Ports] {r.Message}", r.IsAvailable ? ConsoleMessageType.System : ConsoleMessageType.Warning);

        var extIp = await PortCheckerService.GetExternalIpAsync();
        if (extIp != null)
            AppendLog($"[Ports] {Loc.ExternalIp}: {extIp}", ConsoleMessageType.System);
    }

    // ── Open folder ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenInstallFolder()
    {
        if (System.IO.Directory.Exists(Server.InstallPath))
            System.Diagnostics.Process.Start("explorer.exe", Server.InstallPath);
    }

    // ── Config Presets ───────────────────────────────────────────────────────

    [RelayCommand]
    private void LoadPresets()
    {
        AvailablePresets = _presets.GetPresetsForGame(Server.GameId);
        SelectedPreset   = AvailablePresets.FirstOrDefault();
    }

    [RelayCommand]
    private void ApplyPreset()
    {
        if (SelectedPreset == null) return;

        var result = WpfMsgBox.Show(
            $"Apply preset \"{SelectedPreset.Name}\" to server \"{Server.DisplayName}\"?\n\n" +
            $"{SelectedPreset.Description}\n\n" +
            "A backup of the original config file will be created automatically.",
            "Apply Config Preset",
            WpfMsgBoxButton.YesNo,
            WpfMsgBoxImage.Question);

        if (result != WpfMsgBoxResult.Yes) return;

        var backup = _presets.ApplyPreset(Server, SelectedPreset);
        if (backup == null)
        {
            AppendLog($"[HighPop] ⚠ Config file not found: {SelectedPreset.ConfigFile}", ConsoleMessageType.Warning);
            return;
        }

        AppendLog($"[HighPop] ✔ Preset \"{SelectedPreset.Name}\" applied. Backup: {System.IO.Path.GetFileName(backup)}", ConsoleMessageType.System);
    }

    // ── Auto Update timer ────────────────────────────────────────────────────

    private void StartUpdateTimer()
    {
        StopUpdateTimer();
        if (!Server.AutoUpdate || Plugin?.SteamAppId == 0) return;

        int intervalMin = Server.AutoUpdateIntervalMin > 0 ? Server.AutoUpdateIntervalMin : 30;
        _updateTimer = new System.Timers.Timer(intervalMin * 60_000);
        _updateTimer.Elapsed += async (_, _) => await RunPeriodicUpdateAsync();
        _updateTimer.AutoReset = true;
        _updateTimer.Start();
        AppendLog($"[AutoUpdate] Scheduled — checking every {intervalMin} min", ConsoleMessageType.System);
    }

    private void StopUpdateTimer()
    {
        _updateTimer?.Stop();
        _updateTimer?.Dispose();
        _updateTimer = null;
    }

    private async Task RunPeriodicUpdateAsync()
    {
        if (!IsRunning) return; // server was stopped manually
        AppendLog("[AutoUpdate] Checking for updates...", ConsoleMessageType.System);

        bool wasAutoRestart = Server.AutoRestart;
        try
        {
            // Temporarily disable AutoRestart so we don't get an auto-restart
            // during the stop → update → start cycle
            Server.AutoRestart = false;
            await _manager.WarnPlayersAsync(Server, "Server restarting for an update in 1 minute");
            await Task.Delay(60_000);
            await _manager.StopAsync(Server);
            // StopPerfMonitoring called by OnStatusChanged

            await InstallAsync(); // runs SteamCMD update

            if (!Server.AutoUpdate)
            {
                Server.AutoRestart = wasAutoRestart;
                return;
            }
            Server.AutoRestart = wasAutoRestart;

            await _manager.StartAsync(Server);
            // StartPerfMonitoring + StartUpdateTimer called by OnStatusChanged(Running)
            AppendLog("[AutoUpdate] ✅ Updated and restarted.", ConsoleMessageType.System);
            await _notifications.NotifyAsync($"🔄 {Server.DisplayName} updated & restarted", Plugin?.GameName ?? "", "#F05A28");
        }
        catch (Exception ex)
        {
            Server.AutoRestart = wasAutoRestart;
            AppendLog($"[AutoUpdate] ❌ {ex.Message}", ConsoleMessageType.Error);
        }
    }

    // ── Mod manager ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task InstallOxideAsync()
    {
        if (Plugin == null || !Plugin.SupportsOxide) return;
        if (IsRunning)
        {
            ModStatusText = "Stop the server before installing or updating Oxide.";
            return;
        }
        if (Plugin.GameId == "rust" && ModManagerService.IsCarbonInstalled(Server.InstallPath))
        {
            ModStatusText = "Carbon is already installed. Use one Rust mod framework at a time.";
            return;
        }
        ModBusy = true;
        try
        {
            var progress = new Progress<(int pct, string msg)>(x =>
                WpfApplication.Current?.Dispatcher?.Invoke(() => ModStatusText = $"[{x.pct}%] {x.msg}"));

            await _mods.InstallOxideAsync(Plugin, Server.InstallPath, progress);
            AppendLog("[Mods] ✅ Oxide installed successfully.", ConsoleMessageType.System);
        }
        catch (Exception ex)
        {
            AppendLog($"[Mods] ❌ {ex.Message}", ConsoleMessageType.Error);
            WpfApplication.Current?.Dispatcher?.Invoke(() => ModStatusText = $"❌ {ex.Message}");
        }
        finally { ModBusy = false; }
    }

    [RelayCommand]
    private async Task InstallCarbonAsync()
    {
        if (Plugin?.GameId != "rust") return;
        if (IsRunning)
        {
            ModStatusText = "Stop the Rust server before installing or updating Carbon.";
            return;
        }
        if (ModManagerService.GetInstalledOxideVersion(Server.InstallPath) != null)
        {
            ModStatusText = "Oxide is already installed. Use one Rust mod framework at a time.";
            return;
        }

        ModBusy = true;
        try
        {
            var progress = new Progress<(int pct, string msg)>(x =>
                WpfApplication.Current?.Dispatcher?.Invoke(() => ModStatusText = $"[{x.pct}%] {x.msg}"));
            await _mods.InstallCarbonAsync(Server.InstallPath, progress);
            AppendLog("[Mods] ✅ Carbon installed successfully.", ConsoleMessageType.System);
        }
        catch (Exception ex)
        {
            AppendLog($"[Mods] ❌ {ex.Message}", ConsoleMessageType.Error);
            WpfApplication.Current?.Dispatcher?.Invoke(() => ModStatusText = $"❌ {ex.Message}");
        }
        finally { ModBusy = false; }
    }

    [RelayCommand]
    private void OpenCarbonPluginFolder() =>
        ModManagerService.OpenCarbonPluginFolder(Server.InstallPath);

    [RelayCommand]
    private void OpenPluginFolder()
    {
        if (Plugin == null) return;
        ModManagerService.OpenPluginFolder(Plugin, Server.InstallPath);
    }

    // ── Config editor ───────────────────────────────────────────────────────

    [RelayCommand]
    private void LoadConfigFiles()
    {
        ConfigFiles = _configEditor.FindConfigs(Server, Plugin);
        if (ConfigFiles.Count > 0) SelectedConfigFile = ConfigFiles[0];
    }

    [RelayCommand]
    private void SaveConfigFile()
    {
        if (SelectedConfigFile == null) return;
        SelectedConfigFile.Content = ConfigContent;
        try
        {
            _configEditor.Save(Server, SelectedConfigFile);
            AppendLog("[Config] File saved.", ConsoleMessageType.System);
            RefreshConfigHistory();
        }
        catch (Exception ex) { AppendLog($"[Config] Save failed: {ex.Message}", ConsoleMessageType.Error); }
    }

    [ObservableProperty] private List<Services.ConfigSnapshot> _configHistory = [];

    private void RefreshConfigHistory()
    {
        ConfigHistory = SelectedConfigFile == null ? [] : _configEditor.GetHistory(Server.Id, SelectedConfigFile.Path);
    }

    [RelayCommand]
    private void RestoreConfigSnapshot(Services.ConfigSnapshot? snap)
    {
        if (snap == null || SelectedConfigFile == null) return;
        var result = WpfMsgBox.Show(
            $"Restore the version from {snap.SavedAt:dd.MM.yyyy HH:mm:ss}? The current content will be overwritten " +
            "(but is itself saved to history first, so you can undo this too).",
            "Restore config version", WpfMsgBoxButton.YesNo, WpfMsgBoxImage.Warning);
        if (result != WpfMsgBoxResult.Yes) return;

        var content = _configEditor.ReadSnapshot(snap.FilePath);
        SelectedConfigFile.Content = content;
        ConfigContent = content;
        try
        {
            _configEditor.Save(Server, SelectedConfigFile);
            AppendLog($"[Config] Restored version from {snap.SavedAt:dd.MM.yyyy HH:mm:ss}.", ConsoleMessageType.System);
            RefreshConfigHistory();
        }
        catch (Exception ex) { AppendLog($"[Config] Restore failed: {ex.Message}", ConsoleMessageType.Error); }
    }

    partial void OnSelectedConfigFileChanged(Services.ConfigFileEntry? value)
    {
        if (value == null) return;
        _configEditor.LoadContent(value);
        ConfigContent = value.Content;
        RefreshConfigHistory();
    }

    // ── Players ──────────────────────────────────────────────────────────────

    private bool _playersFetchedOnce;

    private void StartPlayerRefresh()
    {
        _playerRefreshTimer?.Dispose();
        _playerRefreshTimer = new System.Timers.Timer(15_000);
        _playerRefreshTimer.Elapsed  += (_, _) => _ = FetchOnlinePlayersAsync().ContinueWith(t => AppendLog($"[Players] {t.Exception!.InnerException?.Message}", ConsoleMessageType.Warning), System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
        _playerRefreshTimer.AutoReset = true;
        _playerRefreshTimer.Start();

        // Give Rust WebRCON a moment to finish binding after the process starts.
        _ = Task.Delay(3_000).ContinueWith(_ => FetchOnlinePlayersAsync().ContinueWith(
            t => AppendLog($"[Players] {t.Exception!.InnerException?.Message}", ConsoleMessageType.Warning),
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted));
    }

    private void StopPlayerRefresh()
    {
        _playerRefreshTimer?.Stop();
        _playerRefreshTimer?.Dispose();
        _playerRefreshTimer = null;
    }

    [RelayCommand]
    private async Task RefreshPlayersAsync()
    {
        await FetchOnlinePlayersAsync();
        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            PlayerHistory = _playerStats.GetSessions(Server.Id, 50);
            PlayerStatsList = _playerStats.GetPlayerStats(Server.Id, 50);
            RefreshActivityStats();
        });
    }

    private async Task FetchOnlinePlayersAsync()
    {
        if (Plugin == null) return;

        var cmd = Plugin.GetPlayersCommand();
        if (cmd == null || !RconConnected || _rcon == null) return;

        string response;
        await _rconLock.WaitAsync();
        try   { response = await _rcon.SendCommandAsync(cmd); }
        catch { return; }
        finally { _rconLock.Release(); }

        if (string.IsNullOrWhiteSpace(response)) return;
        var parsed = Services.PlayerParserService.ParseRustPlayerList(response);
        var selectedSteamIds = OnlinePlayers
            .Where(p => p.IsSelected && RustModerationCommands.IsSteamId(p.SteamId))
            .Select(p => p.SteamId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var player in parsed.Where(p => RustModerationCommands.IsSteamId(p.SteamId)))
        {
            var profile = _moderation.GetRecord(Server.Id, player.SteamId);
            player.IsSelected    = selectedSteamIds.Contains(player.SteamId);
            player.StaffNotes    = profile?.Notes ?? string.Empty;
            player.IsWhitelisted = profile?.Whitelisted == true;
        }

        // Keep the model in sync — used by Shut-down-when-empty and the web dashboard's
        // server list (the detail endpoint already gets a live count separately).
        Server.CurrentPlayers = parsed.Count;

        // Compare against the previous list → session logging
        var prev = OnlinePlayers.ToList();
        var currNames  = parsed.Select(p => p.SteamId.Length > 0 ? p.SteamId : p.Name).ToHashSet();
        var prevNames  = prev.Select(p => p.SteamId.Length > 0 ? p.SteamId : p.Name).ToHashSet();

        var joined = parsed.Where(p => !prevNames.Contains(p.SteamId.Length > 0 ? p.SteamId : p.Name)).ToList();
        var left   = prev.Where(p => !currNames.Contains(p.SteamId.Length > 0 ? p.SteamId : p.Name)).ToList();

        foreach (var p in joined)
            _playerStats.RecordJoin(Server.Id, p.Name, p.SteamId);

        foreach (var p in left)
            _playerStats.RecordLeave(Server.Id, p.Name, p.SteamId);

        // Skip Discord alerts on the very first fetch after opening a server, otherwise
        // every already-connected player would fire a spurious "joined" notification.
        if (_playersFetchedOnce)
        {
            foreach (var p in joined) _ = _notifications.NotifyPlayerEventAsync(Server, p.Name, joined: true, parsed.Count);
            foreach (var p in left)   _ = _notifications.NotifyPlayerEventAsync(Server, p.Name, joined: false, parsed.Count);
        }
        _playersFetchedOnce = true;

        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            OnlinePlayers = parsed;
            PlayerHistory = _playerStats.GetSessions(Server.Id, 50);
            PlayerStatsList = _playerStats.GetPlayerStats(Server.Id, 50);
            RefreshModerationProfiles();
            RefreshActivityStats();
        });
    }

    private void RefreshModerationProfiles() =>
        ModerationProfiles = _moderation.GetRecords(Server.Id);

    // ── Performance history ──────────────────────────────────────────────────

    private void EnsurePerfModel()
    {
        if (_perfModel != null) return;
        _cpuSeries = new OxyPlot.Series.LineSeries
        {
            Title = "CPU %", Color = OxyPlot.OxyColor.Parse("#F05A28"),
            StrokeThickness = 2, MarkerType = OxyPlot.MarkerType.None,
        };
        _memSeries = new OxyPlot.Series.LineSeries
        {
            Title = "RAM MB", Color = OxyPlot.OxyColor.Parse("#3FB950"),
            StrokeThickness = 2, MarkerType = OxyPlot.MarkerType.None, YAxisKey = "mem",
        };
        _perfModel = new OxyPlot.PlotModel
        {
            Background          = OxyPlot.OxyColor.FromArgb(0, 0, 0, 0),
            PlotAreaBorderColor = OxyPlot.OxyColor.Parse("#30363d"),
            TextColor           = OxyPlot.OxyColor.Parse("#8b949e"),
        };
        _perfModel.Axes.Add(new OxyPlot.Axes.DateTimeAxis
        {
            Position = OxyPlot.Axes.AxisPosition.Bottom, StringFormat = "HH:mm:ss",
            AxislineColor = OxyPlot.OxyColor.Parse("#30363d"), TicklineColor = OxyPlot.OxyColor.Parse("#30363d"),
            MajorGridlineColor = OxyPlot.OxyColor.Parse("#21262d"), MajorGridlineStyle = OxyPlot.LineStyle.Solid,
        });
        _perfModel.Axes.Add(new OxyPlot.Axes.LinearAxis
        {
            Position = OxyPlot.Axes.AxisPosition.Left, Title = "CPU %", Minimum = 0, Maximum = 100,
            MajorGridlineColor = OxyPlot.OxyColor.Parse("#21262d"), MajorGridlineStyle = OxyPlot.LineStyle.Solid,
        });
        _perfModel.Axes.Add(new OxyPlot.Axes.LinearAxis
        {
            Key = "mem", Position = OxyPlot.Axes.AxisPosition.Right, Title = "RAM MB", Minimum = 0,
            MaximumPadding = 0.15, // headroom so peak usage doesn't look pinned to the top edge
        });
        _perfModel.Series.Add(_cpuSeries);
        _perfModel.Series.Add(_memSeries);
        WpfApplication.Current?.Dispatcher?.Invoke(() => PerfPlot = _perfModel);
    }

    private void UpdatePerfChart()
    {
        var cutoff  = DateTime.Now.AddMinutes(-PerfRangeMinutes);
        var samples = _perfHistory.Get(Server.Id).Where(s => s.Time >= cutoff).ToList();
        EnsurePerfModel();

        // Mutate series and invalidate on the UI thread together — this can be called from the
        // 2s timer's background thread, and OxyPlot's renderer reads Points on the UI thread.
        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            _cpuSeries!.Points.Clear();
            _memSeries!.Points.Clear();
            foreach (var s in samples)
            {
                var x = OxyPlot.Axes.DateTimeAxis.ToDouble(s.Time);
                _cpuSeries.Points.Add(new OxyPlot.DataPoint(x, s.Cpu));
                _memSeries.Points.Add(new OxyPlot.DataPoint(x, s.MemMb));
            }
            _perfModel!.InvalidatePlot(true);
        });
    }

    private static OxyPlot.PlotModel CreateEmptyPlot()
    {
        var m = new OxyPlot.PlotModel { Background = OxyPlot.OxyColor.FromArgb(0, 0, 0, 0) };
        m.Axes.Add(new OxyPlot.Axes.LinearAxis { Position = OxyPlot.Axes.AxisPosition.Left });
        m.Axes.Add(new OxyPlot.Axes.LinearAxis { Position = OxyPlot.Axes.AxisPosition.Bottom });
        return m;
    }

    // ── Direct Connect ────────────────────────────────────────────────────────

    [RelayCommand]
    private void JoinServer()
    {
        var ip   = string.IsNullOrEmpty(Server.ServerIp) || Server.ServerIp == "0.0.0.0" ? "127.0.0.1" : Server.ServerIp;
        var uri  = $"steam://connect/{ip}:{Server.ServerPort}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog($"[Connect] {ex.Message}", ConsoleMessageType.Error); }
    }

    [RelayCommand]
    private void CopyConnectionLink()
    {
        var ip   = string.IsNullOrEmpty(Server.ServerIp) || Server.ServerIp == "0.0.0.0" ? "127.0.0.1" : Server.ServerIp;
        var link = $"steam://connect/{ip}:{Server.ServerPort}";
        try
        {
            System.Windows.Clipboard.SetText(link);
            AppendLog($"[Connect] Copied: {link}", ConsoleMessageType.System);
        }
        catch (Exception ex) { AppendLog($"[Connect] {ex.Message}", ConsoleMessageType.Error); }
    }

    public bool HasShareableStatusLink => _config.WebApiRequired && _config.WebApiPort > 0;

    [RelayCommand]
    private void CleanupJunkFiles()
    {
        if (IsRunning)
        {
            WpfMsgBox.Show("Stop the server first — junk cleanup only scans/deletes files while it's not running.",
                "Server hygiene", WpfMsgBoxButton.OK, WpfMsgBoxImage.Warning);
            return;
        }

        var junk = _hygiene.ScanJunk(Server);
        if (junk.Count == 0)
        {
            WpfMsgBox.Show("No leftover log files, crash reports or temp files found.",
                "Server hygiene", WpfMsgBoxButton.OK, WpfMsgBoxImage.Information);
            return;
        }

        var totalSize = junk.Sum(j => j.SizeBytes);
        var sizeText  = totalSize >= 1024 * 1024 * 1024
            ? $"{totalSize / (1024.0 * 1024 * 1024):F2} GB"
            : $"{totalSize / (1024.0 * 1024):F1} MB";
        var byType = junk.GroupBy(j => j.Description).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}(s)");

        var result = WpfMsgBox.Show(
            $"Found {junk.Count} item(s) totaling {sizeText}:\n{string.Join(", ", byType)}\n\n" +
            "This will permanently delete them. Continue?",
            "Server hygiene", WpfMsgBoxButton.YesNo, WpfMsgBoxImage.Warning);
        if (result != WpfMsgBoxResult.Yes) return;

        _hygiene.DeleteJunk(junk);
        AppendLog($"[Hygiene] Deleted {junk.Count} junk item(s) ({sizeText}).", ConsoleMessageType.System);
    }

    [RelayCommand]
    private void CopyStatusLink()
    {
        if (!HasShareableStatusLink)
        {
            AppendLog("[Status] Enable the Web Dashboard in Settings to get a shareable link.", ConsoleMessageType.Warning);
            return;
        }
        var link = $"http://<your-ip>:{_config.WebApiPort}/status/{Server.Id}";
        try
        {
            System.Windows.Clipboard.SetText(link);
            AppendLog($"[Status] Copied: {link} (replace <your-ip> with your machine's address)", ConsoleMessageType.System);
        }
        catch (Exception ex) { AppendLog($"[Status] {ex.Message}", ConsoleMessageType.Error); }
    }

    // ── Player Management ─────────────────────────────────────────────────────

    // Vanhanmallinen tekstisyöttö (yhteensopivuus)
    [ObservableProperty] private string _playerInput = string.Empty;

    [RelayCommand]
    private async Task KickPlayerAsync()
    {
        if (string.IsNullOrWhiteSpace(PlayerInput) || Plugin == null) return;
        var cmd = Plugin.GetKickCommand(PlayerInput,
            string.IsNullOrWhiteSpace(KickReason) ? "Kicked by admin" : KickReason);
        if (cmd == null) { AppendLog("[Players] Kick not supported.", ConsoleMessageType.Warning); return; }
        await SendRconOrConsole(cmd);
        AppendLog($"[Players] Kicked: {PlayerInput}", ConsoleMessageType.System);
        KickReason = string.Empty;
        _ = FetchOnlinePlayersAsync();
    }

    [RelayCommand]
    private async Task BanPlayerAsync()
    {
        if (string.IsNullOrWhiteSpace(PlayerInput) || Plugin == null) return;
        var reason = string.IsNullOrWhiteSpace(BanReason) ? "Banned by admin" : BanReason;
        var (steamId, playerName) = ResolvePlayer(PlayerInput);
        if (!await BanTargetAsync(steamId, playerName, reason)) return;
        BanReason = string.Empty;
        BanDuration = string.Empty;
        _ = FetchOnlinePlayersAsync();
    }

    /// <summary>Records a ban in the server's group ban list and replays it on the group's
    /// other currently-running servers of the same game.</summary>
    private async Task SyncBanToGroupAsync(
        string target,
        string playerName,
        string reason,
        string duration,
        DateTime? expiresAtUtc)
    {
        if (string.IsNullOrEmpty(Server.GroupId) || Plugin == null) return;
        _groupBans.AddBan(Server.GroupId, Server.GameId, target, reason,
            playerName, duration, expiresAtUtc);

        foreach (var sibling in _manager.GetRunningGroupSiblings(Server))
        {
            string? cmd;
            if (RustModerationCommands.IsSteamId(target))
            {
                var remaining = expiresAtUtc is { } expiry
                    ? RustModerationCommands.RemainingDuration(expiry, DateTime.UtcNow)
                    : string.Empty;
                if (!RustModerationCommands.TryBuildBan(target, playerName, reason, remaining,
                    DateTime.UtcNow, out var timedCommand, out _, out _, out _))
                    continue;
                cmd = timedCommand;
            }
            else
                cmd = Plugin.GetBanCommand(target, reason);
            if (cmd == null) continue;
            try { await _manager.SendCommandAsync(sibling.Id, cmd); }
            catch { }
        }
        AppendLog($"[Group Ban] Synced to {Server.GroupId}.", ConsoleMessageType.System);
    }

    // Kick/Ban suoraan pelaajan kortista
    [RelayCommand]
    private async Task KickOnlinePlayerAsync(Models.OnlinePlayer? player)
    {
        if (player == null || Plugin == null) return;
        var target = player.SteamId.Length > 0 ? player.SteamId : player.Name;
        var reason  = string.IsNullOrWhiteSpace(KickReason) ? "Kicked by admin" : KickReason;
        var cmd     = Plugin.GetKickCommand(target, reason);
        if (cmd == null) { AppendLog("[Players] Kick not supported.", ConsoleMessageType.Warning); return; }
        await SendRconOrConsole(cmd);
        AppendLog($"[Players] Kicked {player.Name} ({reason})", ConsoleMessageType.System);
        KickReason = string.Empty;
        _ = FetchOnlinePlayersAsync();
    }

    [RelayCommand]
    private async Task BanOnlinePlayerAsync(Models.OnlinePlayer? player)
    {
        if (player == null || Plugin == null) return;
        var reason  = string.IsNullOrWhiteSpace(BanReason) ? "Banned by admin" : BanReason;
        if (!await BanTargetAsync(player.SteamId, player.Name, reason)) return;
        BanReason = string.Empty;
        BanDuration = string.Empty;
        _ = FetchOnlinePlayersAsync();
    }

    private async Task<bool> BanTargetAsync(string steamId, string playerName, string reason)
    {
        if (Plugin == null) return false;

        string? command;
        string normalizedDuration = string.Empty;
        DateTime? expiresAtUtc = null;
        var target = RustModerationCommands.IsSteamId(steamId) ? steamId : playerName;

        if (RustModerationCommands.IsSteamId(steamId))
        {
            if (!RustModerationCommands.TryBuildBan(steamId, playerName, reason, BanDuration,
                DateTime.UtcNow, out var timedCommand, out normalizedDuration, out expiresAtUtc, out var error))
            {
                ModerationStatus = error;
                AppendLog("[Moderation] " + error, ConsoleMessageType.Warning);
                return false;
            }
            command = timedCommand;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(BanDuration))
            {
                ModerationStatus = "Timed bans require a connected player or a 17-digit Steam64 ID.";
                AppendLog("[Moderation] " + ModerationStatus, ConsoleMessageType.Warning);
                return false;
            }
            command = Plugin.GetBanCommand(target, reason);
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            AppendLog("[Players] Ban not supported.", ConsoleMessageType.Warning);
            return false;
        }

        await SendRconOrConsole(command);
        var durationText = string.IsNullOrEmpty(normalizedDuration) ? "permanent" : normalizedDuration;
        AppendLog($"[Players] Banned {playerName} ({reason}; {durationText})", ConsoleMessageType.System);
        ModerationStatus = $"Ban issued for {playerName} ({durationText}).";

        if (RustModerationCommands.IsSteamId(steamId))
            _moderation.RecordBan(Server.Id, steamId, playerName, reason, normalizedDuration, expiresAtUtc);
        await SyncBanToGroupAsync(target, playerName, reason, normalizedDuration, expiresAtUtc);
        RefreshModerationState();
        return true;
    }

    private (string SteamId, string PlayerName) ResolvePlayer(string input)
    {
        input = input.Trim();
        var online = OnlinePlayers.FirstOrDefault(p =>
            p.SteamId.Equals(input, StringComparison.Ordinal)
            || p.Name.Equals(input, StringComparison.OrdinalIgnoreCase));
        return online == null
            ? (RustModerationCommands.IsSteamId(input) ? input : string.Empty, input)
            : (online.SteamId, online.Name);
    }

    [RelayCommand]
    private void SelectAllPlayers()
    {
        foreach (var player in OnlinePlayers) player.IsSelected = true;
        OnlinePlayers = OnlinePlayers.ToList();
        ModerationStatus = $"Selected {OnlinePlayers.Count} online player(s).";
    }

    [RelayCommand]
    private void ClearPlayerSelection()
    {
        foreach (var player in OnlinePlayers) player.IsSelected = false;
        OnlinePlayers = OnlinePlayers.ToList();
        ModerationStatus = "Player selection cleared.";
    }

    [RelayCommand]
    private async Task BulkKickPlayersAsync()
    {
        var selected = OnlinePlayers.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ModerationStatus = "Select at least one online player first.";
            return;
        }
        if (!ConfirmBulkAction("kick", selected.Count)) return;

        var reason = string.IsNullOrWhiteSpace(KickReason) ? "Kicked by admin" : KickReason;
        var completed = 0;
        foreach (var player in selected)
        {
            var target = RustModerationCommands.IsSteamId(player.SteamId) ? player.SteamId : player.Name;
            var command = Plugin?.GetKickCommand(target, reason);
            if (command == null) continue;
            await SendRconOrConsole(command);
            completed++;
        }
        AppendLog($"[Moderation] Bulk kicked {completed} player(s): {reason}", ConsoleMessageType.System);
        ModerationStatus = $"Bulk kick completed for {completed} player(s).";
        KickReason = string.Empty;
        _ = FetchOnlinePlayersAsync();
    }

    [RelayCommand]
    private async Task BulkBanPlayersAsync()
    {
        var selected = OnlinePlayers
            .Where(p => p.IsSelected && RustModerationCommands.IsSteamId(p.SteamId))
            .ToList();
        if (selected.Count == 0)
        {
            ModerationStatus = "Select at least one online player with a Steam64 ID first.";
            return;
        }
        if (!ConfirmBulkAction("ban", selected.Count)) return;

        var reason = string.IsNullOrWhiteSpace(BanReason) ? "Banned by admin" : BanReason;
        var completed = 0;
        foreach (var player in selected)
            if (await BanTargetAsync(player.SteamId, player.Name, reason)) completed++;

        AppendLog($"[Moderation] Bulk banned {completed} player(s): {reason}", ConsoleMessageType.System);
        ModerationStatus = $"Bulk ban completed for {completed} player(s).";
        BanReason = string.Empty;
        BanDuration = string.Empty;
        _ = FetchOnlinePlayersAsync();
    }

    [RelayCommand]
    private async Task UnbanPlayerAsync()
    {
        var (steamId, playerName) = ResolvePlayer(PlayerInput);
        if (!RustModerationCommands.IsSteamId(steamId))
        {
            ModerationStatus = "Unban requires a valid 17-digit Steam64 ID.";
            return;
        }

        await SendRconOrConsole(RustModerationCommands.Unban(steamId));
        _moderation.RecordUnban(Server.Id, steamId, playerName);
        _groupBans.RemoveBan(Server.GroupId, Server.GameId, steamId);
        ModerationStatus = $"Unban issued for {playerName}.";
        AppendLog($"[Players] Unbanned {playerName} ({steamId})", ConsoleMessageType.System);
        RefreshModerationState();
    }

    [RelayCommand]
    private void UsePlayerForModeration(Models.OnlinePlayer? player)
    {
        if (player == null) return;
        PlayerInput = RustModerationCommands.IsSteamId(player.SteamId) ? player.SteamId : player.Name;
        PlayerNote  = player.StaffNotes;
        ModerationStatus = $"Loaded {player.Name} into the moderation workspace.";
    }

    [RelayCommand]
    private void SavePlayerNote()
    {
        var (steamId, playerName) = ResolvePlayer(PlayerInput);
        if (!RustModerationCommands.IsSteamId(steamId))
        {
            ModerationStatus = "Player notes require a valid 17-digit Steam64 ID.";
            return;
        }
        _moderation.SetNote(Server.Id, steamId, playerName, PlayerNote);
        ModerationStatus = $"Saved staff notes for {playerName}.";
        RefreshModerationState();
    }

    [RelayCommand]
    private async Task GrantWhitelistAsync()
    {
        var (steamId, playerName) = ResolvePlayer(PlayerInput);
        if (!CanManageWhitelist(steamId)) return;
        await SendRconOrConsole(RustModerationCommands.GrantWhitelist(steamId));
        _moderation.SetWhitelisted(Server.Id, steamId, playerName, allowed: true);
        ModerationStatus = $"Whitelist permission granted to {playerName}.";
        AppendLog($"[Whitelist] Granted whitelist.allow to {playerName} ({steamId})", ConsoleMessageType.System);
        RefreshModerationState();
    }

    [RelayCommand]
    private async Task RevokeWhitelistAsync()
    {
        var (steamId, playerName) = ResolvePlayer(PlayerInput);
        if (!CanManageWhitelist(steamId)) return;
        await SendRconOrConsole(RustModerationCommands.RevokeWhitelist(steamId));
        _moderation.SetWhitelisted(Server.Id, steamId, playerName, allowed: false);
        ModerationStatus = $"Whitelist permission revoked from {playerName}.";
        AppendLog($"[Whitelist] Revoked whitelist.allow from {playerName} ({steamId})", ConsoleMessageType.System);
        RefreshModerationState();
    }

    private bool CanManageWhitelist(string steamId)
    {
        if (!RustModerationCommands.IsSteamId(steamId))
        {
            ModerationStatus = "Whitelist changes require a valid 17-digit Steam64 ID.";
            return false;
        }
        var hasFramework = ModManagerService.GetInstalledOxideVersion(Server.InstallPath) != null
            || ModManagerService.IsCarbonInstalled(Server.InstallPath);
        if (!hasFramework)
        {
            ModerationStatus = "Install Carbon or Oxide plus the Whitelist plugin before managing whitelist access.";
            return false;
        }
        if (!IsRunning)
        {
            ModerationStatus = "Start Rust and connect WebRCON before changing whitelist access.";
            return false;
        }
        return true;
    }

    private static bool ConfirmBulkAction(string action, int count) =>
        System.Windows.MessageBox.Show(
            $"Confirm bulk {action} for {count} selected player(s)?\n\nThis sends live Rust moderation commands and cannot be undone as a group.",
            $"HighPop — Confirm bulk {action}",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

    private void RefreshModerationState()
    {
        RefreshModerationProfiles();
        foreach (var player in OnlinePlayers.Where(p => RustModerationCommands.IsSteamId(p.SteamId)))
        {
            var profile = _moderation.GetRecord(Server.Id, player.SteamId);
            player.StaffNotes     = profile?.Notes ?? string.Empty;
            player.IsWhitelisted = profile?.Whitelisted == true;
        }
        OnlinePlayers = OnlinePlayers.ToList();
    }

    [RelayCommand]
    private async Task ListPlayersAsync()
    {
        if (Plugin == null) return;
        var cmd = Plugin.GetPlayersCommand();
        if (cmd == null) { AppendLog("[Players] Player list not supported.", ConsoleMessageType.Warning); return; }
        await SendRconOrConsole(cmd);
    }

    /// <summary>Re-applies this group's bans (for this game) a few seconds after start, in
    /// case this server missed bans issued while it wasn't running. Ban commands are
    /// idempotent for every supported game, so re-sending an existing ban is harmless.</summary>
    private async Task ReplayGroupBansAsync()
    {
        if (string.IsNullOrEmpty(Server.GroupId) || Plugin == null) return;
        await Task.Delay(10_000);
        if (!IsRunning) return;

        var bans = _groupBans.GetBans(Server.GroupId, Server.GameId);
        foreach (var ban in bans)
        {
            string? cmd;
            if (RustModerationCommands.IsSteamId(ban.Target))
            {
                var remaining = ban.ExpiresAtUtc is { } expiry
                    ? RustModerationCommands.RemainingDuration(expiry, DateTime.UtcNow)
                    : string.Empty;
                if (!RustModerationCommands.TryBuildBan(
                    ban.Target, ban.PlayerName, ban.Reason, remaining, DateTime.UtcNow,
                    out var timedCommand, out _, out _, out _))
                    continue;
                cmd = timedCommand;
            }
            else
                cmd = Plugin.GetBanCommand(ban.Target, ban.Reason);
            if (cmd == null) continue;
            try { await SendRconOrConsole(cmd); } catch { }
        }
        if (bans.Count > 0)
            AppendLog($"[Group Ban] Re-applied {bans.Count} group ban(s).", ConsoleMessageType.System);
    }

    private async Task SendRconOrConsole(string cmd)
    {
        if (RconConnected && _rcon != null)
        {
            await _rconLock.WaitAsync();
            try { var r = await _rcon.SendCommandAsync(cmd); if (!string.IsNullOrEmpty(r)) AppendLog(r); }
            finally { _rconLock.Release(); }
        }
        else
            _manager.SendCommand(Server.Id, cmd);
    }

    // ── Templates ─────────────────────────────────────────────────────────────

    [ObservableProperty] private string _templateName        = string.Empty;
    [ObservableProperty] private string _templateCategory    = string.Empty;
    [ObservableProperty] private string _templateTagsInput   = string.Empty;  // pilkkueroteltu
    [ObservableProperty] private string _templateDescription = string.Empty;

    [RelayCommand]
    private void SaveAsTemplate()
    {
        if (string.IsNullOrWhiteSpace(TemplateName)) return;
        var tags = TemplateTagsInput
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _templates.SaveFromServer(Server, TemplateName, TemplateDescription, TemplateCategory, tags);
        var saved = TemplateName;
        TemplateName        = string.Empty;
        TemplateCategory    = string.Empty;
        TemplateTagsInput   = string.Empty;
        TemplateDescription = string.Empty;
        RefreshTemplates();
        AppendLog($"[Template] Saved as template \"{saved}\".", ConsoleMessageType.System);
    }

    [RelayCommand]
    private void ApplyTemplate(Models.ServerTemplate? template)
    {
        if (template == null) return;
        _templates.ApplyToServer(template, Server);
        AppendLog($"[Template] Applied \"{template.Name}\".", ConsoleMessageType.System);
    }

    [RelayCommand]
    private void CloneTemplate(Models.ServerTemplate? template)
    {
        if (template == null) return;
        var clone = _templates.Clone(template.Id);
        RefreshTemplates();
        AppendLog($"[Template] Cloned as \"{clone.Name}\".", ConsoleMessageType.System);
    }

    [RelayCommand]
    private void DeleteTemplate(Models.ServerTemplate? template)
    {
        if (template == null) return;
        _templates.Delete(template.Id);
        RefreshTemplates();
    }

    [RelayCommand]
    private void ExportTemplate(Models.ServerTemplate? template)
    {
        if (template == null) return;
        var hasImg  = HasCustomImage;
        var ext     = hasImg ? ".hpt" : ".json";
        var filter  = hasImg ? "HighPop template with image|*.hpt|JSON file|*.json"
                             : "JSON file|*.json";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export template",
            Filter     = filter,
            FileName   = SanitizeFileName(template.Name) + ext,
            DefaultExt = ext,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _templates.ExportSingle(template.Id, dlg.FileName,
                hasImg ? CustomImagePath : null);
            AppendLog($"[Template] Exported: {dlg.FileName}", ConsoleMessageType.System);
        }
        catch (Exception ex)
        {
            AppendLog($"[Template] Export failed: {ex.Message}", ConsoleMessageType.Error);
        }
    }

    [RelayCommand]
    private void ImportTemplates()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title       = "Import templates",
            Filter      = "HighPop template|*.hpt;*.wgst;*.json|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        int total = 0;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                var imgDir = Path.Combine(_config.AppDataPath, "server_images");
                var (count, imgPath) = _templates.Import(file, imgDir);
                total += count;
                // If image was extracted and a template was imported, rename image to match new template id
                if (imgPath != null && count > 0)
                {
                    var newId = _templates.All.Last().Id;
                    var dest  = Path.Combine(imgDir, newId + ".png");
                    if (File.Exists(imgPath)) File.Move(imgPath, dest, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[Template] Import failed ({Path.GetFileName(file)}): {ex.Message}",
                    ConsoleMessageType.Error);
            }
        }
        if (total > 0)
        {
            RefreshTemplates();
            AppendLog($"[Template] Imported {total} template(s).", ConsoleMessageType.System);
        }
    }

    private void RefreshTemplates()
    {
        OnPropertyChanged(nameof(GameTemplates));
        OnPropertyChanged(nameof(FilteredTemplates));
        OnPropertyChanged(nameof(AvailableCategories));
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ── Scheduled tasks ──────────────────────────────────────────────────────

    [RelayCommand]
    private void RefreshScheduledTasks()
        => ScheduledTasks = _scheduler.Tasks.Where(t => t.ServerId == Server.Id).ToList();

    [RelayCommand]
    private void AddScheduledTask(Services.ScheduledTask? task)
    {
        if (task == null) return;
        task.ServerId   = Server.Id;
        task.ServerName = Server.DisplayName;
        _scheduler.AddTask(task);
        RefreshScheduledTasks();
    }

    [RelayCommand]
    private void RemoveScheduledTask(Services.ScheduledTask? task)
    {
        if (task == null) return;
        _scheduler.RemoveTask(task.Id);
        RefreshScheduledTasks();
    }

    // ── Performance monitoring ───────────────────────────────────────────────

    private void StartPerfMonitoring()
    {
        var inst = _manager.GetInstance(Server.Id);
        if (inst?.Process == null) return;
        _perfMonitor.Track(Server.Id, inst.Process.Id);

        lock (_perfLock)
        {
            _perfTimer?.Stop();
            _perfTimer?.Dispose();
            _perfTimer = new System.Timers.Timer(2000);
            _perfTimer.Elapsed += (_, _) =>
            {
                lock (_perfLock)
                {
                    if (_perfTimer == null) return; // stopped between fire and lock
                }
                var m = _perfMonitor.Get(Server.Id);
                if (m == null) return;
                _perfHistory.Record(Server.Id, m.CurrentCpu, m.CurrentMemMb);
                UpdatePerfChart();
                WpfApplication.Current?.Dispatcher?.Invoke(() =>
                {
                    CpuPercent = Math.Round(m.CurrentCpu, 1);
                    MemoryMb   = m.CurrentMemMb;
                    OnPropertyChanged(nameof(UptimeText));
                });
            };
            _perfTimer.Start();
        }
    }

    /// <summary>
    /// Stops live monitoring for this ViewModel. Does NOT clear recorded history — this
    /// runs whenever the ViewModel is disposed (e.g. switching servers, closing HighPop), which
    /// must not erase history for a server that's still actually running.
    /// </summary>
    private void StopPerfMonitoring()
    {
        lock (_perfLock)
        {
            _perfTimer?.Stop();
            _perfTimer?.Dispose();
            _perfTimer = null;
        }
        _perfMonitor.Untrack(Server.Id);
        _perfModel  = null;
        _cpuSeries  = null;
        _memSeries  = null;
        WpfApplication.Current?.Dispatcher?.Invoke(() => { CpuPercent = 0; MemoryMb = 0; PerfPlot = CreateEmptyPlot(); });
    }

    // ── Events ───────────────────────────────────────────────────────────────

    private void OnLogReceived(string serverId, ConsoleMessage msg)
    {
        if (serverId != Server.Id) return;
        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            Log.Add(msg);
            while (Log.Count > MaxConsoleLines)
                Log.RemoveAt(0);
            if (!string.IsNullOrWhiteSpace(ConsoleFilter))
                OnPropertyChanged(nameof(FilteredLog));
        });
    }

    private void OnStatusChanged(string serverId, ServerStatus status)
    {
        if (serverId != Server.Id) return;
        WpfApplication.Current?.Dispatcher?.Invoke(RefreshStatus);
        _ = _notifications.NotifyServerStatusAsync(Server, status);

        var actionText = status switch
        {
            ServerStatus.Running  => "Server started",
            ServerStatus.Stopped  => "Server stopped",
            ServerStatus.Error    => "Server crashed",
            ServerStatus.Updating => "Server updating",
            ServerStatus.Stopping => "Server stopping",
            _ => null
        };
        if (actionText != null) AddActionLog(actionText);

        // Restart the auto-update timer when the server comes back up (e.g. after crash-restart)
        // StartAsync relay command is NOT called on crash-restarts — ServerManagerService handles that.
        if (status == ServerStatus.Running)
        {
            WpfApplication.Current?.Dispatcher?.Invoke(() =>
            {
                StartPerfMonitoring();
                StartPlayerRefresh();
                if (_updateTimer == null) StartUpdateTimer();
            });
            _ = ReplayGroupBansAsync();
        }
        else if (status == ServerStatus.Stopped || status == ServerStatus.Error)
        {
            WpfApplication.Current?.Dispatcher?.Invoke(() =>
            {
                StopPerfMonitoring();
                _perfHistory.Clear(Server.Id);
                StopPlayerRefresh();
                OnlinePlayers = [];
                Server.CurrentPlayers = 0;
                if (!Server.AutoRestart)
                    StopUpdateTimer();
            });
        }
    }

    private void OnPortsReassigned(Models.GameServer srv)
    {
        if (srv.Id != Server.Id) return;
        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            OnPropertyChanged(nameof(Server));
        });
    }

    private void OnCrashLimitReached(string serverId)
    {
        if (serverId != Server.Id) return;
        WpfApplication.Current?.Dispatcher?.Invoke(StopPerfMonitoring);
        StopUpdateTimer();
        _ = _notifications.NotifyAsync(
            $"⛔ {Server.DisplayName} — Auto-restart disabled",
            $"Server crashed too many times. Manual restart required.",
            "#F85149");
    }

    private void OnSteamOutput(string serverId, string line)
    {
        if (serverId.Length > 0 && serverId != Server.Id) return;
        WpfApplication.Current?.Dispatcher?.Invoke(() => AppendLog(line, ConsoleMessageType.System));
    }

    private void OnSteamProgress(string serverId, int p)
    {
        if (serverId.Length > 0 && serverId != Server.Id) return;
        WpfApplication.Current?.Dispatcher?.Invoke(() => InstallProgress = p);
    }

    private void AppendLog(string text, ConsoleMessageType type = ConsoleMessageType.Info)
    {
        // Split on newlines so batched output (e.g. from BuildTools flush) renders as separate
        // lines, not one invisible block. BeginInvoke (async) so the calling thread never blocks
        // waiting for the UI to process the log — this keeps other servers' buttons responsive.
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        WpfApplication.Current?.Dispatcher?.BeginInvoke(() =>
        {
            foreach (var line in lines)
                Log.Add(new ConsoleMessage { Text = line, Type = type });
        });
    }

    public void AppendConsoleWarning(string text)
        => AppendLog(text, ConsoleMessageType.Warning);

    private void AddActionLog(string action)
    {
        var entry = $"[{DateTime.Now:dd.MM HH:mm:ss}] {action}";
        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            ActionLog.Insert(0, entry);
            if (ActionLog.Count > 100) ActionLog.RemoveAt(ActionLog.Count - 1);
        });
    }

    private void RefreshStatus()
    {
        StatusText = Server.Status switch
        {
            ServerStatus.Running      => Loc.StatusRunning,
            ServerStatus.Starting     => Loc.StatusStarting,
            ServerStatus.Stopping     => Loc.StatusStopping,
            ServerStatus.Installing   => Loc.StatusInstalling,
            ServerStatus.Updating     => Loc.StatusUpdating,
            ServerStatus.Error        => Loc.StatusError,
            ServerStatus.NotInstalled => Loc.StatusNotInstalled,
            _                         => Loc.StatusStopped,
        };
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(UptimeText));
    }

    public void Dispose()
    {
        _manager.LogReceived       -= OnLogReceived;
        _manager.StatusChanged     -= OnStatusChanged;
        _manager.CrashLimitReached -= OnCrashLimitReached;
        _manager.PortsReassigned   -= OnPortsReassigned;
        _steamCmd.OutputReceived   -= OnSteamOutput;
        _steamCmd.ProgressChanged  -= OnSteamProgress;
        _network.ServerStatsUpdated -= OnServerStatsUpdated; // #1: estää muistivuodon poistetuille palvelimille

        StopUpdateTimer();
        StopPerfMonitoring();
        StopPlayerRefresh();
        if (_rconLock.Wait(TimeSpan.FromSeconds(3)))
        {
            try { _rcon?.Dispose(); _rcon = null; }
            finally { _rconLock.Release(); }
        }
        else
        {
            // Lock timed out during shutdown — dispose anyway to avoid resource leak
            try { _rcon?.Dispose(); } catch { }
            _rcon = null;
        }

        _rconLock.Dispose();
    }

    // ── Plugin-specific settings fields ──────────────────────────────────────
    public List<PluginFieldVm> PluginFields { get; }
}

public partial class PluginFieldVm : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly GameServer _server;
    public HighPop.Games.ConfigField Field { get; }

    public PluginFieldVm(GameServer server, HighPop.Games.ConfigField field)
    {
        _server = server;
        Field   = field;
    }

    public string Value
    {
        get => _server.GameSpecificSettings.TryGetValue(Field.Key, out var v) ? v : Field.DefaultValue;
        set
        {
            _server.GameSpecificSettings[Field.Key] = value;
            OnPropertyChanged();
        }
    }
}
