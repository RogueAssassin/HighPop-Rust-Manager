using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
    public nint JobHandle { get; set; } = nint.Zero;
    public volatile bool IntentionalStop;
    public string StopReason { get; set; } = string.Empty;
    private int _ready;
    public bool IsReady => Volatile.Read(ref _ready) == 1;
    public DateTime? ReadyTime { get; private set; }
    public string ReadySource { get; private set; } = string.Empty;

    /// <summary>Times of recent crashes (last 10 minutes). Used for crash-loop detection.</summary>
    public List<DateTime> CrashTimes { get; } = [];

    public ServerInstance(GameServer server) => Server = server;

    public bool TryMarkReady(string source)
    {
        if (Interlocked.CompareExchange(ref _ready, 1, 0) != 0) return false;
        ReadyTime = DateTime.Now;
        ReadySource = source;
        return true;
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
}

public class ServerManagerService
{
    // CSI sequences (colors, cursor movement) and OSC sequences (terminal title-set, used by
    // FXServer/txAdmin) — both leak through as garbled text in the embedded console otherwise.
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-9;]*[a-zA-Z]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)", RegexOptions.Compiled);
    private readonly NetworkMonitorService _network;
    private readonly ConfigService _config;
    private readonly RustTelemetryService _telemetry;
    private readonly ConcurrentDictionary<string, ServerInstance> _running = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lifecycleGates = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _crashHistory = new();
    private readonly ConcurrentDictionary<string, long> _recoveryGenerations = new();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public event Action<string, ConsoleMessage>? LogReceived;
    public event Action<string, ServerStatus>?  StatusChanged;
    /// <summary>Fired when a server has crashed too many times and auto-restart gives up.</summary>
    public event Action<string>? CrashLimitReached;
    /// <summary>Fired when a server's ports were automatically reassigned because they were in use.</summary>
    public event Action<GameServer>? PortsReassigned;

    public ServerManagerService(
        ConfigService config,
        NetworkMonitorService network,
        RustTelemetryService telemetry)
    {
        _config  = config;
        _network = network;
        _telemetry = telemetry;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();
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
        if (server.RunningPid <= 0) return false;
        try
        {
            var p = Process.GetProcessById(server.RunningPid);
            if (p.HasExited)
            {
                server.RunningPid = 0;
                return false;
            }

            var plugin = GameRegistry.Get(server.GameId);
            var exeName = plugin != null ? Path.GetFileNameWithoutExtension(plugin.Executable) : null;
            if (!string.IsNullOrEmpty(exeName) && !string.Equals(p.ProcessName, exeName, StringComparison.OrdinalIgnoreCase))
            {
                // PID was recycled by an unrelated process — not actually our server.
                server.RunningPid = 0;
                return false;
            }

            var inst = new ServerInstance(server) { Process = p, StartTime = SafeStartTime(p) };
            inst.TryMarkReady("reattached running process");
            try { p.EnableRaisingEvents = true; } catch { }
            p.Exited += (_, _) =>
            {
                if (!IsCurrentInstance(server.Id, inst)) return;
                _network.UnregisterServer(server.Id);
                RecordProcessExit(server, inst, SafeExitCode(p), inst.IntentionalStop);
                server.RunningPid = 0;
                RemoveInstanceIfCurrent(server.Id, inst);
                SetStatus(server, inst.IntentionalStop ? ServerStatus.Stopped : ServerStatus.Error);
            };
            _running[server.Id] = inst;
            _network.RegisterServer(server.Id, p.Id);
            SetStatus(server, ServerStatus.Running);
            var msg = new ConsoleMessage { Text = $"[HighPop] Reattached to running process (PID {p.Id}) after HighPop restart.", Type = ConsoleMessageType.Info };
            inst.AddToLog(msg);
            LogReceived?.Invoke(server.Id, msg);
            return true;
        }
        catch
        {
            server.RunningPid = 0;
            return false;
        }
    }

    private static DateTime? SafeStartTime(Process p)
    {
        try { return p.StartTime; } catch { return null; }
    }

    public async Task StartAsync(GameServer server)
    {
        var gate = _lifecycleGates.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { await StartCoreAsync(server); }
        finally { gate.Release(); }
    }

    private async Task StartCoreAsync(GameServer server)
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
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            WorkingDirectory       = exeDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            CreateNoWindow         = true,
            WindowStyle            = ProcessWindowStyle.Hidden,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding  = System.Text.Encoding.UTF8,
        };

        var inst = inst0;

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // Some engines (Unity/Rust) write the same line to both stdout and stderr.
        // Suppress duplicates seen within a 200 ms window across both streams.
        var _recentLines = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();

        void AddLog(string text, ConsoleMessageType type)
        {
            // Keep Rust console output readable if a mod emits ANSI escape codes.
            text = AnsiEscapeRegex.Replace(text, "");
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var threshold = System.Diagnostics.Stopwatch.Frequency / 5; // 200 ms
            if (_recentLines.TryGetValue(text, out var seen) && (now - seen) < threshold) return;
            _recentLines[text] = now;
            var msg = new ConsoleMessage { Text = text, Type = type };
            inst.AddToLog(msg);
            LogReceived?.Invoke(server.Id, msg);
            if (server.GameId == "rust"
                && text.Contains("Server startup complete", StringComparison.OrdinalIgnoreCase))
                MarkServerReady(server, inst, "Rust startup log");
        }

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (plugin.IsNoiseLine(e.Data)) return;
            AddLog(e.Data, DetectType(e.Data));
        };

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (plugin.IsNoiseLine(e.Data)) return;
            AddLog(e.Data, ConsoleMessageType.Error);
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
                server.RunningPid = 0;

                if (inst.IntentionalStop)
                {
                    RemoveInstanceIfCurrent(server.Id, inst);
                    SetStatus(server, ServerStatus.Stopped);
                    return;
                }

                SetStatus(server, ServerStatus.Error);

                if (!server.KeepOnline && !server.AutoRestart)
                {
                    RemoveInstanceIfCurrent(server.Id, inst);
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
                var generation = _recoveryGenerations.AddOrUpdate(server.Id, 1, (_, current) => current + 1);
                await RecoverUnexpectedExitAsync(server, generation, delaySec);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HighPop] Exited handler error for {server.Id}: {ex.Message}");
            }
        };

        if (_config.OptimizeRamBeforeStart)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            try
            {
                foreach (var p in Process.GetProcesses())
                    try { EmptyWorkingSet(p.Handle); } catch { }
            }
            catch { }
        }

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
        server.LastStarted = DateTime.Now;
        server.RunningPid  = proc.Id;

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Rust output is captured in HighPop, so hide any window the process creates.
        // CreateNoWindow suppresses the console host, while this also handles a window
        // created later by RustDedicated via AllocConsole/CreateWindow.
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
            AddLog(
                inst.JobHandle != nint.Zero
                    ? $"[HighPop] Hard RAM cap active at {server.MaxRamMb:N0} MB. Windows can terminate Rust if this limit is exceeded."
                    : $"[HighPop] Could not apply the configured {server.MaxRamMb:N0} MB RAM cap.",
                inst.JobHandle != nint.Zero ? ConsoleMessageType.Warning : ConsoleMessageType.Error);
        }

        // Schedule daily restart if enabled
        if (server.DailyRestartEnabled)
            _ = RunDailyRestartAsync(server, inst);

        // Register with bandwidth tracking
        _network.RegisterServer(server.Id, proc.Id);

        // Add firewall rules
        if (server.FirewallAutoManage)
            FirewallService.AddRules(server);

        SetStatus(server, ServerStatus.Running);
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
            server.RunningPid = 0;
            SetStatus(server, ServerStatus.Error);
            throw;
        }
    }

    public async Task StopAsync(GameServer server, string reason = "Requested from HighPop")
    {
        CancelRecovery(server.Id);
        var gate = _lifecycleGates.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { await StopCoreAsync(server, reason); }
        finally { gate.Release(); }
    }

    private async Task StopCoreAsync(GameServer server, string reason)
    {
        if (!_running.TryGetValue(server.Id, out var inst))
        {
            // HighPop may have been restarted while this server kept running — fall back
            // to killing the orphaned PID we persisted to disk.
            await KillOrphanedPidAsync(server);
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
            try { await inst.Process.StandardInput.WriteLineAsync(stopCmd); }
            catch { }
            try
            {
                using var gracefulStopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await inst.Process.WaitForExitAsync(gracefulStopCts.Token);
            }
            catch (OperationCanceledException)
            {
                InjectLogLine(server.Id,
                    "[HighPop] Rust did not exit within 30 seconds; forcing the process to close.",
                    ConsoleMessageType.Warning);
            }
        }

        RemoveInstanceIfCurrent(server.Id, inst);
        _network.UnregisterServer(server.Id);
        if (server.FirewallAutoManage) FirewallService.RemoveRules(server);

        if (inst.Process?.HasExited == false)
            inst.Process.Kill(entireProcessTree: true);

        server.RunningPid = 0;
        SetStatus(server, ServerStatus.Stopped);
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
        server.RunningPid = 0;
        if (server.FirewallAutoManage) FirewallService.RemoveRules(server);
        SetStatus(server, ServerStatus.Stopped);
        return Task.CompletedTask;
    }

    public async Task SendCommandAsync(string serverId, string command)
    {
        if (!_running.TryGetValue(serverId, out var inst)) return;
        if (inst.Process != null)
            await inst.Process.StandardInput.WriteLineAsync(command);
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

    public Task KillAsync(GameServer server)
    {
        CancelRecovery(server.Id);
        if (!_running.TryGetValue(server.Id, out var inst)) return KillOrphanedPidAsync(server);
        SetStatus(server, ServerStatus.Stopping);
        inst.IntentionalStop = true;
        inst.DailyRestartCts.Cancel();
        JobObjectService.ReleaseJob(inst.JobHandle);
        inst.JobHandle = nint.Zero;
        _running.TryRemove(server.Id, out _);
        _network.UnregisterServer(server.Id);
        if (server.FirewallAutoManage) FirewallService.RemoveRules(server);
        try { inst.Process?.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* process already dead — swallow */ }
        server.RunningPid = 0;
        SetStatus(server, ServerStatus.Stopped);
        return Task.CompletedTask;
    }

    public void KillAll()
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
                JobObjectService.ReleaseJob(instance.JobHandle);
                instance.JobHandle = nint.Zero;

                // An always-on Rust process is deliberately detached from HighPop so closing
                // or updating the manager does not create game-server downtime. RunningPid is
                // retained and TryReattach restores management on the next launch.
                if (instance.Server.KeepOnline && instance.Process?.HasExited == false)
                    continue;

                instance.Process?.Kill(entireProcessTree: true);
                instance.Server.RunningPid = 0;
            }
            catch { }
        }
        _running.Clear();
    }

    private void CancelRecovery(string serverId)
        => _recoveryGenerations.AddOrUpdate(serverId, 1, (_, current) => current + 1);

    public void QueueAlwaysOnRecovery(GameServer server, string reason, int initialDelaySeconds = 10)
    {
        if (!server.KeepOnline || IsRunning(server.Id)) return;
        var generation = _recoveryGenerations.AddOrUpdate(server.Id, 1, (_, current) => current + 1);
        InjectLogLine(
            server.Id,
            $"[HighPop] Always-on recovery queued after {reason}.",
            ConsoleMessageType.Warning);
        _ = RecoverUnexpectedExitAsync(server, generation, initialDelaySeconds);
    }

    private async Task RecoverUnexpectedExitAsync(GameServer server, long generation, int initialDelaySeconds)
    {
        var delay = TimeSpan.FromSeconds(Math.Clamp(initialDelaySeconds, 1, 300));
        while (_recoveryGenerations.TryGetValue(server.Id, out var current)
               && current == generation
               && (server.KeepOnline || server.AutoRestart))
        {
            await Task.Delay(delay);
            if (!_recoveryGenerations.TryGetValue(server.Id, out current) || current != generation)
                return;

            try
            {
                await StartAsync(server);
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
                await Task.Delay(500);
                try
                {
                    proc.Refresh();
                    var hwnd = proc.MainWindowHandle;
                    if (hwnd != IntPtr.Zero) { ShowWindow(hwnd, showCmd); return; }
                }
                catch { return; }
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
                await StopAsync(server, "Scheduled daily restart");
                await Task.Delay(3000);
                await StartAsync(server);
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
        if (!IsCurrentInstance(server.Id, inst) || !inst.TryMarkReady(source)) return;
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

    private bool RemoveInstanceIfCurrent(string serverId, ServerInstance instance)
        => ((ICollection<KeyValuePair<string, ServerInstance>>)_running)
            .Remove(new KeyValuePair<string, ServerInstance>(serverId, instance));

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

    private static ConsoleMessageType DetectType(string line)
    {
        var l = line.ToLowerInvariant();
        if (l.Contains("error") || l.Contains("exception") || l.Contains("fatal")) return ConsoleMessageType.Error;
        if (l.Contains("warn")) return ConsoleMessageType.Warning;
        return ConsoleMessageType.Info;
    }
}
