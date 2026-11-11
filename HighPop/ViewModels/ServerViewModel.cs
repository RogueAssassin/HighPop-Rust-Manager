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
    private readonly RustReferenceService   _rustReference;
    private readonly RogueRustPluginCatalogService _pluginCatalog;
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
    public ObservableCollection<string> ActionLog { get; } = [];

    [ObservableProperty] private int _serverNumber;

    [ObservableProperty] private string _consoleInput = string.Empty;
    [ObservableProperty] private int _installProgress;
    [ObservableProperty] private string _installProgressText = "Ready";
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
    [ObservableProperty] private string _detectedModFramework = "Not scanned";
    [ObservableProperty] private List<InstalledModPlugin> _installedPlugins = [];
    [ObservableProperty] private bool _isOxideInstalled;
    [ObservableProperty] private bool _isCarbonInstalled;
    [ObservableProperty] private bool _isWorkspaceActive;

    // Rogue Rust Reference — loaded on demand and shared across all server workspaces.
    private List<RustReferenceEntry> _allRustReferenceEntries = [];
    [ObservableProperty] private List<RustReferenceEntry> _rustReferenceEntries = [];
    [ObservableProperty] private RustReferenceEntry? _selectedRustReferenceEntry;
    [ObservableProperty] private string _rustReferenceSearch = string.Empty;
    [ObservableProperty] private string _rustReferenceKind = "All";
    [ObservableProperty] private string _rustReferenceStatus =
        "Load the latest Rust commands and server variables from Rogue Rust Reference.";
    [ObservableProperty] private bool _rustReferenceLoading;
    public IReadOnlyList<string> RustReferenceKinds { get; } = ["All", "Commands", "ConVars"];

    // Players — reaaliaikainen lista
    [ObservableProperty] private List<Models.OnlinePlayer>    _onlinePlayers  = [];
    [ObservableProperty] private List<Services.PlayerSession> _playerHistory  = [];
    [ObservableProperty] private List<Services.PlayerStats>   _playerStatsList = [];
    [ObservableProperty] private List<Services.PlayerStats>   _mostActivePlayers = [];
    [ObservableProperty] private List<Services.RustPlayerModerationRecord> _moderationProfiles = [];
    [ObservableProperty] private List<HourBar> _hourlyActivity = [];
    [ObservableProperty] private string _playerQueryStatus = "Waiting for the server player query.";

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

    private System.Timers.Timer? _playerRefreshTimer;
    private readonly SemaphoreSlim _playerRefreshGate = new(1, 1);

    public IReadOnlyList<string> RustServerProfileOptions { get; } =
        ["Community (Vanilla)", "Modded", "Development"];
    public IReadOnlyList<string> RustSteamBranchOptions { get; } =
        ["public", "staging", "aux01"];
    public string EffectiveLogDirectory => IsRust
        ? RustPlugin.GetEffectiveLogDirectory(Server)
        : Server.LogDirectory;
    public string ServerConfigPath => IsRust
        ? RustPlugin.GetServerConfigPath(Server)
        : string.Empty;
    public string TelemetryDirectory => IsRust
        ? _telemetry.GetServerDirectory(Server)
        : string.Empty;
    // Status of the server.cfg read at start-up; the Rust page shows it until it has its own.
    private string _rustVariableLoadStatus = "server.cfg has not been read yet.";

    // Log Watcher rules and scheduled tasks (Log Watcher and Schedule pages)
    private ServerAutomationViewModel? _automationVm;
    public ServerAutomationViewModel Automation => _automationVm ??= new(this, _scheduler);

    // Resource limits
    [ObservableProperty] private long _maxRamMb;
    partial void OnMaxRamMbChanged(long value) => Server.MaxRamMb = value;

    // Backups page. Created on first use so servers whose Backups page is never opened
    // do not read their backup folder at startup.
    private ServerBackupsViewModel? _backupsVm;
    public ServerBackupsViewModel Backups => _backupsVm ??= new(this, _backup, _notifications);
    /// <summary>Reloads the backup list if the Backups page has been opened; otherwise it loads on first open.</summary>
    public void RefreshBackups() => _backupsVm?.Refresh();

    // Config files editor, presets and templates. Created on first use.
    private ServerConfigViewModel? _configVm;
    public ServerConfigViewModel Config => _configVm ??= new(this, _configEditor, _presets, _templates, _config);
    /// <summary>Tells the pages that values on <see cref="Server"/> changed outside a bound property.</summary>

    // Mods page: framework installs, RogueRust and the plugin catalog. Created on first use.
    private ServerModsViewModel? _modsVm;
    public ServerModsViewModel Mods => _modsVm ??= new(this, _mods, _pluginCatalog, _manager);

    // Players page admin actions: moderation and player tools. Created on first use.
    private ServerPlayersViewModel? _playersVm;
    public ServerPlayersViewModel Players => _playersVm ??= new(this, _manager, _moderation, _groupBans);

    // Rust page: server.cfg variable editor and browser tags. Created on first use.
    private ServerRustSettingsViewModel? _rustSettingsVm;
    public ServerRustSettingsViewModel RustSettings => _rustSettingsVm ??= new(this, _manager, _rustVariableLoadStatus);

    // Monitoring charts. Created when the Charts page first needs them.
    private ServerMonitoringViewModel? _monitoringVm;
    public ServerMonitoringViewModel Monitoring => _monitoringVm ??= new(this, _perfHistory);
    /// <summary>Stops chart redraws without creating the charts for a server that never showed them.</summary>
    internal void HideCharts()
    {
        if (_monitoringVm != null) _monitoringVm.ChartsVisible = false;
    }
    internal void NotifyServerChanged() => OnPropertyChanged(nameof(Server));

    public bool HasPlayerCommands => Plugin?.GetPlayersCommand() != null
                                  || Plugin?.GetKickCommand("") != null;

    public FileBrowserViewModel FileBrowser { get; } = new();
    private System.Timers.Timer? _perfTimer;
    private int _networkUiUpdatePending;

    public bool HasModSupport       => Plugin?.SupportsOxide == true;
    public bool IsRust              => Plugin?.GameId == "rust";
    public bool HasSingleModFramework => IsOxideInstalled ^ IsCarbonInstalled;

    partial void OnIsWorkspaceActiveChanged(bool value)
    {
        if (value) _monitoringVm?.OnWorkspaceActivated();
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
    public bool HasTrackedProcess => Server.RunningPid > 0 || _manager.IsRunning(Server.Id);
    public bool CanStart     => (Server.Status is ServerStatus.Stopped or ServerStatus.Error or ServerStatus.NotInstalled)
                                && !HasTrackedProcess;
    public bool CanStop      => Server.Status is ServerStatus.Running or ServerStatus.Starting;
    public bool CanForceStop => (Server.Status is ServerStatus.Running or ServerStatus.Starting or ServerStatus.Stopping)
                                || (Server.Status == ServerStatus.Error && HasTrackedProcess);
    public bool CanInstallServerFiles => !IsInstalling && !HasTrackedProcess;
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

    internal string CustomImagePath => Path.Combine(
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
        if (Interlocked.Exchange(ref _networkUiUpdatePending, 1) == 1) return;
        var netIn = NetworkMonitorService.FormatSpeed(stats.BytesInPerSec);
        var netOut = NetworkMonitorService.FormatSpeed(stats.BytesOutPerSec);
        var connectionCount = stats.ConnectionCount;
        var historyIn = stats.HistoryIn.ToList();
        var historyOut = stats.HistoryOut.ToList();

        WpfApplication.Current?.Dispatcher?.BeginInvoke(() =>
        {
            try
            {
                NetIn           = netIn;
                NetOut          = netOut;
                ConnectionCount = connectionCount;
                NetInHistory    = historyIn;
                NetOutHistory   = historyOut;
            }
            finally { Interlocked.Exchange(ref _networkUiUpdatePending, 0); }
        });
    }

    public ServerViewModel(GameServer server, ServerManagerService manager, SteamCmdService steamCmd,
        BackupService backup, NotificationService notifications, PerformanceMonitorService perfMonitor,
        ConfigService config, ModManagerService mods,
        ConfigEditorService configEditor, PlayerStatsService playerStats,
        PerfHistoryService perfHistory,
        TemplateService templates, ScheduledTaskService scheduler, NetworkMonitorService network,
        GroupBanListService groupBans, RustModerationService moderation, ServerHygieneService hygiene,
        ConfigPresetService presets, RustTelemetryService telemetry,
        RustReferenceService rustReference, RogueRustPluginCatalogService pluginCatalog)
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
        _rustReference = rustReference;
        _pluginCatalog = pluginCatalog;
        Server.RustServerVariables ??= RustServerVariable.CreateDefaults();
        if (IsRust)
        {
            try
            {
                var count = RustPlugin.LoadServerConfigVariables(Server);
                _rustVariableLoadStatus = File.Exists(RustPlugin.GetServerConfigPath(Server))
                    ? $"Loaded {count} active variable(s) from server.cfg."
                    : "server.cfg does not exist yet. Save to create it.";
            }
            catch (Exception ex)
            {
                _rustVariableLoadStatus = $"Could not read server.cfg: {ex.Message}";
            }
        }

        _network.ServerStatsUpdated += OnServerStatsUpdated;
        FileBrowser.Initialize(server.InstallPath);

        _manager.LogReceived      += OnLogReceived;
        _manager.StatusChanged    += OnStatusChanged;
        _manager.LifecycleChanged += OnLifecycleChanged;
        _manager.CrashLimitReached += OnCrashLimitReached;
        _manager.PortsReassigned  += OnPortsReassigned;
        _steamCmd.OutputReceived  += OnSteamOutput;
        _steamCmd.ProgressDetailChanged += OnSteamProgressDetail;

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
        foreach (var qc in Server.QuickCommands) QuickCommands.Add(qc);

        PluginFields = Plugin?.GetConfigFields()
            .Where(f => f.Key is not ("serverName" or "maxPlayers" or "serverPass"
                or "steamBranch" or "tags" or "appPort"))
            .Select(f => new PluginFieldVm(server, f, () =>
            {
                if (f.Key != "identity") return;
                OnPropertyChanged(nameof(EffectiveLogDirectory));
                OnPropertyChanged(nameof(ServerConfigPath));
                const string identityStatus = "Identity changed. Reload server.cfg from the new identity path.";
                if (_rustSettingsVm != null) _rustSettingsVm.NotifyIdentityChanged(identityStatus);
                else _rustVariableLoadStatus = identityStatus;
            }))
            .ToList() ?? [];

        RefreshStatus();
        RefreshModerationProfiles();
        RefreshInstalledPlugins();

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
                    _backupsVm?.Refresh();
                }
                catch (Exception ex) { AppendLog($"[HighPop] Backup before start failed: {ex.Message}", ConsoleMessageType.Warning); }
            }

            // Start does file I/O, process scans and pre-flight cleanup; run it off the UI thread.
            await Task.Run(() => _manager.StartAsync(Server, initiator, reason));
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
            // Run the whole shutdown sequence off the UI thread. Stop can take up to the
            // graceful timeout plus process-tree termination; nothing in it may ever hold the
            // dispatcher. Status/log events already marshal back via the Dispatcher.
            await Task.Run(() => _manager.StopAsync(Server, reason, initiator));
            // StopPerfMonitoring() called from OnStatusChanged(Stopped)

            if (Server.BackupOnShutdown)
            {
                try
                {
                    AppendLog("[HighPop] Creating backup after shutdown...", ConsoleMessageType.System);
                    await _backup.CreateBackupAsync(Server);
                    AppendLog("[HighPop] Backup created.", ConsoleMessageType.System);
                    _backupsVm?.Refresh();
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
            await Task.Run(() => _manager.ForceStopAsync(Server));
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
        if (_manager.IsRunning(Server.Id))
        {
            AppendLog("[HighPop] Restart cancelled: the server is still running after the stop attempt. Use Force Stop, then Start.",
                ConsoleMessageType.Error);
            return;
        }

        // Wait for the old process to exit and its ports to free instead of a fixed delay.
        AppendLog("[HighPop] Waiting for the previous process and ports to be released...", ConsoleMessageType.System);
        if (!await Task.Run(() => _manager.WaitForServerReleasedAsync(Server, TimeSpan.FromSeconds(30))))
            AppendLog("[HighPop] Ports were still in use after 30 seconds; attempting to start anyway.",
                ConsoleMessageType.Warning);
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
        InstallProgress   = 0;
        InstallProgressText = "Preparing SteamCMD";
        Server.Status     = ServerStatus.Installing;
        RefreshStatus();
        AppendLog($"[HighPop] {Loc.InstallingText} {Plugin.GameName}...", ConsoleMessageType.System);

        // Auto-backup before update (only when there is something to back up)
        if (Server.BackupEnabled && hadExistingInstall)
        {
            try { await _backup.CreateBackupAsync(Server); AppendLog("[Backup] Auto-backup created before update.", ConsoleMessageType.System); _backupsVm?.Refresh(); }
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
        finally
        {
            IsInstalling = false;
            InstallProgressText = Server.Status == ServerStatus.Error ? "Update failed" : "Ready";
            RefreshStatus();
        }
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

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SendConsoleCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(ConsoleInput)) return;
        var cmd = ConsoleInput.Trim();
        ConsoleInput = string.Empty;
        // Chat mode: plain text goes to every player. A typed "say …" is left as it is.
        if (ConsoleChatMode && IsRust && !IsSayCommand(cmd))
            cmd = "say " + RustModerationCommands.CleanQuoted(cmd);
        await RunCommandTextAsync(cmd);
    }

    /// <summary>
    /// When on, the console box sends what is typed as in-game chat (<c>say</c>) instead of as a
    /// Rust console command. Typing a sentence in command mode made Rust look for a command named
    /// after the first word, which it silently ignores.
    /// </summary>
    [ObservableProperty] private bool _consoleChatMode;

    /// <summary>
    /// True when the first word of <paramref name="command"/> is not a Rust command or ConVar in the
    /// Rogue Rust Reference. Plugin commands are not in the reference, so callers phrase the result
    /// as a hint, not an error.
    /// </summary>
    private async Task<bool> IsUnknownRustCommandAsync(string command)
    {
        var text = command.TrimStart();
        var end = text.IndexOfAny([' ', '\t']);
        var name = (end < 0 ? text : text[..end]).ToLowerInvariant();
        if (name.Length == 0) return false;
        // Carbon, Oxide and Harmony commands are not part of the Rust reference.
        if (name.StartsWith("c.", StringComparison.Ordinal)
            || name.StartsWith("o.", StringComparison.Ordinal)
            || name.StartsWith("carbon.", StringComparison.Ordinal)
            || name.StartsWith("oxide.", StringComparison.Ordinal)
            || name.StartsWith("umod.", StringComparison.Ordinal)
            || name.StartsWith("harmony.", StringComparison.Ordinal))
            return false;
        try
        {
            var entries = _allRustReferenceEntries;
            if (entries.Count == 0) entries = (await _rustReference.LoadAsync()).Entries.ToList();
            if (entries.Count == 0) return false;
            return !entries.Any(e =>
                e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || e.Name.Equals("global." + name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunQuickCommandAsync(QuickCommand? qc)
    {
        if (qc == null || string.IsNullOrWhiteSpace(qc.Command)) return;
        await RunCommandTextAsync(qc.Command);
    }

    private async Task RunCommandTextAsync(string cmd)
    {
        AppendLog("> " + cmd, ConsoleMessageType.Input);
        AddActionLog($"Console command sent: {SummarizeCommand(cmd)}");

        await SendRconOrConsole(cmd);
    }

    [RelayCommand]
    private void ClearConsole()
    {
        while (_pendingConsoleMessages.TryDequeue(out _)) { }
        Log.Clear();
        FilteredLog.Clear();
    }

    // ── Rogue Rust Reference ────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshRustReferenceAsync()
    {
        if (RustReferenceLoading) return;
        RustReferenceLoading = true;
        RustReferenceStatus = "Downloading the current command and ConVar metadata…";
        try
        {
            // The first load for another server reuses the singleton's in-memory snapshot.
            // Once this workspace already has data, the same button performs an explicit refresh.
            var snapshot = await _rustReference.LoadAsync(refresh: _allRustReferenceEntries.Count > 0);
            _allRustReferenceEntries = snapshot.Entries.ToList();
            ApplyRustReferenceFilter();
            var updated = snapshot.UpdatedAt?.ToLocalTime().ToString("g") ?? "unknown time";
            RustReferenceStatus = snapshot.FromCache
                ? $"Offline cache loaded: {_allRustReferenceEntries.Count:N0} entries · source sync {updated}."
                : $"Loaded {_allRustReferenceEntries.Count:N0} entries · source sync {updated}.";
        }
        catch (Exception ex)
        {
            RustReferenceStatus = ex.Message;
        }
        finally { RustReferenceLoading = false; }
    }

    [RelayCommand]
    private void InsertRustReference(RustReferenceEntry? entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) return;
        ConsoleInput = entry.Name + " ";
        AddActionLog($"Rust Reference inserted: {entry.Name}");
    }

    [RelayCommand]
    private void OpenRustReference()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                RustReferenceService.ReferenceUrl) { UseShellExecute = true });
        }
        catch { }
    }

    partial void OnRustReferenceSearchChanged(string value) => ApplyRustReferenceFilter();
    partial void OnRustReferenceKindChanged(string value) => ApplyRustReferenceFilter();

    private void ApplyRustReferenceFilter()
    {
        IEnumerable<RustReferenceEntry> filtered = _allRustReferenceEntries;
        filtered = RustReferenceKind switch
        {
            "Commands" => filtered.Where(entry => entry.Kind == "Command"),
            "ConVars" => filtered.Where(entry => entry.Kind == "ConVar"),
            _ => filtered,
        };
        if (!string.IsNullOrWhiteSpace(RustReferenceSearch))
        {
            var search = RustReferenceSearch.Trim();
            filtered = filtered.Where(entry =>
                entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.Scope.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        RustReferenceEntries = filtered.Take(750).ToList();
    }

    [RelayCommand]
    private async Task ExportConsoleAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export server console",
            Filter = "Log file|*.log|Text file|*.txt",
            FileName = $"{SanitizeFileName(Server.DisplayName)}-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            DefaultExt = ".log",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = Log.Select(message =>
                $"[{message.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{message.Type}] [{message.SourceLabel}] {message.Text}");
            await File.WriteAllLinesAsync(dialog.FileName, lines);
            AddActionLog($"Console exported: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AppendLog($"[Console] Export failed: {ex.Message}", ConsoleMessageType.Error);
        }
    }

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

    partial void OnConsoleFilterChanged(string value) => RebuildFilteredLog();
    partial void OnShowConsoleInfoChanged(bool value) => RebuildFilteredLog();
    partial void OnShowConsoleWarningsChanged(bool value) => RebuildFilteredLog();
    partial void OnShowConsoleErrorsChanged(bool value) => RebuildFilteredLog();
    partial void OnIsConsolePausedChanged(bool value)
    {
        if (!value) RebuildFilteredLog();
    }

    // ── RCON ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ConnectRconAsync() => await ConnectRconCoreAsync(automatic: false);

    private async Task<bool> ConnectRconCoreAsync(
        bool automatic,
        CancellationToken cancellationToken = default)
    {
        await _rconLock.WaitAsync(cancellationToken);
        try
        {
            if (_rcon != null)
            {
                _rcon.ChatReceived -= OnRconChat;
                _rcon.ReportReceived -= OnRconReport;
                await _rcon.DisconnectAsync(cancellationToken);
            }
            var rcon = new RconService();
            rcon.ChatReceived += OnRconChat;
            rcon.ReportReceived += OnRconReport;
            _rcon = rcon;

            var ip = string.IsNullOrEmpty(Server.ServerIp) || Server.ServerIp is "0.0.0.0" or "::" or "[::]"
                ? "127.0.0.1"
                : Server.ServerIp;
            var port = Server.RconPort > 0 ? Server.RconPort : Server.ServerPort + 1;
            var ok = await rcon.ConnectAsync(ip, port, Server.RconPassword, cancellationToken);
            var problem = rcon.LastError;
            // A password Rust can never receive will not start working on a retry.
            var permanent = !ok && !RconService.CanSendPassword(Server.RconPassword, out _);

            UiDispatcher.Post(() =>
            {
                RconConnected = ok;
                RconStatusText = ok
                    ? $"Connected to {ip}:{port}"
                    : permanent ? "RCON password cannot be used over WebRCON"
                    : automatic ? $"Waiting for WebRCON on {ip}:{port}" : "Connection failed";
            });
            if (ok)
            {
                _manager.ReportRconConnected(Server.Id);
                if (!automatic) _rconAutoConnectCts?.Cancel();
                AppendLog("[RCON] " + (automatic ? "Auto-connected." : Loc.RconConnectedMsg),
                    ConsoleMessageType.System);
                AddActionLog(automatic ? "WebRCON auto-connected" : "WebRCON connected");
                _ = LoadRecentChatAsync(rcon);
            }
            else if (!automatic || permanent)
            {
                AppendLog("[RCON] " + (problem.Length > 0 ? problem : Loc.RconFailedMsg), ConsoleMessageType.Error);
                AddActionLog("WebRCON connection failed");
                if (permanent) _rconAutoConnectCts?.Cancel();
            }
            return ok;
        }
        finally { _rconLock.Release(); }
    }

    private void StartAutoRconConnect()
    {
        _rconAutoConnectCts?.Cancel();
        _rconAutoConnectCts?.Dispose();
        _rconAutoConnectCts = null;
        if (!Server.AutoConnectRcon || !HasRcon || !IsRunning) return;

        var cts = new CancellationTokenSource();
        _rconAutoConnectCts = cts;
        var token = cts.Token;
        var lifecycleGeneration = Server.LifecycleGeneration;
        UiDispatcher.Post(() =>
            RconStatusText = "Waiting for Rust WebRCON...");
        _ = Task.Run(async () =>
        {
            try
            {
                var timeout = TimeSpan.FromMinutes(
                    Math.Clamp(Server.RconAutoConnectTimeoutMinutes, 1, 60));
                var requestedDelay = TimeSpan.FromSeconds(
                    Math.Clamp(Server.RconAutoConnectDelaySeconds, 5, 600));
                var initialDelay = requestedDelay < timeout ? requestedDelay : timeout;
                var deadline = DateTime.UtcNow + timeout;

                UiDispatcher.Post(() =>
                    RconStatusText = $"Waiting {initialDelay.TotalSeconds:0}s before WebRCON...");
                await Task.Delay(initialDelay, token);

                var failedAttempt = 0;
                while (DateTime.UtcNow < deadline && IsRunning && !token.IsCancellationRequested)
                {
                    if (!ServerLifecycleRules.IsCurrent(Server, lifecycleGeneration)
                        || Server.DesiredState != ServerDesiredState.Running)
                        return;
                    if (RconConnected) return;
                    if (await ConnectRconCoreAsync(automatic: true, token))
                    {
                        await FetchOnlinePlayersAsync();
                        return;
                    }
                    failedAttempt++;
                    var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
                    var retryDelay = RconReconnectPolicy.GetDelay(failedAttempt, jitter);
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                    if (retryDelay > remaining) retryDelay = remaining;
                    UiDispatcher.Post(() =>
                        RconStatusText = $"WebRCON retry {failedAttempt + 1} in {retryDelay.TotalSeconds:0}s");
                    await Task.Delay(retryDelay, token);
                }
                if (!token.IsCancellationRequested)
                {
                    UiDispatcher.Post(() =>
                        RconStatusText = "Auto-connect timed out — use Connect to retry");
                    AppendLog(
                        $"[RCON] Auto-connect timed out after {timeout.TotalMinutes:0} minute(s).",
                        ConsoleMessageType.Warning);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppendLog($"[RCON] Auto-connect failed: {ex.Message}", ConsoleMessageType.Warning);
            }
        }, token);
    }

    [RelayCommand]
    private async Task DisconnectRconAsync()
    {
        _rconAutoConnectCts?.Cancel();
        await _rconLock.WaitAsync();
        try
        {
            if (_rcon != null)
                await _rcon.DisconnectAsync();
            _rcon = null;
        }
        finally { _rconLock.Release(); }

        RconConnected = false;
        RconStatusText = "Disconnected";
        AppendLog("[RCON] " + Loc.RconDisconnectedMsg, ConsoleMessageType.System);
        AddActionLog("WebRCON disconnected");
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
        OpenExistingExplorerFolder(Server.InstallPath, "server files");
    }

    [RelayCommand]
    private void BrowseLogDirectory()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the Rust server log directory",
            SelectedPath = EffectiveLogDirectory,
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        CustomLogDirectory = dialog.SelectedPath;
        AddActionLog($"Custom log directory set to {dialog.SelectedPath}");
    }

    [RelayCommand]
    private void ResetLogDirectory()
    {
        CustomLogDirectory = string.Empty;
        AddActionLog("Log directory reset to the Rust identity default");
    }

    [RelayCommand]
    private void OpenLogDirectory()
    {
        var directory = EffectiveLogDirectory;
        Directory.CreateDirectory(directory);
        OpenExistingExplorerFolder(directory, "Rust logs");
    }

    [RelayCommand]
    private void OpenServerConfigDirectory()
    {
        var directory = Path.GetDirectoryName(ServerConfigPath);
        if (!string.IsNullOrWhiteSpace(directory))
            OpenExistingExplorerFolder(directory, "Rust configuration");
    }

    [RelayCommand]
    private void OpenTelemetryDirectory()
    {
        try
        {
            var directory = _telemetry.GetServerDirectory(Server);
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", directory)
                {
                    UseShellExecute = true,
                });
        }
        catch (Exception ex)
        {
            AppendLog($"[Telemetry] Could not open event directory: {ex.Message}",
                ConsoleMessageType.Error);
        }
    }

    [RelayCommand]
    private void OpenServerConfig()
    {
        var path = ServerConfigPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
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

    private async Task RunPeriodicUpdateAsync(bool operatorRequested = false)
    {
        if (!await _periodicUpdateGate.WaitAsync(0)) return;
        try
        {
            if (!IsRunning) return; // server was stopped manually
            AppendLog("[AutoUpdate] Checking for updates...", ConsoleMessageType.System);

            bool wasAutoRestart = Server.AutoRestart;
            try
            {
                var branch = Server.GameSpecificSettings.TryGetValue("steamBranch", out var configuredBranch)
                    && !string.IsNullOrWhiteSpace(configuredBranch)
                        ? configuredBranch
                        : Plugin?.SteamBranch;
                var installedBuild = Plugin?.GetSteamInstalledBuildId(Server);
                var latestBuild = await _steamCmd.GetLatestBuildIdAsync(
                    Server.Id,
                    Plugin?.SteamAppId ?? 0,
                    branch);

                if (string.IsNullOrWhiteSpace(installedBuild) || string.IsNullOrWhiteSpace(latestBuild))
                {
                    AppendLog(
                        "[AutoUpdate] Could not verify both installed and current Rust build IDs. The running server was left untouched.",
                        ConsoleMessageType.Warning);
                    return;
                }
                if (string.Equals(installedBuild, latestBuild, StringComparison.Ordinal))
                {
                    AppendLog($"[AutoUpdate] Rust build {installedBuild} is current; no restart needed.",
                        ConsoleMessageType.System);
                    return;
                }

                AppendLog($"[AutoUpdate] Rust update found: {installedBuild} → {latestBuild}.",
                    ConsoleMessageType.Warning);
                var countdownGeneration = Server.LifecycleGeneration;
                if (!await RunUpdateCountdownAsync(operatorRequested))
                {
                    Server.AutoRestart = wasAutoRestart;
                    AppendLog("[AutoUpdate] Automatic update restart cancelled.",
                        ConsoleMessageType.System);
                    return;
                }
                if (!ServerLifecycleRules.IsCurrent(Server, countdownGeneration)
                    || Server.DesiredState != ServerDesiredState.Running)
                {
                    Server.AutoRestart = wasAutoRestart;
                    AppendLog(
                        "[AutoUpdate] Update cancelled because a newer lifecycle request superseded the countdown.",
                        ConsoleMessageType.Warning);
                    return;
                }
                // Temporarily disable AutoRestart only for the actual stop → update → start
                // cycle, not during the one-minute player warning.
                Server.AutoRestart = false;
                await _manager.StopAsync(Server, "Automatic SteamCMD update cycle",
                    LifecycleInitiator.UpdateWorkflow);
                var stoppedGeneration = Server.LifecycleGeneration;
                // StopPerfMonitoring called by OnStatusChanged

                await InstallAsync(); // runs SteamCMD update
                var installedAfterUpdate = Plugin?.GetSteamInstalledBuildId(Server);
                var updateConfirmed = string.Equals(
                    installedAfterUpdate,
                    latestBuild,
                    StringComparison.Ordinal);
                Server.AutoRestart = wasAutoRestart;

                // Even if SteamCMD failed, restart the existing installation so a failed
                // maintenance attempt does not leave a previously live server offline.
                if (!ServerLifecycleRules.IsCurrent(Server, stoppedGeneration)
                    || Server.DesiredState != ServerDesiredState.Stopped)
                {
                    AppendLog(
                        "[AutoUpdate] Restart cancelled because a newer lifecycle request superseded the update cycle.",
                        ConsoleMessageType.Warning);
                    return;
                }
                await _manager.StartAsync(Server, LifecycleInitiator.UpdateWorkflow,
                    "Restart after SteamCMD update cycle");
                // StartPerfMonitoring + StartUpdateTimer called by OnStatusChanged(Running)
                if (updateConfirmed)
                {
                    AppendLog("[AutoUpdate] ✅ Updated and restarted.", ConsoleMessageType.System);
                    await _notifications.NotifyAsync(
                        $"🔄 {Server.DisplayName} updated & restarted",
                        Plugin?.GameName ?? "",
                        "#22D3EE");
                }
                else
                {
                    AppendLog(
                        "[AutoUpdate] The target build was not confirmed. The previous Rust installation was restarted.",
                        ConsoleMessageType.Warning);
                }
            }
            catch (Exception ex)
            {
                Server.AutoRestart = wasAutoRestart;
                AppendLog($"[AutoUpdate] ❌ {ex.Message}", ConsoleMessageType.Error);
            }
        }
        finally { _periodicUpdateGate.Release(); }
    }

    private async Task<bool> RunUpdateCountdownAsync(bool operatorRequested)
    {
        var checkpoints = ServerMaintenancePolicy.GetUpdateCountdownSeconds(
            Server.AutoUpdateWarningMinutes);
        for (var index = 0; index < checkpoints.Count; index++)
        {
            if (!IsRunning || (!operatorRequested && !Server.AutoUpdate)) return false;

            var remaining = checkpoints[index];
            var label = ServerMaintenancePolicy.FormatCountdown(remaining);
            await _manager.WarnPlayersAsync(
                Server,
                $"Rust server update scheduled in {label}. The world will be saved before restart.");

            var next = index + 1 < checkpoints.Count ? checkpoints[index + 1] : 0;
            if (!await DelayUpdateCountdownAsync(remaining - next, operatorRequested)) return false;
        }
        return IsRunning && (operatorRequested || Server.AutoUpdate);
    }

    private async Task<bool> DelayUpdateCountdownAsync(int seconds, bool operatorRequested)
    {
        var remaining = seconds;
        while (remaining > 0)
        {
            var slice = Math.Min(30, remaining);
            await Task.Delay(TimeSpan.FromSeconds(slice));
            remaining -= slice;
            if (!IsRunning || (!operatorRequested && !Server.AutoUpdate)) return false;
        }
        return true;
    }

    // ── Custom join domain ───────────────────────────────────────────────────

    public const string CustomDomainGuideUrl =
        "https://github.com/RogueAssassin/HighPop-Rust-Manager/blob/main/docs/custom-domains.md";
    private readonly CustomDomainService _customDomains = new();
    [ObservableProperty] private string _domainCheckHostname = string.Empty;
    [ObservableProperty] private bool _domainChecking;
    [ObservableProperty] private string _domainCheckSummary =
        "Set Public join domain in Settings, or type a hostname here, then select Check DNS.";
    [ObservableProperty] private List<DomainCheckItem> _domainCheckItems = [];
    [ObservableProperty] private string _domainConnectCommand = string.Empty;

    private string ConfiguredPublicHostname =>
        Server.GameSpecificSettings.TryGetValue("publicHostname", out var value) ? value : string.Empty;

    [RelayCommand]
    private async Task CheckCustomDomainAsync()
    {
        if (DomainChecking) return;
        var hostname = string.IsNullOrWhiteSpace(DomainCheckHostname) ? ConfiguredPublicHostname : DomainCheckHostname;
        if (string.IsNullOrWhiteSpace(hostname))
        {
            DomainCheckSummary = "Enter a hostname such as rust.example.com, or set Public join domain in Settings.";
            return;
        }

        DomainChecking = true;
        DomainCheckSummary = $"Checking {CustomDomainService.NormalizeHostname(hostname)} against public DNS…";
        try
        {
            var report = await _customDomains.CheckAsync(hostname, Server.ServerPort);
            DomainCheckHostname = report.Hostname;
            DomainCheckItems = report.Items.ToList();
            DomainConnectCommand = report.ConnectCommand;
            DomainCheckSummary = report.Summary;
            AddActionLog($"Custom domain checked: {report.Hostname} ({(report.HasErrors ? "errors" : "ok")})");
        }
        catch (Exception ex)
        {
            DomainCheckSummary = $"DNS check failed: {ex.Message}";
        }
        finally { DomainChecking = false; }
    }

    [RelayCommand]
    private void CopyDomainConnectCommand()
    {
        if (string.IsNullOrWhiteSpace(DomainConnectCommand)) return;
        try { System.Windows.Clipboard.SetText(DomainConnectCommand); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    [RelayCommand]
    private void OpenCustomDomainGuide() => OpenTrustedUrl(CustomDomainGuideUrl, "github.com");

    internal static void OpenTrustedUrl(string? url, string host)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !RogueGamingEndpoints.IsHttpsOnHost(uri, host))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [RelayCommand]
    internal void RefreshInstalledPlugins()
    {
        IsOxideInstalled = ModManagerService.GetInstalledOxideVersion(Server.InstallPath) != null;
        IsCarbonInstalled = ModManagerService.IsCarbonInstalled(Server.InstallPath);
        DetectedModFramework = ModManagerService.GetDetectedFramework(Server.InstallPath);
        InstalledPlugins = ModManagerService.GetInstalledPlugins(Server.InstallPath);
        _modsVm?.Refresh();

        if (!DetectedModFramework.StartsWith("Vanilla", StringComparison.OrdinalIgnoreCase))
        {
            if (Server.RustServerProfile == "Community (Vanilla)")
                RustServerProfile = "Modded";
        }
        OnPropertyChanged(nameof(HasSingleModFramework));
        OnPropertyChanged(nameof(EffectiveLogDirectory));
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
        UiDispatcher.Post(() =>
        {
            PlayerHistory = _playerStats.GetSessions(Server.Id, 50);
            PlayerStatsList = _playerStats.GetPlayerStats(Server.Id, 50);
            RefreshActivityStats();
        });
    }

    internal async Task FetchOnlinePlayersAsync()
    {
        if (Plugin == null) return;
        if (!await _playerRefreshGate.WaitAsync(0)) return;

        try
        {
            var parsed = new List<Models.OnlinePlayer>();
            var querySource = string.Empty;
            var cmd = Plugin.GetPlayersCommand();

            var rcon = _rcon;
            if (cmd != null && RconConnected && rcon != null)
            {
                string response = string.Empty;
                try { response = await rcon.SendCommandAsync(cmd); }
                catch
                {
                    UiDispatcher.Post(() =>
                    {
                        RconConnected = false;
                        RconStatusText = "Connection lost — reconnecting";
                    });
                    if (Server.AutoConnectRcon) StartAutoRconConnect();
                }

                if (Services.PlayerParserService.TryParseRustPlayerList(response, out var rconPlayers))
                {
                    parsed = rconPlayers;
                    querySource = "WebRCON";
                    try { await RefreshServerInfoAsync(rcon); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { }
                }
                else if (!rcon.IsConnected)
                {
                    UiDispatcher.Post(() =>
                    {
                        RconConnected = false;
                        RconStatusText = "Connection lost — reconnecting";
                    });
                    if (Server.AutoConnectRcon) StartAutoRconConnect();
                }
            }

            if (querySource.Length == 0 && Server.QueryPort > 0)
            {
                var queryHost = Server.ServerIp is "" or "0.0.0.0" or "::" or "[::]"
                    ? "127.0.0.1"
                    : Server.ServerIp;
                parsed = await A2SQueryService.QueryPlayersAsync(queryHost, Server.QueryPort);
                if (parsed.Count > 0) querySource = "Steam query fallback";
            }

            if (querySource.Length == 0)
            {
                UiDispatcher.Post(() =>
                    PlayerQueryStatus = RconConnected
                        ? "The server returned an unreadable player list; retrying automatically."
                        : "Player query unavailable — waiting for WebRCON or the Steam query port.");
                return;
            }

            UpdateOnlinePlayers(parsed, querySource);
        }
        finally
        {
            _playerRefreshGate.Release();
        }
    }

    private void UpdateOnlinePlayers(List<Models.OnlinePlayer> parsed, string querySource)
    {
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
        Server.LastPlayerSampleAt = DateTime.Now;
        _manager.ReportPlayerSample(Server.Id);
        if (_lastTelemetryPlayerCount != parsed.Count)
        {
            _lastTelemetryPlayerCount = parsed.Count;
            _ = _telemetry.AppendAsync(
                Server,
                "players.count_changed",
                querySource.Equals("WebRCON", StringComparison.OrdinalIgnoreCase)
                    ? "webrcon"
                    : "steam-query",
                new Dictionary<string, string>
                {
                    ["players"] = parsed.Count.ToString(),
                    ["capacity"] = Server.MaxPlayers.ToString(),
                });
        }

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

        UiDispatcher.Post(() =>
        {
            OnlinePlayers = parsed;
            PlayerQueryStatus = $"{querySource} • refreshed {DateTime.Now:HH:mm:ss}";
            PlayerHistory = _playerStats.GetSessions(Server.Id, 50);
            PlayerStatsList = _playerStats.GetPlayerStats(Server.Id, 50);
            RefreshModerationProfiles();
            RefreshActivityStats();
        });
    }

    internal void RefreshModerationProfiles() =>
        ModerationProfiles = _moderation.GetRecords(Server.Id);

    // ── Live server info (WebRCON serverinfo) ────────────────────────────────

    [ObservableProperty] private RustServerInfo? _serverInfo;
    private async Task RefreshServerInfoAsync(RconService rcon)
    {
        var reply = await rcon.SendCommandAsync("serverinfo", TimeSpan.FromSeconds(5));
        if (RustServerInfo.TryParse(reply, out var info))
            UiDispatcher.Post(() => ServerInfo = info);
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

    // ── Group bans ───────────────────────────────────────────────────────────

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

    internal async Task SendRconOrConsole(string cmd)
    {
        // RconService is safe for concurrent callers and correlates replies by identifier, so the
        // connection lock is deliberately NOT held here. Holding it across the reply wait
        // serialised every command (and the player refresh) behind slow or reply-less commands.
        var rcon = await EnsureRconForCommandAsync();
        if (rcon != null)
        {
            try
            {
                if (IsSayCommand(cmd))
                {
                    await SendSayAsync(rcon, cmd);
                    if (rcon.IsConnected) return;
                    throw new IOException("the connection closed while sending");
                }
                // Rust answers an unknown command with nothing at all, which looked like a timeout.
                // Warn straight away; the line is still sent in case it is a plugin command.
                var unknown = await IsUnknownRustCommandAsync(cmd);
                if (unknown)
                {
                    var word = cmd.TrimStart().Split([' ', '\t'], 2)[0];
                    AppendLog(
                        $"[RCON] '{word}' is not a Rust console command, so Rust will ignore this line unless a plugin handles it. " +
                        $"To message players, type say {cmd.Trim()} or switch the input to 💬 Chat.",
                        ConsoleMessageType.Warning);
                }
                var r = await rcon.SendCommandAsync(cmd);
                if (unknown && r.StartsWith("[RCON] No response", StringComparison.Ordinal) && rcon.IsConnected)
                    return;
                if (!string.IsNullOrEmpty(r))
                    AppendLog(r, r.StartsWith("[RCON]", StringComparison.Ordinal)
                        ? ConsoleMessageType.Warning
                        : ConsoleMessageType.Info);
                if (rcon.IsConnected) return;
            }
            catch (Exception ex)
            {
                AppendLog($"[RCON] Connection lost: {ex.Message}", ConsoleMessageType.Warning);
            }

            UiDispatcher.Post(() =>
            {
                RconConnected = false;
                RconStatusText = "Connection lost — using process console";
            });
            if (Server.AutoConnectRcon) StartAutoRconConnect();
        }
        await _manager.SendCommandAsync(Server.Id, cmd);
        if (IsSayCommand(cmd))
            UiDispatcher.Post(() => BroadcastStatus =
                "WebRCON is not connected, so delivery cannot be confirmed. Check the console for the reason.");
    }

    private DateTime _lastOnDemandRconAttemptUtc = DateTime.MinValue;

    /// <summary>
    /// Returns a connected WebRCON client, connecting on demand when the server is running but the
    /// console is not connected yet (auto-connect off, still waiting, or the connection dropped).
    /// Rust ignores process input, so without this a typed <c>say</c> was silently lost.
    /// </summary>
    internal async Task<RconService?> EnsureRconForCommandAsync()
    {
        var rcon = _rcon;
        if (RconConnected && rcon?.IsConnected == true) return rcon;
        if (!IsRust || !HasRcon || !IsRunning) return null;
        // One attempt every 10 seconds at most, so a burst of commands to a server that is still
        // starting does not queue a connection attempt each.
        if (DateTime.UtcNow - _lastOnDemandRconAttemptUtc < TimeSpan.FromSeconds(10)) return null;
        _lastOnDemandRconAttemptUtc = DateTime.UtcNow;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (!await ConnectRconCoreAsync(automatic: true, timeout.Token)) return null;
        }
        catch (OperationCanceledException) { return null; }
        rcon = _rcon;
        return rcon?.IsConnected == true ? rcon : null;
    }

    private static bool IsSayCommand(string command)
    {
        var text = command.TrimStart();
        var end = text.IndexOfAny([' ', '\t']);
        var name = end < 0 ? text : text[..end];
        return name.Equals("say", StringComparison.OrdinalIgnoreCase)
            || name.Equals("global.say", StringComparison.OrdinalIgnoreCase)
            || name.Equals("chat.say", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendSayAsync(RconService rcon, string command)
    {
        if (RconService.SayText(command).Length == 0)
        {
            AppendLog("[Chat] Type a message after say, for example: say Server restart in 10 minutes",
                ConsoleMessageType.Warning);
            return;
        }
        var result = await rcon.SayAsync(command, TimeSpan.FromSeconds(4));
        if (!result.Sent)
            AppendLog($"[Chat] Not sent: {result.Reply}", ConsoleMessageType.Warning);
        else if (result.Delivered)
            AppendLog("[Chat] Delivered in game.", ConsoleMessageType.System);
        else
            AppendLog("[Chat] Rust accepted the message but did not broadcast it within 4 s. " +
                      "A Carbon/Oxide plugin may be blocking server messages (OnServerMessage), or chat is disabled.",
                ConsoleMessageType.Warning);
        UiDispatcher.Post(() => BroadcastStatus = !result.Sent
            ? "Not sent: " + result.Reply
            : result.Delivered ? $"Delivered in game at {DateTime.Now:HH:mm:ss}."
            : "Sent, but Rust did not broadcast it. Check chat plugins.");
    }

    // ── In-game chat and F7 reports (WebRCON broadcasts) ─────────────────────

    private const int MaxChatLines = 300;
    public ObservableCollection<RconChatMessage> ChatLog { get; } = [];
    public ObservableCollection<PlayerReport> PlayerReports { get; } = [];
    [ObservableProperty] private string _broadcastMessage = string.Empty;
    [ObservableProperty] private string _broadcastStatus = string.Empty;

    private void OnRconChat(RconChatMessage chat) => UiDispatcher.Post(() => AddChat(chat));

    private void AddChat(RconChatMessage chat)
    {
        // chat.tail on reconnect can repeat lines already shown.
        if (ChatLog.Any(c => c.Time == chat.Time && c.UserId == chat.UserId && c.Message == chat.Message)) return;
        ChatLog.Add(chat);
        while (ChatLog.Count > MaxChatLines) ChatLog.RemoveAt(0);
    }

    private void OnRconReport(string json)
    {
        var report = PlayerReport.Parse(json);
        UiDispatcher.Post(() =>
        {
            PlayerReports.Insert(0, report);
            while (PlayerReports.Count > 100) PlayerReports.RemoveAt(PlayerReports.Count - 1);
        });
        AppendLog($"[Report] {report.Summary}", ConsoleMessageType.Warning);
    }

    private async Task LoadRecentChatAsync(RconService rcon)
    {
        try
        {
            var tail = await rcon.SendCommandAsync("chat.tail 50");
            var lines = RconChatMessage.ParseTail(tail);
            UiDispatcher.Post(() => { foreach (var line in lines) AddChat(line); });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SendBroadcastAsync()
    {
        var text = RustModerationCommands.CleanQuoted(BroadcastMessage);
        if (text.Length == 0) return;
        BroadcastMessage = string.Empty;
        BroadcastStatus = "Sending…";
        AddActionLog($"Chat broadcast: {SummarizeCommand(text)}");
        await RunCommandTextAsync("say " + text);
    }

    internal static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ── Scheduled tasks ──────────────────────────────────────────────────────

    /// <summary>Refreshes the Schedule page if it has been opened for this server.</summary>
    public void RefreshScheduledTasks() => _automationVm?.RefreshScheduledTasks();

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
                _monitoringVm?.OnSampleRecorded();
                WpfApplication.Current?.Dispatcher?.BeginInvoke(() =>
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
        UiDispatcher.Post(() =>
        {
            CpuPercent = 0;
            MemoryMb = 0;
        });
        _monitoringVm?.UpdatePerfChart();
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
        UiDispatcher.Post(RefreshStatus);
        UiDispatcher.Post(() => OnPropertyChanged(nameof(LastExitSummary)));
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
            UiDispatcher.Post(() =>
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
            UiDispatcher.Post(() =>
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
        UiDispatcher.Post(() =>
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

        UiDispatcher.Post(() =>
        {
            RconConnected = false;
            RconStatusText = "Disconnected";
        });
    }

    private void OnPortsReassigned(Models.GameServer srv)
    {
        if (srv.Id != Server.Id) return;
        UiDispatcher.Post(() =>
        {
            OnPropertyChanged(nameof(Server));
        });
    }

    private void OnCrashLimitReached(string serverId)
    {
        if (serverId != Server.Id) return;
        UiDispatcher.Post(StopPerfMonitoring);
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

    private void OnSteamProgressDetail(string serverId, int p, string detail)
    {
        if (serverId.Length > 0 && serverId != Server.Id) return;
        WpfApplication.Current?.Dispatcher?.BeginInvoke(() =>
        {
            if (InstallProgress != p) InstallProgress = p;
            InstallProgressText = detail;
        });
    }

    internal void AppendLog(
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

        // Both lists are in arrival order, so the oldest line is always at index 0 of FilteredLog
        // if it is there at all. Checking that avoids a full search of the list for every new line.
        while (Log.Count > MaxConsoleLines)
        {
            var removed = Log[0];
            Log.RemoveAt(0);
            if (FilteredLog.Count > 0 && ReferenceEquals(FilteredLog[0], removed)) FilteredLog.RemoveAt(0);
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

    internal void AddActionLog(string action)
    {
        var safeAction = UiTextSanitizer.Normalize(action, 2_048);
        var entry = $"[{DateTime.Now:dd.MM HH:mm:ss}] {safeAction}";
        WpfApplication.Current?.Dispatcher?.BeginInvoke(() =>
        {
            ActionLog.Insert(0, entry);
            if (ActionLog.Count > 100) ActionLog.RemoveAt(ActionLog.Count - 1);
        });
        _ = _telemetry.AppendAsync(
            Server,
            "operator.action",
            "manager",
            new Dictionary<string, string> { ["summary"] = safeAction });
    }

    public void ResetUiTextAfterRenderFailure()
    {
        while (_pendingConsoleMessages.TryDequeue(out _)) { }
        Log.Clear();
        FilteredLog.Clear();
        ActionLog.Clear();
    }

    public void ResetRustReferenceAfterBindingFailure()
    {
        _allRustReferenceEntries.Clear();
        RustReferenceEntries = [];
        SelectedRustReferenceEntry = null;
        RustReferenceStatus = "Reference display was reset after a recoverable WPF binding error. Refresh to try again.";
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
        _playersVm?.NotifyRunningChanged();
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(HasTrackedProcess));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanForceStop));
        OnPropertyChanged(nameof(CanInstallServerFiles));
        _modsVm?.RaiseCanInstallChanged();
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(UptimeText));
    }

    private void OpenExistingExplorerFolder(string path, string label)
    {
        try
        {
            if (!ModManagerService.OpenExistingFolder(path))
            {
                Mods.ModStatusText = $"The {label} folder does not exist yet.";
                AppendLog($"[Files] The {label} folder does not exist yet.", ConsoleMessageType.Warning);
                return;
            }
            AddActionLog($"Opened {label} in Windows Explorer");
        }
        catch (Exception ex)
        {
            Mods.ModStatusText = $"Could not open {label}: {ex.Message}";
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
        _steamCmd.ProgressDetailChanged -= OnSteamProgressDetail;
        _network.ServerStatsUpdated -= OnServerStatsUpdated; // #1: estää muistivuodon poistetuille palvelimille

        StopUpdateTimer();
        StopPerfMonitoring();
        StopPlayerRefresh();
        _rconAutoConnectCts?.Cancel();
        _rconAutoConnectCts?.Dispose();
        _rconAutoConnectCts = null;
        // Dispose runs on the UI thread. RconService.Dispose aborts the socket without
        // waiting, so take the lock only if it's free right now instead of blocking for 3 s.
        var lockTaken = _rconLock.Wait(0);
        try
        {
            try { _rcon?.Dispose(); } catch { }
            _rcon = null;
        }
        finally { if (lockTaken) _rconLock.Release(); }

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
