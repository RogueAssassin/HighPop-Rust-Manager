Warning: truncated output (original token count: 33971)
Total output lines: 3250

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
    private readonly RustTelemetryService   _telemetry;
    private RconService? _rcon;
    private readonly SemaphoreSlim _rconLock  = new(1, 1);
    private CancellationTokenSource? _rconAutoConnectCts;
    private int _lastTelemetryPlayerCount = -1;
    private readonly object        _perfLock  = new();
    private readonly ConcurrentQueue<ConsoleMessage> _pendingConsoleMessages = new();
    private int _consoleFlushScheduled;
    private System.Timers.Timer?   _updateTimer;
    private readonly SemaphoreSlim _periodicUpdateGate = new(1, 1);

    public GameServer Server { get; }
    public IGamePlugin? Plugin { get; }
    private const int MaxConsoleLines = 2000;
    public ObservableCollection<ConsoleMessage> Log { get; } = [];
    public ObservableCollection<ConsoleMessage> FilteredLog { get; } = [];
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
    [ObservableProperty] private string _rconStatusText = "Disconnected";
    [ObservableProperty] private string _consoleFilter   = string.Empty;
    [ObservableProperty] private bool _showConsoleInfo = true;
    [ObservableProperty] private bool _showConsoleWarnings = true;
    [ObservableProperty] private bool _showConsoleErrors = true;
    [ObservableProperty] private bool _isConsolePaused;
    [ObservableProperty] private string _portConfigurationStatus = "Ports are valid.";
    [ObservableProperty] private string _modStatusText   = string.Empty;
    [ObservableProperty] private bool   _modBusy;
    [ObservableProperty] private string _detectedModFramework = "Not scanned";
    [ObservableProperty] private string _rogueRustStatus = "Not installed";
    [ObservableProperty] private List<InstalledModPlugin> _installedPlugins = [];
    [ObservableProperty] private bool _isOxideInstalled;
    [ObservableProperty] private bool _isCarbonInstalled;
    [ObservableProperty] private bool _isWorkspaceActive;

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
    [ObservableProperty] private OxyPlot.PlotModel _operationsPlot = CreateEmptyPlot();
    private OxyPlot.PlotModel?          _perfModel;
    private OxyPlot.PlotModel?          _operationsModel;
    private OxyPlot.Series.LineSeries?  _cpuSeries;
    private OxyPlot.Series.LineSeries?  _memSeries;
    private OxyPlot.Series.LineSeries?  _netInSeries;
    private OxyPlot.Series.LineSeries?  _netOutSeries;
    private OxyPlot.Series.LineSeries?  _playersSeries;
    [ObservableProperty] private string _chartSummary = "No samples yet";
    [ObservableProperty] private int _perfRangeMinutes = 15;
    partial void OnPerfRangeMinutesChanged(int _) => UpdatePerfChart();
    public IReadOnlyList<int> PerfRangeOptions { get; } = [5, 15, 30, 60];

    public IReadOnlyList<string> RustServerProfileOptions { get; } =
        ["Community (Vanilla)", "Modded", "Development"];
    public IReadOnlyList<string> RustSteamBranchOptions { get; } =
        ["public", "staging", "aux01"];
    public IReadOnlyList<string> RogueRustChannelOptions { get; } = ["Stable", "Testing"];
    public ObservableCollection<RustBrowserTagOption> RustBrowserTags { get; } = [];
    public string EffectiveLogDirectory => IsRust
        ? RustPlugin.GetEffectiveLogDirectory(Server)
        : Server.LogDirectory;
    public string ServerConfigPath => IsRust
        ? RustPlugin.GetServerConfigPath(Server)
        : string.Empty;
    public string TelemetryDirectory => IsRust
        ? _telemetry.GetServerDirectory(Server)
        : string.Empty;
    [ObservableProperty] private string _rustVariableStatus = "server.cfg has not been read yet.";
    [ObservableProperty] private int _rustVariablePendingCount;
    [ObservableProperty] private string _rustVariableFilter = string.Empty;
    public IEnumerable<RustServerVariable> FilteredRustServerVariables =>
        string.IsNullOrWhiteSpace(RustVariableFilter)
            ? Server.RustServerVariables
            : Server.RustServerVariables.Where(variable =>
                variable.Name.Contains(RustVariableFilter, StringComparison.OrdinalIgnoreCase)
                || variable.Description.Contains(RustVariableFilter, StringComparison.OrdinalIgnoreCase));
    partial void OnRustVariableFilterChanged(string value) =>
        OnPropertyChanged(nameof(FilteredRustServerVariables));
    private readonly HashSet<RustServerVariable> _trackedRustVariables = [];
    private bool _syncingRustTags;

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
    public bool HasSingleModFramework => IsOxideInstalled ^ IsCarbonInstalled;
    public bool CanInstallOxide => IsStopped && !ModBusy && !IsCarbonInstalled;
    public bool CanInstallCarbon => IsStopped && !ModBusy && !IsOxideInstalled;

    partial void OnModBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallOxide));
        OnPropertyChanged(nameof(CanInstallCarbon));
    }

    partial void OnIsWorkspaceActiveChanged(bool value)
    {
        if (value) UpdatePerfChart();
    }

    public List<CpuCoreItem> CpuCores { get; }
    public string[] PriorityOptions { get; } = ["Normal", "AboveNormal", "High", "BelowNormal", "RealTime"];

    // ── Batch selection ───────────────────────────────────────────────────────
    [ObservableProperty] private bool _isBatchSelected;
    internal Action? BatchSelectionChanged { get; set; }
    internal Func<GameServer, ServerPortSet, string?>? ValidatePortSet { get; set; }
    partial void OnIsBatchSelectedChanged(bool _) => BatchSelectionChanged?.Invoke();

    public string LifecycleDetailText
    {
        get
        {
            var phase = Server.LifecyclePhase switch
            {
                ServerLifecyclePhase.StoppedByOperator => "Stopped by operator",
                ServerLifecyclePhase.ProcessRunning => "Process running",
                ServerLifecyclePhase.RustReady => "Rust ready",
                ServerLifecyclePhase.RconReady => "WebRCON ready",
                _ => Server.LifecyclePhase.ToString(),
            };
            return string.IsNullOrWhiteSpace(Server.LastLifecycleReason)
                ? phase
                : $"{phase} · {Server.LastLifecycleReason}";
        }
    }

    public string StatusColor => Server.LifecyclePhase switch
    {
        ServerLifecyclePhase.Online or ServerLifecyclePhase.RconReady or ServerLifecyclePhase.RustReady => "#3FB950",
        ServerLifecyclePhase.Starting or ServerLifecyclePhase.ProcessRunning => "#22D3EE",
        ServerLifecyclePhase.Stopping or ServerLifecyclePhase.Maintenance => "#A855F7",
        ServerLifecyclePhase.Recovering or ServerLifecyclePhase.Degraded => "#D29922",
        ServerLifecyclePhase.Faulted => "#F85149",
        _ => Server.Status switch
    {
        ServerStatus.Running      => "#3FB950",
        ServerStatus.Starting     => "#22D3EE",
        ServerStatus.Stopping     => "#A855F7",
        ServerStatus.Stopped      => "#8B949E",
        ServerStatus.Installing   => "#22D3EE",
        ServerStatus.Updating     => "#A855F7",
        ServerStatus.Error        => "#F85149",
        ServerStatus.NotInstalled => "#8B949E",
        _                         => "#8B949E",
        },
    };

    public bool IsRunning    => Server.Status == ServerStatus.Running;
    public bool IsStopped    => Server.Status is ServerStatus.Stopped or ServerStatus.NotInstalled;
    public bool CanStart     => Server.Status is ServerStatus.Stopped or ServerStatus.Error or ServerStatus.NotInstalled;
    public bool CanStop      => Server.Status is ServerStatus.Running or ServerStatus.Starting;
    public bool CanInstallServerFiles => !IsInstalling && !IsRunning;
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
    public string LastExitSummary => string.IsNullOrWhiteSpace(Server.LastExitReason)
        ? "No recorded process exit yet."
        : $"{Server.LastExitAt:g} — {Server.LastExitReason}";

    public int RustPlusPort
    {
        get => Server.GameSpecificSettings.TryGetValue("appPort", out var text)
            && int.TryParse(text, out var port) ? port : 0;
        set
        {
            if (RustPlusPort == value) return;
            if (!TryAcceptPortSet(CurrentPortSet() with { RustPlus = value })) return;
            Server.GameSpecificSettings["appPort"] = value.ToString();
            OnPropertyChanged();
        }
    }

    public int GamePort
    {
        get => Server.ServerPort;
        set
        {
            if (Server.ServerPort == value) return;
            if (!TryAcceptPortSet(CurrentPortSet() with { Game = value })) return;
            Server.ServerPort = value;
            OnPropertyChanged();
        }
    }

    public int QueryPort
    {
        get => Server.QueryPort;
        set
        {
            if (Server.QueryPort == value) return;
            if (!TryAcceptPortSet(CurrentPortSet() with { Query = value })) return;
            Server.QueryPort = value;
            OnPropertyChanged();
        }
    }

    public int RconPort
    {
        get => Server.RconPort;
        set
        {
            if (Server.RconPort == value) return;
            if (!TryAcceptPortSet(CurrentPortSet() with { Rcon = value })) return;
            Server.RconPort = value;
            OnPropertyChanged();
        }
    }

    private ServerPortSet CurrentPortSet() => new(
        Server.ServerPort, Server.QueryPort, Server.RconPort, RustPlusPort);

    private bool TryAcceptPortSet(ServerPortSet candidate)
    {
        if (!IsStopped)
        {
            PortConfigurationStatus = "Stop Rust before changing ports.";
            return false;
        }
        var error = ValidatePortSet?.Invoke(Server, candidate);
        PortConfigurationStatus = error ?? "Ports are valid and available.";
        return error == null;
    }

    public bool AutoConnectRcon
    {
        get => Server.AutoConnectRcon;
        set
        {
            if (Server.AutoConnectRcon == value) return;
            Server.AutoConnectRcon = value;
            OnPropertyChanged();
            if (value) StartAutoRconConnect();
            else
            {
                _rconAutoConnectCts?.Cancel();
                RconStatusText = RconConnected ? "Connected (auto-reconnect off)" : "Auto-connect disabled";
            }
            AddActionLog($"WebRCON auto-connect {(value ? "enabled" : "disabled")}");
        }
    }

    public string CustomLogDirectory
    {
        get => Server.LogDirectory;
        set
        {
            if (Server.LogDirectory == value) return;
            Server.LogDirectory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveLogDirectory));
        }
    }

    public string RustServerProfile
    {
        get => Server.RustServerProfile;
        set
        {
            if (Server.RustServerProfile == value) return;
            Server.RustServerProfile = value;
            OnPropertyChanged();
            AddActionLog($"Rust server profile changed to {value}");
        }
    }

    public string RustSteamBranch
    {
        get => Server.GameSpecificSettings.GetValueOrDefault("steamBranch", "public");
        set
        {
            var branch = string.IsNullOrWhiteSpace(value) ? "public" : value.Trim();
            if (RustSteamBranch == branch) return;
            Server.GameSpecificSettings["steamBranch"] = branch;
            OnPropertyChanged();
            if (!branch.Equals("public", StringComparison.OrdinalIgnoreCase)
                && RustServerProfile != "Modded")
                RustServerProfile = "Development";
            else if (branch.Equals("public", StringComparison.OrdinalIgnoreCase)
                     && RustServerProfile == "Development")
                RustServerProfile = "Community (Vanilla)";
            AddActionLog($"Rust SteamCMD branch changed to {branch}");
        }
    }

    public string RogueRustChannel
    {
        get => ModManagerService.NormalizeRogueRustChannel(Server.RogueRustChannel);
        set
        {
            var channel = ModManagerService.NormalizeRogueRustChannel(value);
            if (RogueRustChannel == channel) return;
            Server.RogueRustChannel = channel;
            OnPropertyChanged();
            RogueRustStatus = ModManagerService.GetInstalledRogueRustVersion(Server.InstallPath) is { Length: > 0 } version
                ? $"Installed {version} · {channel} channel"
                : $"Not installed · {channel} channel";
            AddActionLog($"RogueRust channel changed to {channel}");
        }
    }

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

    public ServerViewModel(GameServer server, ServerManagerService manager, SteamCmdService steamCmd,
        BackupService backup, NotificationService notifications, PerformanceMonitorService perfMonitor,
        ConfigService config, ModManagerService mods,
        ConfigEditorService configEditor, PlayerStatsService playerStats,
        PerfHistoryService perfHistory,
        TemplateService templates, ScheduledTaskService scheduler, NetworkMonitorService network,
        GroupBanListService groupBans, RustModerationService moderation, ServerHygieneService hygiene,
        ConfigPresetService presets, RustTelemetryService telemetry)
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
        _telemetry     = telemetry;
        Server.RustServerVariables ??= RustServerVariable.CreateDefaults();
        Server.RustServerVariables.CollectionChanged += (_, _) => TrackRustVariableChanges();
        if (IsRust)
        {
            try
            {
                var count = RustPlugin.LoadServerConfigVariables(Server);
                RustVariableStatus = File.Exists(RustPlugin.GetServerConfigPath(Server))
                    ? $"Loaded {count} active variable(s) from server.cfg."
                    : "server.cfg does not exist yet. Save to create it.";
            }
            catch (Exception ex)
            {
                RustVariableStatus = $"Could not read server.cfg: {ex.Message}";
            }
        }
        TrackRustVariableChanges();
        AvailablePresets = _presets.GetPresetsForGame(server.GameId);
        SelectedPreset   = AvailablePresets.FirstOrDefault();
        InitializeRustBrowserTags();

        _network.ServerStatsUpdated += OnServerStatsUpdated;
        FileBrowser.Initialize(server.InstallPath);

        _manager.LogReceived      += OnLogReceived;
        _manager.StatusChanged    += OnStatusChanged;
        _manager.LifecycleChanged += OnLifecycleChanged;
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
            .Where(f => f.Key is not ("serverName" or "maxPlayers" or "serverPass"
                or "steamBranch" or "tags" or "appPort"))
            .Select(f => new PluginFieldVm(server, f, () =>
            {
                if (f.Key != "identity") return;
                OnPropertyChanged(nameof(EffectiveLogDirectory));
                OnPropertyChanged(nameof(ServerConfigPath));
                RustVariableStatus = "Identity changed. Reload server.cfg from the new identity path.";
            }))
            .ToList() ?? [];

        RefreshStatus();
        RefreshBackups();
        RefreshModerationProfiles();
        RefreshInstalledPlugins();
        RefreshScheduledTasks();

        // If the server was already running when this ViewModel was created (e.g. reattached
        // to a process that survived a HighPop restart), the StatusChanged(Running) event already
        // fired before we subscribed to it. Start monitoring directly in that case.
        if (IsRunning)
        {
            StartPerfMonitoring();
            StartPlayerRefresh();
            StartUpdateTimer();
            StartAutoRconConnect();
        }
        else
        {
            _playerStats.CloseOpenSessions(Server.Id);
        }
    }

    private void InitializeRustBrowserTags()
    {
        if (!IsRust) return;
        var definitions = new (string Value, string Label, string Group)[]
        {
            ("monthly", "Monthly", "wipe"),
            ("biweekly", "Biweekly", "wipe"),
            ("weekly", "Weekly", "wipe"),
            ("vanilla", "Vanilla", "difficulty"),
            ("hardcore", "Hardcore", "difficulty"),
            ("softcore", "Softcore", "difficulty"),
            ("pve", "PvE", ""),
            ("roleplay", "Roleplay", ""),
            ("creative", "Creative", ""),
            ("minigame", "Minigame", ""),
            ("training", "Combat Training", ""),
            ("battlefield", "Battlefield", ""),
            ("broyale", "Battle Royale", ""),
            ("builds", "Build Server", ""),
            ("tut", "Tutorial", ""),
            ("premium", "Premium", ""),
            ("NA", "North America", "region"),
            ("SA", "South America", "region"),
            ("EU", "Europe", "region"),
            ("WA", "West Asia", "region"),
            ("EA", "East Asia", "region"),
            ("OC", "Oceania", "region"),
            ("AF", "Africa", "region"),
        };
        var selected = Server.GameSpecificSettings
            .GetValueOrDefault("tags", "monthly,vanilla")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _syncingRustTags = true;
        foreach (var (value, label, group) in definitions)
        {
            var option = new RustBrowserTagOption(value, label, group)
            {
                IsSelected = selected.Contains(value),
            };
            option.SelectionChanged += OnRustBrowserTagChanged;
            RustBrowserTags.Add(option);
        }
        _syncingRustTags = false;
    }

    private void OnRustBrowserTagChanged(RustBrowserTagOption changed)
    {
        if (_syncingRustTags) return;
        _syncingRustTags = true;
        try
        {
            if (changed.IsSelected && !string.IsNullOrEmpty(changed.Group))
            {
                foreach (var option in RustBrowserTags.Where(o =>
                    o != changed && o.Group.Equals(changed.Group, StringComparison.Ordinal)))
                    option.IsSelected = false;
            }
            if (changed.IsSelected && RustBrowserTags.Count(o => o.IsSelected) > 4)
            {
                changed.IsSelected = false;
                AppendLog("[Rust] Rust displays at most four browser tags. Deselect one before adding another.",
                    ConsoleMessageType.Warning);
            }
            Server.GameSpecificSettings["tags"] = string.Join(",",
                RustBrowserTags.Where(o => o.IsSelected).Select(o => o.Value));
            AddActionLog($"Rust browser tags changed to {Server.GameSpecificSettings["tags"]}");
        }
        finally { _syncingRustTags = false; }
    }

    // ── Start / Stop / Restart ──────────────────────────────────────────────

    [RelayCommand]
    private Task StartAsync() => StartManagedAsync(LifecycleInitiator.Operator, "Requested from the manager UI");

    public async Task StartManagedAsync(LifecycleInitiator initiator, string reason)
    {
        AddActionLog("Start requested");
        try
        {
            if (Server.UpdateOnStart && Plugin?.SteamAppId > 0)
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

            await _manager.StartAsync(Server, initiator, reason);
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
    private Task StopAsync() => StopManagedAsync(LifecycleInitiator.Operator, "Requested from the manager UI");

    public async Task StopManagedAsync(LifecycleInitiator initiator, string reason)
    {
        AddActionLog("Stop requested");
        try
        {
            StopUpdateTimer();
            await _manager.StopAsync(Server, reason, initiator);
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
    private async Task ForceStopAsync()
    {
        AddActionLog("Force Stop requested");
        try
        {
            StopUpdateTimer();
            await _manager.ForceStopAsync(Server);
            AppendLog("[HighPop] Force Stop completed.", ConsoleMessageType.System);
            StopPerfMonitoring();
        }
        catch (Exception ex) { AppendLog("[ERR] " + ex.Message, ConsoleMessageType.Error); }
    }

    [RelayCommand]
    private void ShowWindow() => _manager.ShowWindow(Server);

    [RelayCommand]
    private Task RestartAsync() => RestartManagedAsync(
        LifecycleInitiator.Operator, "Requested from the manager UI");

    public async Task RestartManagedAsync(LifecycleInitiator initiator, string reason)
    {
        AddActionLog("Restart requested");
        AppendLog("[HighPop] " + Loc.StatusStopping, ConsoleMessageType.System);
        await StopManagedAsync(initiator, reason);
        var stoppedGeneration = Server.LifecycleGeneration;
        await Task.Delay(3000);
        if (!ServerLifecycleRules.IsCurrent(Server, stoppedGeneration)
            || Server.DesiredState != ServerDesiredState.Stopped)
        {
            AppendLog("[HighPop] Restart cancelled because a newer lifecycle request superseded it.",
                ConsoleMessageType.Warning);
            return;
        }
        await StartManagedAsync(initiator, reason);
    }

    // ── Install / Update ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Plugin == null) return;
        if (IsRunning)
        {
            AppendLog(
                "[HighPop] Install/Update is locked while Rust is running. Use Update to run the warned save → stop → update → start workflow, or stop the server first.",
                ConsoleMessageType.Warning);
            return;
        }
        AddActionLog("Install/update requested");

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
            AddActionLog("Install/update completed");
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
            AddActionLog($"Install/update failed: {ex.Message}");
            Server.Status = ServerStatus.Error;
        }
        finally { IsInstalling = false; RefreshStatus(); }
    }


    [RelayCommand]
    private async Task UpdateAsync()
    {
        AppendLog("[HighPop] " + Loc.StatusUpdating, ConsoleMessageType.System);
        if (IsRunning)
            await RunPeriodicUpdateAsync(operatorRequested: true);
        else
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
        Appe…13971 tokens truncated…nk.Count == 0)
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
        AddActionLog($"Player kicked: {PlayerInput}");
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
        AddActionLog($"Player banned: {playerName} ({durationText})");
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
        AddActionLog($"Bulk kick completed for {completed} player(s)");
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
        AddActionLog($"Bulk ban completed for {completed} player(s)");
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
        AddActionLog($"Player unbanned: {playerName}");
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
        AddActionLog($"Staff note updated for {playerName}");
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
        AddActionLog($"Whitelist granted: {playerName}");
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
        AddActionLog($"Whitelist revoked: {playerName}");
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
            try
            {
                var r = await _rcon.SendCommandAsync(cmd);
                if (!string.IsNullOrEmpty(r)) AppendLog(r);
                if (_rcon.IsConnected) return;
            }
            catch (Exception ex)
            {
                AppendLog($"[RCON] Connection lost: {ex.Message}", ConsoleMessageType.Warning);
            }
            finally { _rconLock.Release(); }

            WpfApplication.Current?.Dispatcher?.Invoke(() =>
            {
                RconConnected = false;
                RconStatusText = "Connection lost — using process console";
            });
            if (Server.AutoConnectRcon) StartAutoRconConnect();
        }
        await _manager.SendCommandAsync(Server.Id, cmd);
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

    [RelayCommand]
    private async Task RunScheduledTaskNowAsync(Services.ScheduledTask? task)
    {
        if (task == null || task.IsRunning) return;
        AddActionLog($"Scheduled {task.ActionText} started manually");
        await _scheduler.ExecuteNowAsync(task.Id);
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
                var network = _network.GetServerStats(Server.Id);
                _perfHistory.Record(
                    Server.Id,
                    m.CurrentCpu,
                    m.CurrentMemMb,
                    (network?.BytesInPerSec ?? 0) / 1024.0,
                    (network?.BytesOutPerSec ?? 0) / 1024.0,
                    Server.CurrentPlayers);
                if (IsWorkspaceActive) UpdatePerfChart();
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
        _perfHistory.Flush(Server.Id);
        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            CpuPercent = 0;
            MemoryMb = 0;
        });
        UpdatePerfChart();
    }

    // ── Events ───────────────────────────────────────────────────────────────

    private void OnLogReceived(string serverId, ConsoleMessage msg)
    {
        if (serverId != Server.Id) return;
        EnqueueConsoleMessage(msg);
    }

    private void OnStatusChanged(string serverId, ServerStatus status)
    {
        if (serverId != Server.Id) return;
        WpfApplication.Current?.Dispatcher?.Invoke(RefreshStatus);
        WpfApplication.Current?.Dispatcher?.Invoke(() => OnPropertyChanged(nameof(LastExitSummary)));
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
                StartAutoRconConnect();
            });
            _ = ReplayGroupBansAsync();
        }
        else if (status == ServerStatus.Stopped || status == ServerStatus.Error)
        {
            _rconAutoConnectCts?.Cancel();
            _playerStats.CloseOpenSessions(Server.Id);
            _playersFetchedOnce = false;
            _ = DisconnectRconForStopAsync();
            WpfApplication.Current?.Dispatcher?.Invoke(() =>
            {
                StopPerfMonitoring();
                StopPlayerRefresh();
                OnlinePlayers = [];
                Server.CurrentPlayers = 0;
                Server.LastPlayerSampleAt = null;
                if (!Server.AutoRestart)
                    StopUpdateTimer();
            });
        }
    }

    private void OnLifecycleChanged(ServerLifecycleTransition transition)
    {
        if (transition.ServerId != Server.Id) return;
        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            OnPropertyChanged(nameof(LifecycleDetailText));
            OnPropertyChanged(nameof(StatusColor));
        });
    }

    private async Task DisconnectRconForStopAsync()
    {
        await _rconLock.WaitAsync();
        try
        {
            if (_rcon != null)
                await _rcon.DisconnectAsync();
            _rcon = null;
        }
        catch { }
        finally { _rconLock.Release(); }

        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            RconConnected = false;
            RconStatusText = "Disconnected";
        });
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
        AppendLog(line, ConsoleMessageType.System, "SteamCMD");
    }

    private void OnSteamProgress(string serverId, int p)
    {
        if (serverId.Length > 0 && serverId != Server.Id) return;
        WpfApplication.Current?.Dispatcher?.Invoke(() => InstallProgress = p);
    }

    private void AppendLog(
        string text,
        ConsoleMessageType type = ConsoleMessageType.Info,
        string source = "HighPop")
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
            EnqueueConsoleMessage(new ConsoleMessage
            {
                Text = line.TrimEnd('\r'),
                Type = type,
                Source = InferConsoleSource(line, source),
            });
    }

    private void EnqueueConsoleMessage(ConsoleMessage message)
    {
        _pendingConsoleMessages.Enqueue(message);
        ScheduleConsoleFlush();
    }

    private void ScheduleConsoleFlush()
    {
        if (Interlocked.Exchange(ref _consoleFlushScheduled, 1) != 0) return;
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) FlushConsoleMessages();
        else dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushConsoleMessages));
    }

    private void FlushConsoleMessages()
    {
        var added = 0;
        while (added < 250 && _pendingConsoleMessages.TryDequeue(out var message))
        {
            Log.Add(message);
            if (!IsConsolePaused && MatchesConsoleFilter(message)) FilteredLog.Add(message);
            added++;
        }

        while (Log.Count > MaxConsoleLines)
        {
            var removed = Log[0];
            Log.RemoveAt(0);
            FilteredLog.Remove(removed);
        }

        Interlocked.Exchange(ref _consoleFlushScheduled, 0);
        if (!_pendingConsoleMessages.IsEmpty) ScheduleConsoleFlush();
    }

    private bool MatchesConsoleFilter(ConsoleMessage message)
    {
        var severityVisible = message.Type switch
        {
            ConsoleMessageType.Error => ShowConsoleErrors,
            ConsoleMessageType.Warning => ShowConsoleWarnings,
            _ => ShowConsoleInfo,
        };
        if (!severityVisible) return false;
        if (string.IsNullOrWhiteSpace(ConsoleFilter)) return true;
        return message.Text.Contains(ConsoleFilter, StringComparison.OrdinalIgnoreCase)
            || message.SourceLabel.Contains(ConsoleFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildFilteredLog()
    {
        void Rebuild()
        {
            FilteredLog.Clear();
            foreach (var message in Log.Where(MatchesConsoleFilter)) FilteredLog.Add(message);
        }
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Rebuild();
        else dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Rebuild));
    }

    private static string InferConsoleSource(string line, string fallback)
    {
        if (line.StartsWith("[RCON]", StringComparison.OrdinalIgnoreCase)) return "WebRCON";
        if (line.StartsWith("[SteamCMD]", StringComparison.OrdinalIgnoreCase)) return "SteamCMD";
        if (line.StartsWith("[RogueRust]", StringComparison.OrdinalIgnoreCase)) return "RogueRust";
        if (line.StartsWith("[Oxide]", StringComparison.OrdinalIgnoreCase)) return "Oxide";
        if (line.StartsWith("[Carbon]", StringComparison.OrdinalIgnoreCase)) return "Carbon";
        return fallback;
    }

    public void AppendConsoleWarning(string text)
        => AppendLog(text, ConsoleMessageType.Warning);

    public void RecordAction(string action) => AddActionLog(action);

    public async Task SendManagedCommandAsync(string command, string source)
    {
        AddActionLog($"{source} command: {SummarizeCommand(command)}");
        await SendRconOrConsole(command);
    }

    private void AddActionLog(string action)
    {
        var entry = $"[{DateTime.Now:dd.MM HH:mm:ss}] {action}";
        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            ActionLog.Insert(0, entry);
            if (ActionLog.Count > 100) ActionLog.RemoveAt(ActionLog.Count - 1);
        });
        _ = _telemetry.AppendAsync(
            Server,
            "operator.action",
            "manager",
            new Dictionary<string, string> { ["summary"] = action });
    }

    private static string SummarizeCommand(string command)
    {
        var first = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? "(empty)";
        if (first.Contains("password", StringComparison.OrdinalIgnoreCase)
            || first.Contains("token", StringComparison.OrdinalIgnoreCase)
            || first.Contains("secret", StringComparison.OrdinalIgnoreCase))
            return first + " [value hidden]";

        var safe = new string(command.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return safe.Length <= 100 ? safe : safe[..100] + "…";
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
        OnPropertyChanged(nameof(CanInstallServerFiles));
        OnPropertyChanged(nameof(CanInstallOxide));
        OnPropertyChanged(nameof(CanInstallCarbon));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(UptimeText));
    }

    private void OpenExistingExplorerFolder(string path, string label)
    {
        try
        {
            if (!ModManagerService.OpenExistingFolder(path))
            {
                ModStatusText = $"The {label} folder does not exist yet.";
                AppendLog($"[Files] The {label} folder does not exist yet.", ConsoleMessageType.Warning);
                return;
            }
            AddActionLog($"Opened {label} in Windows Explorer");
        }
        catch (Exception ex)
        {
            ModStatusText = $"Could not open {label}: {ex.Message}";
            AppendLog($"[Files] Could not open {label}: {ex.Message}", ConsoleMessageType.Error);
        }
    }

    public void Dispose()
    {
        _manager.LogReceived       -= OnLogReceived;
        _manager.StatusChanged     -= OnStatusChanged;
        _manager.LifecycleChanged  -= OnLifecycleChanged;
        _manager.CrashLimitReached -= OnCrashLimitReached;
        _manager.PortsReassigned   -= OnPortsReassigned;
        _steamCmd.OutputReceived   -= OnSteamOutput;
        _steamCmd.ProgressChanged  -= OnSteamProgress;
        _network.ServerStatsUpdated -= OnServerStatsUpdated; // #1: estää muistivuodon poistetuille palvelimille

        StopUpdateTimer();
        StopPerfMonitoring();
        StopPlayerRefresh();
        _rconAutoConnectCts?.Cancel();
        _rconAutoConnectCts?.Dispose();
        _rconAutoConnectCts = null;
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

        // Do not dispose the semaphore: a cancelled auto-connect attempt may still be queued
        // briefly, and disposing a semaphore with waiters can surface an ObjectDisposedException
        // during manager shutdown.
    }

    // ── Plugin-specific settings fields ──────────────────────────────────────
    public List<PluginFieldVm> PluginFields { get; }
}

public partial class PluginFieldVm : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly GameServer _server;
    private readonly Action? _changed;
    public HighPop.Games.ConfigField Field { get; }

    public PluginFieldVm(GameServer server, HighPop.Games.ConfigField field, Action? changed = null)
    {
        _server = server;
        Field   = field;
        _changed = changed;
    }

    public string Value
    {
        get => _server.GameSpecificSettings.TryGetValue(Field.Key, out var v) ? v : Field.DefaultValue;
        set
        {
            _server.GameSpecificSettings[Field.Key] = value;
            OnPropertyChanged();
            _changed?.Invoke();
        }
    }
}
