using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using HighPop.Games;
using HighPop.Models;

namespace HighPop.Services;

public class ServerInstance
{
    public GameServer Server { get; }
    public Process? Process { get; set; }
    public DateTime? StartTime { get; set; }
    public ObservableCollection<ConsoleMessage> Log { get; } = [];
    // Lock protecting Log for concurrent read (GetLog HTTP handler) vs write (process output threads)
    public readonly object LogLock = new();
    public int RestartCount { get; set; }
    public CancellationTokenSource DailyRestartCts { get; } = new();
    public CancellationTokenSource? RustLogTailCts { get; set; }
    public nint JobHandle { get; set; } = nint.Zero;
    public volatile bool IntentionalStop;
    public string StopReason { get; set; } = string.Empty;
    private int _ready;
    private int _rustLogActive;
    public bool IsReady => Volatile.Read(ref _ready) == 1;
    public bool IsRustLogActive => Volatile.Read(ref _rustLogActive) == 1;
    public DateTime? ReadyTime { get; private set; }
    public string ReadySource { get; private set; } = string.Empty;
    public DateTime? ProcessObservedUtc { get; private set; }
    public DateTime? RustReadyUtc { get; private set; }
    public DateTime? RconReadyUtc { get; private set; }
    public DateTime? LastPlayerSampleUtc { get; private set; }
    private readonly object _consoleDedupeLock = new();
    private readonly Dictionary<string, (string Transport, long Timestamp)> _recentConsoleLines =
        new(StringComparer.Ordinal);

    /// <summary>Times of recent crashes (last 10 minutes). Used for crash-loop detection.</summary>
    public List<DateTime> CrashTimes { get; } = [];

    public ServerInstance(GameServer server) => Server = server;

    public bool TryMarkReady(string source)
    {
        var now = DateTime.UtcNow;
        if (source.Contains("WebRCON", StringComparison.OrdinalIgnoreCase)) RconReadyUtc = now;
        else if (source.Contains("Rust", StringComparison.OrdinalIgnoreCase)) RustReadyUtc = now;
        if (Interlocked.CompareExchange(ref _ready, 1, 0) != 0) return false;
        ReadyTime = now.ToLocalTime();
        ReadySource = source;
        return true;
    }

    public void MarkProcessObserved() => ProcessObservedUtc = DateTime.UtcNow;
    public void MarkPlayerSample() => LastPlayerSampleUtc = DateTime.UtcNow;
    public void MarkRustLogActive() => Interlocked.Exchange(ref _rustLogActive, 1);

    /// <summary>
    /// Suppresses a line only when a different Rust output transport mirrors it within two
    /// seconds. Legitimate repeated messages from the same transport remain visible.
    /// </summary>
    public bool TryAcceptConsoleLine(string text, string transport)
    {
        var now = Stopwatch.GetTimestamp();
        var threshold = Stopwatch.Frequency * 2;
        lock (_consoleDedupeLock)
        {
            if (_recentConsoleLines.TryGetValue(text, out var previous)
                && !string.Equals(previous.Transport, transport, StringComparison.Ordinal)
                && now - previous.Timestamp < threshold)
                return false;

            _recentConsoleLines[text] = (transport, now);
            if (_recentConsoleLines.Count > 2000) _recentConsoleLines.Clear();
            return true;
        }
    }

    public TimeSpan Uptime => StartTime.HasValue ? DateTime.Now - StartTime.Value : TimeSpan.Zero;

    private const int MaxLogLines = 500;
    public void AddToLog(ConsoleMessage msg)
    {
        lock (LogLock)
        {
            Log.Add(msg);
            while (Log.Count > MaxLogLines) Log.RemoveAt(0);
        }
    }
    public List<ConsoleMessage> GetLogSnapshot() { lock (LogLock) return Log.ToList(); }

    public void CancelAuxiliaryMonitoring()
    {
        try { RustLogTailCts?.Cancel(); } catch { }
        RustLogTailCts?.Dispose();
        RustLogTailCts = null;
    }
}

public sealed record ServerHealthSnapshot(
    string ServerId,
    string DisplayName,
    bool ProcessRunning,
    DateTime? ProcessObservedUtc,
    DateTime? RustReadyUtc,
    DateTime? RconReadyUtc,
    DateTime? LastPlayerSampleUtc,
    int CurrentPlayers,
    ServerDesiredState DesiredState,
    ServerLifecyclePhase LifecyclePhase,
    string LifecycleReason);

public class ServerManagerService
{
    private readonly NetworkMonitorService _network;
    private readonly ConfigService _config;
    private readonly RustTelemetryService _telemetry;
    private readonly ServerLifecycleCoordinator _lifecycle;
    private readonly ConcurrentDictionary<string, ServerInstance> _running = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lifecycleGates = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _crashHistory = new();
    private readonly ConcurrentDictionary<string, long> _recoveryGenerations = new();

    public event Action<string, ConsoleMessage>? LogReceived;
    public event Action<string, ServerStatus>?  StatusChanged;
    public event Action<ServerLifecycleTransition>? LifecycleChanged;
    /// <summary>Fired when a server has crashed too many times and auto-restart gives up.</summary>
    public event Action<string>? CrashLimitReached;
    /// <summary>Fired when a server's ports were automatically reassigned because they were in use.</summary>
    public event Action<GameServer>? PortsReassigned;

    public ServerManagerService(
        ConfigService config,
        NetworkMonitorService network,
        RustTelemetryService telemetry,
        ServerLifecycleCoordinator lifecycle)
    {
        _config  = config;
        _network = network;
        _telemetry = telemetry;
        _lifecycle = lifecycle;
        _lifecycle.Transitioned += transition => LifecycleChanged?.Invoke(transition);
    }

    public ServerInstance? GetInstance(string serverId)
        => _running.TryGetValue(serverId, out var i) ? i : null;

    /// <summary>
    /// True after Rust reports startup completion, WebRCON connects, or the configured
    /// slow-start grace window expires. Automated health/idle actions must not run earlier.
    /// </summary>
    public bool IsServerReady(string serverId)
    {
        if (!_running.TryGetValue(serverId, out var inst) || inst.Process?.HasExited != false)
            return false;
        if (inst.IsReady) return true;

        var graceMinutes = Math.Clamp(inst.Server.StartupGraceMinutes, 1, 60);
        if (inst.Uptime < TimeSpan.FromMinutes(graceMinutes)) return false;

        MarkServerReady(inst.Server, inst, $"startup grace elapsed ({graceMinutes} min)");
        return true;
    }

    public void ReportRconConnected(string serverId)
    {
        if (_running.TryGetValue(serverId, out var inst))
            MarkServerReady(inst.Server, inst, "WebRCON connected");
    }

    public void ReportPlayerSample(string serverId)
    {
        if (_running.TryGetValue(serverId, out var inst)) inst.MarkPlayerSample();
    }

    public IReadOnlyList<ServerHealthSnapshot> GetHealthSnapshots() => _running.Values
        .OrderBy(instance => instance.Server.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Select(instance => new ServerHealthSnapshot(
            instance.Server.Id,
            instance.Server.DisplayName,
            instance.Process?.HasExited == false,
            instance.ProcessObservedUtc,
            instance.RustReadyUtc,
            instance.RconReadyUtc,
            instance.LastPlayerSampleUtc,
            instance.Server.CurrentPlayers,
            instance.Server.DesiredState,
            instance.Server.LifecyclePhase,
            instance.Server.LastLifecycleReason))
        .ToList();

    public int RunningCount => _running.Count;

    public GameServer? GetServer(string serverId)
        => _running.TryGetValue(serverId, out var i) ? i.Server : null;

    /// <summary>Other currently-running servers in the same group and of the same game, used for ban-list sync.</summary>
    public IEnumerable<GameServer> GetRunningGroupSiblings(GameServer server)
        => _running.Values
            .Select(i => i.Server)
            .Where(s => s.Id != server.Id
                     && s.GameId == server.GameId
                     && !string.IsNullOrEmpty(server.GroupId)
                     && s.GroupId == server.GroupId);

    /// <summary>
    /// Called once at HighPop startup for every saved server. If the server has a persisted
    /// RunningPid and a matching process is still alive (HighPop was closed while the game
    /// server kept running), reattach to it so Status/Stop/Kill work again. Otherwise
    /// clears the stale PID.
    /// </summary>
    public bool TryReattach(GameServer server)
    {
        if (server.RunningPid <= 0)
        {
            _lifecycle.InitializeAfterLoad(server, reattached: false);
            return false;
        }
        try
        {
            var p = Process.GetProcessById(server.RunningPid);
            if (p.HasExited)
            {
                ClearRunningIdentity(server);
                _lifecycle.InitializeAfterLoad(server, reattached: false);
                return false;
            }

            var plugin = GameRegistry.Get(server.GameId);
            var exeName = plugin != null ? Path.GetFileNameWithoutExtension(plugin.Executable) : null;
            if (!string.IsNullOrEmpty(exeName) && !string.Equals(p.ProcessName, exeName, StringComparison.OrdinalIgnoreCase))
            {
                // PID was recycled by an unrelated process — not actually our server.
                ClearRunningIdentity(server);
                _lifecycle.InitializeAfterLoad(server, reattached: false);
                return false;
            }

            if (server.RunningProcessStartedUtc is { } expectedStart
                && Math.Abs((p.StartTime.ToUniversalTime() - expectedStart).TotalSeconds) > 2)
            {
                ClearRunningIdentity(server);
                _lifecycle.InitializeAfterLoad(server, reattached: false);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(server.RunningExecutablePath))
            {
                var actualPath = p.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(actualPath)
                    || !string.Equals(Path.GetFullPath(actualPath),
                        Path.GetFullPath(server.RunningExecutablePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    ClearRunningIdentity(server);
                    _lifecycle.InitializeAfterLoad(server, reattached: false);
                    return false;
                }
            }

            var inst = new ServerInstance(server) { Process = p, StartTime = SafeStartTime(p) };
            inst.MarkProcessObserved();
            inst.TryMarkReady("reattached running process");
            try { p.EnableRaisingEvents = true; } catch { }
            p.Exited += async (_, _) =>
            {
                if (!IsCurrentInstance(server.Id, inst)) return;
                _network.UnregisterServer(server.Id);
                RecordProcessExit(server, inst, SafeExitCode(p), inst.IntentionalStop);
                ClearRunningIdentity(server);
                RemoveInstanceIfCurrent(server.Id, inst);
                if (inst.IntentionalStop)
                {
                    SetStatus(server, ServerStatus.Stopped);
                    _lifecycle.MarkPhase(server, ServerLifecyclePhase.StoppedByOperator,
                        server.LastLifecycleInitiator, inst.StopReason,
                        server.LastLifecycleOperationId);
                    return;
                }

                SetStatus(server, ServerStatus.Error);
                if (!ServerLifecycleRules.CanRecover(server))
                {
                    _lifecycle.MarkPhase(server, ServerLifecyclePhase.Faulted,
                        LifecycleInitiator.AlwaysOn, "Reattached Rust process exited unexpectedly");
                    return;
                }

                var recovery = _lifecycle.BeginRecovery(server,
                    "Reattached Rust process exited unexpectedly");
                var generation = _recoveryGenerations.AddOrUpdate(server.Id, 1,
                    (_, current) => current + 1);
                await RecoverUnexpectedExitAsync(server, generation,
                    recovery, server.AutoRestartDelaySec);
            };
            _running[server.Id] = inst;
            inst.RustLogTailCts = new CancellationTokenSource();
            _ = TailRustLogAsync(server, inst, includeRecentHistory: true,
                cancellationToken: inst.RustLogTailCts.Token);
            RememberRunningIdentity(server, p);
            _network.RegisterServer(server.Id, p.Id);
            SetStatus(server, ServerStatus.Running);
            _lifecycle.InitializeAfterLoad(server, reattached: true);
            var msg = new ConsoleMessage { Text = $"[HighPop] Reattached to running process (PID {p.Id}) after HighPop restart.", Type = ConsoleMessageType.Info };
            inst.AddToLog(msg);
            LogReceived?.Invoke(server.Id, msg);
            return true;
        }
        catch
        {
            ClearRunningIdentity(server);
            _lifecycle.InitializeAfterLoad(server, reattached: false);
            return false;
        }
    }

    private static DateTime? SafeStartTime(Process p)
    {
        try { return p.StartTime; } catch { return null; }
    }

    public async Task StartAsync(
        GameServer server,
        LifecycleInitiator initiator = LifecycleInitiator.Operator,
        string reason = "Start requested")
    {
        if (IsRunning(server.Id)) return;
        // Reject invalid profiles before persisting Running intent. Otherwise a bad executable
        // or credential could be retried forever after the manager restarts.
        var plugin = GameRegistry.Get(server.GameId);
        if (plugin == null) throw new InvalidOperationException("This profile is not a Rust Dedicated Server.");
        var validationError = plugin.ValidateBeforeStart(server);
        if (validationError != null) throw new InvalidOperationException(validationError);

        var operation = _lifecycle.RequestStart(server, initiator, reason);
        var operationToken = _lifecycle.GetCancellationToken(server, operation.Generation);
        var gate = _lifecycleGates.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        try { await gate.WaitAsync(operationToken); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (!_lifecycle.IsCurrent(server, operation.Generation)
                || server.DesiredState != ServerDesiredState.Running)
                return;
            try { await StartCoreAsync(server, operation, operationToken); }
            catch (OperationCanceledException) when (!_lifecycle.IsCurrent(server, operation.Generation))
            {
                // A newer Stop/Start owns the profile now; cancellation is an expected outcome.
            }
        }
        finally { gate.Release(); }
    }

    private async Task StartCoreAsync(
        GameServer server,
        ServerLifecycleTransition operation,
        CancellationToken operationToken = default)
    {
        if (IsRunning(server.Id)) return;

        var plugin = GameRegistry.Get(server.GameId);
        if (plugin == null) throw new InvalidOperationException("This profile is not a Rust Dedicated Server.");

        var validationError = plugin.ValidateBeforeStart(server);
        if (validationError != null) throw new InvalidOperationException(validationError);

        SetStatus(server, ServerStatus.Starting);
        try
        {

        try { Directory.CreateDirectory(server.InstallPath); } catch { }
        await plugin.PreStartAsync(server);
        operationToken.ThrowIfCancellationRequested();

        // Pre-flight: kill any zombie instance of THIS server's own executable.
        // Must match on the resolved install-path exe, not just process name — two
        // Rust servers share the RustDedicated process name, so killing by name alone
        // name, and killing by name alone would kill the other server's process.
        var inst0 = new ServerInstance(server);
        _running[server.Id] = inst0;
        var exeName = Path.GetFileNameWithoutExtension(plugin.Executable);
        if (!string.IsNullOrEmpty(exeName))
        {
            var expectedExePath = Path.Combine(server.InstallPath, plugin.Executable);
            var otherRunningPids = _running.Values
                .Where(i => i.Server.Id != server.Id)
                .Select(i => i.Process?.Id ?? 0)
                .ToHashSet();

            var zombies = Process.GetProcessesByName(exeName)
                                 .Where(p =>
                                 {
                                     try
                                     {
                                         if (p.HasExited || otherRunningPids.Contains(p.Id)) return false;
                                         var path = p.MainModule?.FileName;
                                         return path != null &&
                                                string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedExePath),
                                                    StringComparison.OrdinalIgnoreCase);
                                     }
                                     catch { return false; }
                                 })
                                 .ToList();
            foreach (var z in zombies)
            {
                try
                {
                    z.Kill(entireProcessTree: true);
                    z.WaitForExit(3000);
                    var msg = new ConsoleMessage { Text = $"[PRE-FLIGHT] Killed leftover process {exeName} (PID {z.Id})", Type = ConsoleMessageType.Warning };
                    inst0.AddToLog(msg);
                    LogReceived?.Invoke(server.Id, msg);
                }
                catch { /* process already gone */ }
            }
            if (zombies.Count > 0)
                await Task.Delay(1000); // brief pause so OS releases ports
        }

        // Pre-flight: auto-reassign ports if any are in use
        var conflictingPorts = PortCheckerService.CheckServerPorts(server).Where(r => !r.IsAvailable).ToList();
        if (conflictingPorts.Any() && server.LastStarted == null)
        {
            int oldGame  = server.ServerPort;
            int oldQuery = server.QueryPort;
            int oldRcon  = server.RconPort;
            int oldApp   = server.GameSpecificSettings.TryGetValue("appPort", out var appPortText)
                && int.TryParse(appPortText, out var appPort) ? appPort : 0;

            // Find a free offset (up to 1000) where all ports are available
            int offset = 1;
            while (offset < 1000)
            {
                bool ok = true;
                if (!PortCheckerService.CheckPort(oldGame + offset, "UDP").IsAvailable)  { ok = false; }
                if (ok && oldQuery > 0 && !PortCheckerService.CheckPort(oldQuery + offset, "UDP").IsAvailable) { ok = false; }
                if (ok && oldRcon  > 0 && !PortCheckerService.CheckPort(oldRcon  + offset, "TCP").IsAvailable) { ok = false; }
                if (ok && oldApp   > 0 && !PortCheckerService.CheckPort(oldApp   + offset, "TCP").IsAvailable) { ok = false; }
                if (ok) break;
                offset++;
            }

            if (offset < 1000)
            {
                server.ServerPort = oldGame  + offset;
                if (oldQuery > 0) server.QueryPort = oldQuery + offset;
                if (oldRcon  > 0) server.RconPort  = oldRcon  + offset;
                if (oldApp   > 0) server.GameSpecificSettings["appPort"] = (oldApp + offset).ToString();

                var msg = new ConsoleMessage
                {
                    Text = $"[HighPop] Ports in use — automatically reassigned: game {oldGame}→{server.ServerPort}" +
                           (oldQuery > 0 ? $", query {oldQuery}→{server.QueryPort}" : "") +
                           (oldRcon  > 0 ? $", rcon {oldRcon}→{server.RconPort}"   : "") +
                           (oldApp   > 0 ? $", Rust+ {oldApp}→{oldApp + offset}"   : ""),
                    Type = ConsoleMessageType.Warning
                };
                inst0.AddToLog(msg);
                LogReceived?.Invoke(server.Id, msg);
                PortsReassigned?.Invoke(server);
            }
            else
            {
                // Could not find free ports — warn and proceed anyway
                foreach (var r in conflictingPorts)
                {
                    var w = new ConsoleMessage { Text = $"[PRE-FLIGHT] ⚠ {r.Message}", Type = ConsoleMessageType.Warning };
                    inst0.AddToLog(w);
                    LogReceived?.Invoke(server.Id, w);
                }
            }
        }
        else if (conflictingPorts.Any())
        {
            // Not the first start for this server — leave the saved ports alone, just warn clearly
            // so the user can fix it manually (stop the conflicting process, or change the port).
            foreach (var r in conflictingPorts)
            {
                var w = new ConsoleMessage
                {
                    Text = $"[PRE-FLIGHT] ⚠ {r.Message} — fix it in Settings or stop whatever else is using that port.",
                    Type = ConsoleMessageType.Warning
                };
                inst0.AddToLog(w);
                LogReceived?.Invoke(server.Id, w);
            }
        }

        var args = plugin.BuildStartArguments(server);
        if (!string.IsNullOrWhiteSpace(server.CustomArgs)) args += $" {server.CustomArgs.Replace(Environment.NewLine, " ")}";
        var exe  = Path.Combine(server.InstallPath, plugin.Executable);

        // If primary exe missing, try auto-detect from install folder
        if (!File.Exists(exe))
        {
            var found = TryFindExecutable(server.InstallPath, plugin.Executable);
            if (found != null)
                exe = found;
            else
                throw new FileNotFoundException("Server executable not found in: " + server.InstallPath);
        }

        var exeDir = Path.GetDirectoryName(exe) ?? server.InstallPath;
        var oxideInstalled = ModManagerService.GetInstalledOxideVersion(server.InstallPath) != null;
        var carbonInstalled = ModManagerService.IsCarbonInstalled(server.InstallPath);
        var useOxideLiveLog = ShouldUseOxideLiveLog(oxideInstalled, carbonInstalled);
        var captureProcessStreams = !useOxideLiveLog;
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            WorkingDirectory       = exeDir,
            UseShellExecute        = false,
            RedirectStandardOutput = captureProcessStreams,
            RedirectStandardError  = captureProcessStreams,
            RedirectStandardInput  = captureProcessStreams,
            // Oxide expects valid Windows console handles. CREATE_NO_WINDOW leaves them
            // unsupported and makes Oxide install a second output-redirection path.
            // WindowStyle.Hidden supplies real handles without exposing the console window.
            CreateNoWindow         = captureProcessStreams,
            WindowStyle            = ProcessWindowStyle.Hidden,
        };
        if (captureProcessStreams)
        {
            psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
            psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
        }
        psi.Environment["ROGUERUST_UPDATE_MANIFEST_URL"] =
            ModManagerService.GetRogueRustManifestUrl(server.RogueRustChannel);

        var inst = inst0;

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // Some engines (especially Oxide) mirror the same line to multiple transports.
        // ServerInstance owns cross-transport de-duplication so the Rust logfile fallback
        // can also share it with stdout and stderr.
        var lastStdoutType = ConsoleMessageType.Info;
        var lastStderrType = ConsoleMessageType.Info;

        void AddLog(string text, bool fromStandardError)
        {
            var previousType = fromStandardError ? lastStderrType : lastStdoutType;
            var parsed = ConsoleOutputParser.Parse(text, fromStandardError, previousType);
            if (parsed == null) return;
            if (fromStandardError) lastStderrType = parsed.Type;
            else lastStdoutType = parsed.Type;

            var transport = fromStandardError ? "stderr" : "stdout";
            if (!inst.TryAcceptConsoleLine(parsed.Text, transport)) return;

            var msg = new ConsoleMessage
            {
                Text = parsed.Text,
                Type = parsed.Type,
                Source = parsed.Source,
            };
            inst.AddToLog(msg);
            LogReceived?.Invoke(server.Id, msg);
            if (server.GameId == "rust"
                && parsed.Text.Contains("Server startup complete", StringComparison.OrdinalIgnoreCase))
                MarkServerReady(server, inst, "Rust startup log");
        }

        if (captureProcessStreams) proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (plugin.IsNoiseLine(e.Data)) return;
            AddLog(e.Data, fromStandardError: false);
        };

        if (captureProcessStreams) proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (plugin.IsNoiseLine(e.Data)) return;
            AddLog(e.Data, fromStandardError: true);
        };

        proc.Exited += async (_, _) =>
        {
            try
            {
                inst.DailyRestartCts.Cancel();
                JobObjectService.ReleaseJob(inst.JobHandle);
                inst.JobHandle = nint.Zero;

                // An old Exited callback must never remove or change the state of a
                // replacement process that has already started for this profile.
                if (!IsCurrentInstance(server.Id, inst)) return;

                _network.UnregisterServer(server.Id);
                var exitCode = SafeExitCode(proc);
                RecordProcessExit(server, inst, exitCode, inst.IntentionalStop);
                ClearRunningIdentity(server);

                if (inst.IntentionalStop)
                {
                    RemoveInstanceIfCurrent(server.Id, inst);
                    SetStatus(server, ServerStatus.Stopped);
                    return;
                }

                SetStatus(server, ServerStatus.Error);

                if (!ServerLifecycleRules.CanRecover(server))
                {
                    RemoveInstanceIfCurrent(server.Id, inst);
                    _lifecycle.MarkPhase(server, ServerLifecyclePhase.Faulted,
                        LifecycleInitiator.AlwaysOn,
                        "Rust process exited unexpectedly and recovery policy is not active");
                    return;
                }

                // Crash history belongs to the server profile, not one short-lived process
                // instance. Otherwise every successful relaunch resets the protection.
                var now = DateTime.Now;
                var crashes = _crashHistory.GetOrAdd(server.Id, _ => new ConcurrentQueue<DateTime>());
                crashes.Enqueue(now);
                while (crashes.TryPeek(out var oldest) && now - oldest > TimeSpan.FromMinutes(10))
                    crashes.TryDequeue(out _);
                var crashCount = crashes.Count;

                int maxRetries = server.AutoRestartMaxRetries > 0 ? server.AutoRestartMaxRetries : 5;

                if (!server.KeepOnline && crashCount > maxRetries)
                {
                    var giveUp = new ConsoleMessage
                    {
                        Text = $"[HighPop] Server crashed {crashCount}× in 10 min (limit {maxRetries}). Auto-restart disabled.",
                        Type = ConsoleMessageType.Error
                    };
                    inst.AddToLog(giveUp);
                    LogReceived?.Invoke(server.Id, giveUp);
                    server.AutoRestart = false;
                    SetStatus(server, ServerStatus.Error);
                    RemoveInstanceIfCurrent(server.Id, inst);
                    CrashLimitReached?.Invoke(server.Id);
                    return;
                }

                int baseDelaySec = Math.Clamp(
                    server.AutoRestartDelaySec > 0 ? server.AutoRestartDelaySec : 10,
                    1,
                    300);
                int delaySec = server.KeepOnline
                    ? Math.Min(300, baseDelaySec * (1 << Math.Min(Math.Max(0, crashCount - 1), 5)))
                    : baseDelaySec;
                inst.RestartCount++;
                var delayMsg = new ConsoleMessage
                {
                    Text = server.KeepOnline
                        ? $"[HighPop] Server stopped unexpectedly (crash #{crashCount}). Always-on recovery starts in {delaySec}s..."
                        : $"[HighPop] Server stopped unexpectedly (crash #{crashCount}/{maxRetries}). Restarting in {delaySec}s...",
                    Type = ConsoleMessageType.Warning
                };
                inst.AddToLog(delayMsg);
                LogReceived?.Invoke(server.Id, delayMsg);

                RemoveInstanceIfCurrent(server.Id, inst);
                var recovery = _lifecycle.BeginRecovery(server,
                    $"Rust process exited unexpectedly (crash #{crashCount})");
                var generation = _recoveryGenerations.AddOrUpdate(server.Id, 1, (_, current) => current + 1);
                await RecoverUnexpectedExitAsync(server, generation, recovery, delaySec);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HighPop] Exited handler error for {server.Id}: {ex.Message}");
            }
        };

        operationToken.ThrowIfCancellationRequested();

        // Match the clean batch-file launch for Oxide: use real hidden console handles with
        // no redirected standard streams and make a fresh logfile HPRM's sole console source.
        if (useOxideLiveLog)
            RotateOxideLogForLaunch(server);

        try
        {
            proc.Start();
        }
        catch (Win32Exception ex)
        {
            _running.TryRemove(server.Id, out _);
            SetStatus(server, ServerStatus.Error);
            var errMsg = new ConsoleMessage
            {
                Text = $"[ERR] Failed to start server process: {ex.Message}",
                Type = ConsoleMessageType.Error
            };
            LogReceived?.Invoke(server.Id, errMsg);
            throw;
        }

        // Publish process identity before output handlers can observe a fast startup line.
        inst.Process   = proc;
        inst.StartTime = DateTime.Now;
        inst.MarkProcessObserved();
        server.LastStarted = DateTime.Now;
        RememberRunningIdentity(server, proc);

        // Establish Oxide's logfile baseline before redirected reads begin. Carbon retains
        // its process-stream console, which carries Carbon's richer formatted reporting.
        if (useOxideLiveLog)
        {
            inst.RustLogTailCts = new CancellationTokenSource();
            _ = TailRustLogAsync(server, inst, includeRecentHistory: false,
                cancellationToken: inst.RustLogTailCts.Token);
        }

        if (captureProcessStreams)
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }

        // Rust output is captured in HighPop, so hide any window the process creates. Carbon
        // uses CreateNoWindow; Oxide receives a real but hidden console because its logger
        // duplicates output when Windows reports unsupported standard handles.
        _ = ApplyWindowStyleAsync(proc, SW_HIDE, 30);

        // Apply CPU affinity
        if (server.CpuAffinityMask != 0)
        {
            try { proc.ProcessorAffinity = (IntPtr)server.CpuAffinityMask; } catch { }
        }

        // Apply process priority
        try
        {
            proc.PriorityClass = server.ProcessPriority switch
            {
                "AboveNormal" => ProcessPriorityClass.AboveNormal,
                "High"        => ProcessPriorityClass.High,
                "BelowNormal" => ProcessPriorityClass.BelowNormal,
                "RealTime"    => ProcessPriorityClass.RealTime,
                _             => ProcessPriorityClass.Normal,
            };
        }
        catch { }

        // Apply RAM limit via Windows Job Object
        if (server.MaxRamMb > 0)
        {
            inst.JobHandle = JobObjectService.ApplyRamLimit(proc, server.MaxRamMb);
            var ramMessage = new ConsoleMessage
            {
                Text = inst.JobHandle != nint.Zero
                    ? $"[HighPop] Hard RAM cap active at {server.MaxRamMb:N0} MB. Windows can terminate Rust if this limit is exceeded."
                    : $"[HighPop] Could not apply the configured {server.MaxRamMb:N0} MB RAM cap.",
                Type = inst.JobHandle != nint.Zero ? ConsoleMessageType.Warning : ConsoleMessageType.Error,
                Source = "HighPop",
            };
            inst.AddToLog(ramMessage);
            LogReceived?.Invoke(server.Id, ramMessage);
        }

        // Schedule daily restart if enabled
        if (server.DailyRestartEnabled)
            _ = RunDailyRestartAsync(server, inst);

        // Register with bandwidth tracking
        _network.RegisterServer(server.Id, proc.Id);

        // Add firewall rules
        if (server.FirewallAutoManage)
        {
            var firewall = FirewallService.AddRules(server);
            if (!firewall.Success)
                InjectLogLine(server.Id, $"[Firewall] {firewall.Message}", ConsoleMessageType.Error);
        }

        SetStatus(server, ServerStatus.Running);
        _lifecycle.MarkPhase(server, ServerLifecyclePhase.ProcessRunning,
            operation.Initiator, "Rust process started", operation.OperationId);
        _ = _telemetry.AppendAsync(
            server,
            "server.process_started",
            "manager",
            new Dictionary<string, string>
            {
                ["pid"] = proc.Id.ToString(),
                ["port"] = server.ServerPort.ToString(),
                ["rconPort"] = server.RconPort.ToString(),
            });
        }
        catch
        {
            _running.TryRemove(server.Id, out _);
            ClearRunningIdentity(server);
            SetStatus(server, ServerStatus.Error);
            if (_lifecycle.IsCurrent(server, operation.Generation))
                _lifecycle.MarkPhase(server,
                    operation.Initiator == LifecycleInitiator.AlwaysOn
                        ? ServerLifecyclePhase.Recovering
                        : ServerLifecyclePhase.Faulted,
                    operation.Initiator, "Rust process failed to start", operation.OperationId);
            throw;
        }
    }

    public async Task StopAsync(
        GameServer server,
        string reason = "Requested from HighPop",
        LifecycleInitiator initiator = LifecycleInitiator.Operator)
    {
        // Persist desired Stopped before waiting for any active lifecycle operation. A queued
        // or stale Start observes the new generation and cannot resurrect this server.
        var operation = _lifecycle.RequestStop(server, initiator, reason);
        CancelRecovery(server.Id);
        var gate = _lifecycleGates.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!_lifecycle.IsCurrent(server, operation.Generation)) return;
            await StopCoreAsync(server, reason, operation);
        }
        finally { gate.Release(); }
    }

    private async Task StopCoreAsync(
        GameServer server,
        string reason,
        ServerLifecycleTransition operation)
    {
        if (!_running.TryGetValue(server.Id, out var inst))
        {
            // HighPop may have been restarted while this server kept running — fall back
            // to killing the orphaned PID we persisted to disk.
            await KillOrphanedPidAsync(server);
            _lifecycle.MarkPhase(server, ServerLifecyclePhase.StoppedByOperator,
                operation.Initiator, reason, operation.OperationId);
            return;
        }
        inst.IntentionalStop = true;
        inst.StopReason = reason;
        inst.DailyRestartCts.Cancel();
        SetStatus(server, ServerStatus.Stopping);
        InjectLogLine(server.Id, $"[HighPop] Stop requested: {reason}", ConsoleMessageType.Warning);

        var plugin = GameRegistry.Get(server.GameId);
        var stopCmd = plugin?.GetStopCommand(server);

        if (stopCmd != null && inst.Process?.HasExited == false)
        {
            await TrySendServerCommandAsync(server, inst, "server.save", "Saving Rust world before shutdown");
            await Task.Delay(1000);
            await TrySendServerCommandAsync(server, inst, stopCmd, "Requesting clean Rust shutdown");
            try
            {
                var timeoutSeconds = ServerMaintenancePolicy.ClampGracefulStopTimeout(
                    server.GracefulStopTimeoutSeconds);
                using var gracefulStopCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                await inst.Process.WaitForExitAsync(gracefulStopCts.Token);
            }
            catch (OperationCanceledException)
            {
                var timeoutSeconds = ServerMaintenancePolicy.ClampGracefulStopTimeout(
                    server.GracefulStopTimeoutSeconds);
                InjectLogLine(server.Id,
                    $"[HighPop] Rust did not exit within {timeoutSeconds} seconds; forcing the process to close.",
                    ConsoleMessageType.Warning);
            }
        }

        RemoveInstanceIfCurrent(server.Id, inst);
        _network.UnregisterServer(server.Id);
        if (server.FirewallAutoManage) FirewallService.RemoveRules(server);

        if (inst.Process?.HasExited == false)
            inst.Process.Kill(entireProcessTree: true);

        ClearRunningIdentity(server);
        SetStatus(server, ServerStatus.Stopped);
        _lifecycle.MarkPhase(server, ServerLifecyclePhase.StoppedByOperator,
            operation.Initiator, reason, operation.OperationId);
    }

    /// <summary>
    /// Kills a process by the PID persisted on the server model, used when HighPop lost its
    /// in-memory ServerInstance (e.g. after HighPop itself was restarted) but the game server
    /// process is still alive in the background.
    /// </summary>
    private Task KillOrphanedPidAsync(GameServer server)
    {
        SetStatus(server, ServerStatus.Stopping);
        if (server.RunningPid > 0)
        {
            try
            {
                var p = Process.GetProcessById(server.RunningPid);
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
        }
        ClearRunningIdentity(server);
        if (server.FirewallAutoManage) FirewallService.RemoveRules(server);
        SetStatus(server, ServerStatus.Stopped);
        return Task.CompletedTask;
    }

    public async Task SendCommandAsync(string serverId, string command)
    {
        if (!_running.TryGetValue(serverId, out var inst)) return;
        await TrySendServerCommandAsync(inst.Server, inst, command, $"Sent '{command}'");
    }

    public void SendCommand(string serverId, string command)
        => _ = SendCommandAsync(serverId, command);

    public void InjectLogLine(string serverId, string text, ConsoleMessageType type = ConsoleMessageType.System)
    {
        var msg = new ConsoleMessage { Text = text, Type = type };
        if (_running.TryGetValue(serverId, out var inst))
            inst.AddToLog(msg);
        LogReceived?.Invoke(serverId, msg);
    }

    public bool IsRunning(string serverId)
        => _running.TryGetValue(serverId, out var inst) && inst.Process?.HasExited == false;

    public async Task ForceStopAsync(
        GameServer server,
        LifecycleInitiator initiator = LifecycleInitiator.Operator,
        string reason = "Force Stop requested from HighPop")
    {
        var operation = _lifecycle.RequestStop(server, initiator, reason);
        CancelRecovery(server.Id);
        var gate = _lifecycleGates.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!_lifecycle.IsCurrent(server, operation.Generation)) return;
            await ForceStopCoreAsync(server, operation);
        }
        finally { gate.Release(); }
    }

    private async Task ForceStopCoreAsync(
        GameServer server,
        ServerLifecycleTransition operation)
    {
        if (!_running.TryGetValue(server.Id, out var inst))
        {
            await KillOrphanedPidAsync(server);
            _lifecycle.MarkPhase(server, ServerLifecyclePhase.StoppedByOperator,
                operation.Initiator, operation.Reason, operation.OperationId);
            return;
        }
        SetStatus(server, ServerStatus.Stopping);
        inst.IntentionalStop = true;
        inst.StopReason = "Force Stop requested from HighPop";
        inst.DailyRestartCts.Cancel();
        InjectLogLine(server.Id,
            "[HighPop] Force Stop requested — saving the Rust world, requesting exit, then enforcing shutdown.",
            ConsoleMessageType.Warning);

        if (inst.Process?.HasExited == false)
        {
            await TrySendServerCommandAsync(server, inst, "server.save", "Force-saving Rust world");
            await TrySendServerCommandAsync(server, inst, "quit", "Requesting immediate Rust exit");
            try
            {
                using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await inst.Process.WaitForExitAsync(exitCts.Token);
            }
            catch (OperationCanceledException)
            {
                InjectLogLine(server.Id,
                    "[HighPop] Rust did not exit after the force-save request; terminating the process tree.",
                    ConsoleMessageType.Warning);
            }
        }

        JobObjectService.ReleaseJob(inst.JobHandle);
        inst.JobHandle = nint.Zero;
        _running.TryRemove(server.Id, out _);
        _network.UnregisterServer(server.Id);
        if (server.FirewallAutoManage) FirewallService.RemoveRules(server);
        try { inst.Process?.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* process already dead — swallow */ }
        ClearRunningIdentity(server);
        SetStatus(server, ServerStatus.Stopped);
        _lifecycle.MarkPhase(server, ServerLifecyclePhase.StoppedByOperator,
            operation.Initiator, operation.Reason, operation.OperationId);
    }

    /// <summary>Compatibility alias for older call sites; Force Stop is the defined operation.</summary>
    public Task KillAsync(GameServer server) => ForceStopAsync(server);

    private async Task<bool> TrySendProcessCommandAsync(
        ServerInstance instance,
        string command)
    {
        if (instance.Process?.HasExited != false) return false;
        try
        {
            await instance.Process.StandardInput.WriteLineAsync(command);
            await instance.Process.StandardInput.FlushAsync();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task<bool> TrySendServerCommandAsync(
        GameServer server,
        ServerInstance instance,
        string command,
        string description)
    {
        if (await TrySendProcessCommandAsync(instance, command))
        {
            InjectLogLine(server.Id, $"[HighPop] {description}.", ConsoleMessageType.System);
            return true;
        }

        // StandardInput cannot be reacquired after the manager process exits. A verified
        // reattachment therefore uses Rust WebRCON for commands and graceful shutdown.
        if (server.GameId.Equals("rust", StringComparison.OrdinalIgnoreCase)
            && server.RconPort > 0
            && !string.IsNullOrWhiteSpace(server.RconPassword))
        {
            try
            {
                using var rcon = new RconService();
                var host = server.ServerIp is "0.0.0.0" or "::" or "[::]"
                    ? "127.0.0.1"
                    : server.ServerIp;
                if (await rcon.ConnectAsync(host, server.RconPort, server.RconPassword))
                {
                    await rcon.SendCommandAsync(command);
                    InjectLogLine(server.Id,
                        $"[HighPop] {description} via WebRCON.", ConsoleMessageType.System);
                    return true;
                }
            }
            catch { }
        }

        InjectLogLine(server.Id,
            $"[HighPop] Could not send '{command}' through the process console or WebRCON.",
            ConsoleMessageType.Warning);
        return false;
    }

    /// <summary>
    /// Releases HighPop-owned monitoring resources while leaving every live Rust process alone.
    /// The persisted process identity is retained so the next manager instance can verify and
    /// reattach. Explicit Stop and Force Stop remain the only operations that terminate Rust.
    /// </summary>
    public void DetachAllForManagerExit()
    {
        foreach (var id in _recoveryGenerations.Keys)
            CancelRecovery(id);
        foreach (var id in _running.Keys.ToList())
        {
            try
            {
                var instance = _running[id];
                instance.IntentionalStop = true;
                instance.DailyRestartCts.Cancel();
                instance.CancelAuxiliaryMonitoring();
                JobObjectService.ReleaseJob(instance.JobHandle);
                instance.JobHandle = nint.Zero;

                if (instance.Process?.HasExited == false)
                    RememberRunningIdentity(instance.Server, instance.Process);
                else
                    ClearRunningIdentity(instance.Server);
            }
            catch { }
        }
        _running.Clear();
    }

    private static void RememberRunningIdentity(GameServer server, Process process)
    {
        server.RunningPid = process.Id;
        server.RunningProcessStartedUtc = SafeStartTime(process)?.ToUniversalTime();
        try { server.RunningExecutablePath = process.MainModule?.FileName ?? string.Empty; }
        catch { server.RunningExecutablePath = string.Empty; }
    }

    private static void ClearRunningIdentity(GameServer server)
    {
        server.RunningPid = 0;
        server.RunningProcessStartedUtc = null;
        server.RunningExecutablePath = string.Empty;
    }

    private void CancelRecovery(string serverId)
        => _recoveryGenerations.AddOrUpdate(serverId, 1, (_, current) => current + 1);

    public void QueueAlwaysOnRecovery(GameServer server, string reason, int initialDelaySeconds = 10)
    {
        if (!ServerLifecycleRules.CanRecover(server) || IsRunning(server.Id)) return;
        var recovery = _lifecycle.BeginRecovery(server, reason);
        var generation = _recoveryGenerations.AddOrUpdate(server.Id, 1, (_, current) => current + 1);
        InjectLogLine(
            server.Id,
            $"[HighPop] Always-on recovery queued after {reason}.",
            ConsoleMessageType.Warning);
        _ = RecoverUnexpectedExitAsync(server, generation, recovery, initialDelaySeconds);
    }

    private async Task RecoverUnexpectedExitAsync(
        GameServer server,
        long generation,
        ServerLifecycleTransition recovery,
        int initialDelaySeconds)
    {
        var delay = TimeSpan.FromSeconds(Math.Clamp(initialDelaySeconds, 1, 300));
        while (_recoveryGenerations.TryGetValue(server.Id, out var current)
               && current == generation
               && _lifecycle.IsCurrent(server, recovery.Generation)
               && ServerLifecycleRules.CanRecover(server))
        {
            await Task.Delay(delay);
            if (!_recoveryGenerations.TryGetValue(server.Id, out current) || current != generation)
                return;
            if (!_lifecycle.IsCurrent(server, recovery.Generation)
                || !ServerLifecycleRules.CanRecover(server))
                return;

            try
            {
                var gate = _lifecycleGates.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync();
                try
                {
                    if (!_lifecycle.IsCurrent(server, recovery.Generation)
                        || !ServerLifecycleRules.CanRecover(server))
                        return;
                    await StartCoreAsync(server, recovery);
                }
                finally { gate.Release(); }
                return;
            }
            catch (Exception ex)
            {
                SetStatus(server, ServerStatus.Error);
                InjectLogLine(
                    server.Id,
                    $"[HighPop] Recovery start failed: {ex.Message}. Retrying in {Math.Min(300, delay.TotalSeconds * 2):0}s.",
                    ConsoleMessageType.Error);
                delay = TimeSpan.FromSeconds(Math.Min(300, delay.TotalSeconds * 2));
            }
        }
    }


    private static async Task ApplyWindowStyleAsync(System.Diagnostics.Process proc, int showCmd, int maxAttempts)
    {
        try
        {
            for (int i = 0; i < maxAttempts && !proc.HasExited; i++)
            {
                try
                {
                    proc.Refresh();
                    var hwnd = proc.MainWindowHandle;
                    if (hwnd != IntPtr.Zero) { ShowWindow(hwnd, showCmd); return; }
                }
                catch { return; }
                await Task.Delay(i < 5 ? 100 : 500);
            }
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    private const int SW_HIDE    = 0; // hide window completely
    private const int SW_RESTORE = 9;

    public void ShowWindow(GameServer server)
    {
        if (!_running.TryGetValue(server.Id, out var inst)) return;
        var proc = inst.Process;
        if (proc == null || proc.HasExited) return;
        try
        {
            var hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        catch { }
    }

    private static readonly (int minutesBefore, string label)[] RestartWarnings =
    [
        (10, "10 minutes"),
        (5,  "5 minutes"),
        (1,  "1 minute"),
    ];

    /// <summary>
    /// Sends a broadcast message to in-game players via the game's console/RCON/REST
    /// broadcast command, if supported. No-ops silently for games with no known way to
    /// broadcast (most plugins' GetBroadcastCommand returns null in that case).
    /// </summary>
    public async Task WarnPlayersAsync(GameServer server, string message)
    {
        var plugin = GameRegistry.Get(server.GameId);
        var cmd = plugin?.GetBroadcastCommand(message);
        if (cmd == null) return;
        try { await SendCommandAsync(server.Id, cmd); } catch { }

        if (_running.TryGetValue(server.Id, out var inst))
        {
            var msg = new ConsoleMessage { Text = $"[HighPop] {message}", Type = ConsoleMessageType.Warning };
            inst.AddToLog(msg);
            LogReceived?.Invoke(server.Id, msg);
        }
    }

    private async Task RunDailyRestartAsync(GameServer server, ServerInstance inst)
    {
        while (_running.TryGetValue(server.Id, out var current) && current == inst && server.DailyRestartEnabled)
        {
            var now = DateTime.Now;
            var target = now.Date + server.DailyRestartTime;
            if (target <= now)
                target = target.AddDays(1);

            try
            {
                foreach (var (minutesBefore, label) in RestartWarnings)
                {
                    var warnAt = target.AddMinutes(-minutesBefore);
                    if (warnAt <= DateTime.Now) continue;
                    await Task.Delay(warnAt - DateTime.Now, inst.DailyRestartCts.Token);
                    if (!_running.TryGetValue(server.Id, out var c1) || c1 != inst || !server.DailyRestartEnabled)
                        return;
                    await WarnPlayersAsync(server, $"Server restarting in {label}");
                }

                var finalDelay = target - DateTime.Now;
                if (finalDelay > TimeSpan.Zero)
                    await Task.Delay(finalDelay, inst.DailyRestartCts.Token);
            }
            catch (TaskCanceledException) { return; }

            if (!_running.TryGetValue(server.Id, out var c) || c != inst || !server.DailyRestartEnabled)
                return;

            var msg = new ConsoleMessage
            {
                Text = $"[HighPop] Daily restart triggered at {server.DailyRestartTime:hh\\:mm}",
                Type = ConsoleMessageType.Warning
            };
            inst.AddToLog(msg);
            LogReceived?.Invoke(server.Id, msg);

            try
            {
                await SendCommandAsync(server.Id, "server.save");
                await Task.Delay(1000);
                await StopAsync(server, "Scheduled daily restart", LifecycleInitiator.Scheduler);
                var stoppedGeneration = server.LifecycleGeneration;
                await Task.Delay(3000);
                if (!ServerLifecycleRules.IsCurrent(server, stoppedGeneration)
                    || server.DesiredState != ServerDesiredState.Stopped)
                    return;
                await StartAsync(server, LifecycleInitiator.Scheduler, "Scheduled daily restart");
            }
            catch (Exception ex)
            {
                var error = new ConsoleMessage
                {
                    Text = $"[HighPop] Daily restart failed: {ex.Message}",
                    Type = ConsoleMessageType.Error,
                };
                inst.AddToLog(error);
                try { LogReceived?.Invoke(server.Id, error); } catch { }
            }
            return; // new instance will spawn its own RunDailyRestartAsync
        }
    }

    private void SetStatus(GameServer server, ServerStatus status)
    {
        // Stop/Kill set Stopped explicitly, and the process's own Exited handler also sets
        // Stopped when it fires (race ordering varies) — skip the no-op transition so only
        // one StatusChanged event (and one Discord notification) fires per real change.
        if (server.Status == status) return;
        server.Status = status;
        var handlers = StatusChanged?.GetInvocationList();
        if (handlers == null) return;
        foreach (var handler in handlers)
        {
            try { ((Action<string, ServerStatus>)handler)(server.Id, status); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ServerManager] StatusChanged subscriber failed: {ex}");
            }
        }
    }

    private void MarkServerReady(GameServer server, ServerInstance inst, string source)
    {
        if (!IsCurrentInstance(server.Id, inst)) return;
        var phase = source.Contains("WebRCON", StringComparison.OrdinalIgnoreCase)
            ? ServerLifecyclePhase.RconReady
            : ServerLifecyclePhase.RustReady;
        _lifecycle.MarkPhase(server, phase, server.LastLifecycleInitiator, source,
            server.LastLifecycleOperationId);
        if (!inst.TryMarkReady(source)) return;
        var elapsed = inst.Uptime;
        var msg = new ConsoleMessage
        {
            Text = $"[HighPop] Rust is ready after {FormatDuration(elapsed)} ({source}). Automated health and idle checks are now active.",
            Type = ConsoleMessageType.System,
        };
        inst.AddToLog(msg);
        LogReceived?.Invoke(server.Id, msg);
        _ = _telemetry.AppendAsync(
            server,
            "server.ready",
            source,
            new Dictionary<string, string>
            {
                ["startupSeconds"] = Math.Max(0, (int)elapsed.TotalSeconds).ToString(),
            });
    }

    private void RecordProcessExit(GameServer server, ServerInstance inst, int? exitCode, bool intentional)
    {
        var reason = intentional
            ? (string.IsNullOrWhiteSpace(inst.StopReason) ? "Intentional stop" : inst.StopReason)
            : $"RustDedicated exited unexpectedly with code {(exitCode?.ToString() ?? "unknown")}";
        server.LastExitAt = DateTime.Now;
        server.LastExitCode = exitCode;
        server.LastExitReason = reason;

        var capHint = !intentional && server.MaxRamMb > 0
            ? $" A hard RAM cap of {server.MaxRamMb:N0} MB is configured; check whether Rust exceeded it."
            : string.Empty;
        var msg = new ConsoleMessage
        {
            Text = $"[HighPop] {reason} after {FormatDuration(inst.Uptime)}.{capHint}",
            Type = intentional ? ConsoleMessageType.System : ConsoleMessageType.Error,
        };
        inst.AddToLog(msg);
        LogReceived?.Invoke(server.Id, msg);
        _ = _telemetry.AppendAsync(
            server,
            intentional ? "server.stopped" : "server.exited",
            "manager",
            new Dictionary<string, string>
            {
                ["intentional"] = intentional.ToString(),
                ["exitCode"] = exitCode?.ToString() ?? "unknown",
                ["reason"] = reason,
                ["uptimeSeconds"] = Math.Max(0, (int)inst.Uptime.TotalSeconds).ToString(),
            });
    }

    private bool IsCurrentInstance(string serverId, ServerInstance instance)
        => _running.TryGetValue(serverId, out var current) && ReferenceEquals(current, instance);

    internal static bool ShouldUseOxideLiveLog(bool oxideInstalled, bool carbonInstalled)
        => oxideInstalled && !carbonInstalled;

    private static void RotateOxideLogForLaunch(GameServer server)
    {
        var logDirectory = RustPlugin.GetEffectiveLogDirectory(server);
        Directory.CreateDirectory(logDirectory);
        var current = Path.Combine(logDirectory, "RustDedicated.log");
        if (!File.Exists(current)) return;

        var previous = Path.Combine(logDirectory, "RustDedicated.previous.log");
        try
        {
            File.Move(current, previous, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"HighPop could not rotate the previous Oxide log before startup: {current}", ex);
        }
    }

    private bool RemoveInstanceIfCurrent(string serverId, ServerInstance instance)
    {
        var removed = ((ICollection<KeyValuePair<string, ServerInstance>>)_running)
            .Remove(new KeyValuePair<string, ServerInstance>(serverId, instance));
        if (removed) instance.CancelAuxiliaryMonitoring();
        return removed;
    }

    private async Task TailRustLogAsync(
        GameServer server,
        ServerInstance instance,
        bool includeRecentHistory,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(server.GameId, "rust", StringComparison.OrdinalIgnoreCase)) return;
        var logPath = Path.Combine(RustPlugin.GetEffectiveLogDirectory(server), "RustDedicated.log");
        long position = 0;
        var initialized = false;
        var previousType = ConsoleMessageType.Info;

        // A newly launched Oxide server receives a freshly rotated logfile, so consume it
        // from byte zero. Reattachment instead loads a bounded recent history below.
        if (!includeRecentHistory)
        {
            position = 0;
            initialized = true;
        }

        while (!cancellationToken.IsCancellationRequested
               && IsCurrentInstance(server.Id, instance)
               && instance.Process?.HasExited == false)
        {
            try
            {
                if (!File.Exists(logPath))
                {
                    await Task.Delay(750, cancellationToken);
                    continue;
                }

                await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                var discardPartialLine = false;
                if (!initialized)
                {
                    position = includeRecentHistory
                        ? Math.Max(0, stream.Length - 64 * 1024)
                        : stream.Length;
                    stream.Seek(position, SeekOrigin.Begin);
                    discardPartialLine = position > 0;
                    initialized = true;
                }
                else
                {
                    if (stream.Length < position) position = 0;
                    stream.Seek(position, SeekOrigin.Begin);
                }

                using var reader = new StreamReader(stream, leaveOpen: true);
                if (discardPartialLine) await reader.ReadLineAsync(cancellationToken);
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    var parsed = ConsoleOutputParser.Parse(line, fromStandardError: false, previousType);
                    if (parsed == null) continue;
                    previousType = parsed.Type;
                    instance.MarkRustLogActive();
                    if (!instance.TryAcceptConsoleLine(parsed.Text, "logfile")) continue;
                    var message = new ConsoleMessage
                    {
                        Text = parsed.Text,
                        Type = parsed.Type,
                        Source = parsed.Source,
                    };
                    instance.AddToLog(message);
                    LogReceived?.Invoke(server.Id, message);
                    if (parsed.Text.Contains("Server startup complete", StringComparison.OrdinalIgnoreCase))
                        MarkServerReady(server, instance, "Rust logfile");
                }
                position = stream.Position;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            try { await Task.Delay(750, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return null; }
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s"
            : duration.TotalMinutes >= 1
                ? $"{duration.Minutes}m {duration.Seconds}s"
                : $"{Math.Max(0, (int)duration.TotalSeconds)}s";

    private static string? TryFindExecutable(string installPath, string hintExe)
    {
        if (!Directory.Exists(installPath)) return null;

        // 1. exact in root
        var root = Path.Combine(installPath, hintExe);
        if (File.Exists(root)) return root;

        // 2. any *server*.exe or *dedicated*.exe in root
        foreach (var f in Directory.GetFiles(installPath, "*.exe"))
        {
            var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            if (n.Contains("server") || n.Contains("dedicated")) return f;
        }

        // 3. recurse one level
        foreach (var dir in Directory.GetDirectories(installPath))
        {
            var sub = Path.Combine(dir, hintExe);
            if (File.Exists(sub)) return sub;
            foreach (var f in Directory.GetFiles(dir, "*.exe"))
            {
                var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (n.Contains("server") || n.Contains("dedicated")) return f;
            }
        }

        // 4. any .exe at all
        var any = Directory.GetFiles(installPath, "*.exe").FirstOrDefault();
        return any;
    }

}
