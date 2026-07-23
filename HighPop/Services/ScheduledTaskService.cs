using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace HighPop.Services;

public enum ScheduledActionType { Restart, Stop, Start, Backup, Update, QuickCommand, Wipe, WipeMap, Broadcast }
public enum ScheduleFrequency   { Once, Daily, Weekly, Interval }

public class ScheduledTask
{
    public string Id          { get; set; } = Guid.NewGuid().ToString();
    public string ServerId    { get; set; } = string.Empty;
    public string ServerName  { get; set; } = string.Empty;
    public ScheduledActionType Action    { get; set; }
    public ScheduleFrequency   Frequency { get; set; }
    public TimeSpan            TimeOfDay { get; set; }
    public DayOfWeek           DayOfWeek { get; set; }
    /// <summary>Used when Frequency == Interval — repeat every N minutes.</summary>
    public int    IntervalMinutes { get; set; } = 60;
    /// <summary>Console command to send when Action == QuickCommand.</summary>
    public string Command     { get; set; } = string.Empty;
    public bool   IsEnabled   { get; set; } = true;
    public DateTime? LastRun  { get; set; }
    public DateTime? NextRun  { get; set; }
    public string LastResult { get; set; } = "Never run";
    public int ConsecutiveFailures { get; set; }
    public long LastDurationMilliseconds { get; set; }
    [JsonIgnore] public bool IsRunning { get; set; }

    public string ActionText => Action switch
    {
        ScheduledActionType.Restart      => "Restart",
        ScheduledActionType.Stop         => "Stop",
        ScheduledActionType.Start        => "Start",
        ScheduledActionType.Backup       => "Backup",
        ScheduledActionType.Update       => "Update",
        ScheduledActionType.QuickCommand => $"Command: {Command}",
        ScheduledActionType.Wipe         => "Wipe (full)",
        ScheduledActionType.WipeMap      => "Wipe (map only)",
        ScheduledActionType.Broadcast    => $"Broadcast: {Command}",
        _ => Action.ToString()
    };

    public string FrequencyText => Frequency switch
    {
        ScheduleFrequency.Daily    => $"Daily {TimeOfDay:hh\\:mm}",
        ScheduleFrequency.Weekly   => $"{DayOfWeek} {TimeOfDay:hh\\:mm}",
        ScheduleFrequency.Once     => $"Once {NextRun:dd.MM HH:mm}",
        ScheduleFrequency.Interval => IntervalMinutes % 60 == 0
            ? $"Every {IntervalMinutes / 60}h"
            : $"Every {IntervalMinutes}min",
        _ => Frequency.ToString()
    };
}

public class ScheduledTaskService : IDisposable
{
    private readonly ConfigService _config;
    private readonly ServerManagerService _manager;
    private readonly BackupService _backup;
    private readonly NotificationService _notifications;
    private readonly System.Timers.Timer _timer;
    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private List<ScheduledTask> _tasks = [];
    private readonly string _file;
    private int _checkRunning; // Interlocked flag — prevents concurrent CheckTasksAsync runs
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _serverGates = new();
    private readonly ConcurrentDictionary<string, byte> _activeTasks = new();

    public event Action<ScheduledTask, string>? TaskExecuted;
    public DateTime? LastCheckAt { get; private set; }
    public string LastCheckResult { get; private set; } = "Waiting for first scheduler check";

    /// <summary>Wired by MainViewModel to return live in-memory server objects.</summary>
    public Func<IEnumerable<Models.GameServer>>? GetServers { get; set; }

    /// <summary>Wired by MainViewModel to trigger a server update via its ViewModel command.</summary>
    public Func<string, Task>? UpdateServer { get; set; }

    public ScheduledTaskService(ConfigService config, ServerManagerService manager, BackupService backup,
                                NotificationService notifications)
    {
        _config        = config;
        _manager       = manager;
        _backup        = backup;
        _notifications = notifications;
        _file          = System.IO.Path.Combine(config.AppDataPath, "scheduled_tasks.json");

        Load();
        _timer = new System.Timers.Timer(30_000); // check every 30s
        _timer.Elapsed += OnTimerElapsed;
        _timer.Start();
    }

    private async void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try { await CheckTasksAsync(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Scheduler] Timer check failed: {ex}");
        }
    }

    public IReadOnlyList<ScheduledTask> Tasks
    {
        get { lock (_lock) { return _tasks.ToList(); } }
    }

    public void AddTask(ScheduledTask task)
    {
        task.NextRun = ComputeNextRun(task);
        List<ScheduledTask> snapshot;
        lock (_lock) { _tasks.Add(task); snapshot = [.. _tasks]; }
        SaveSnapshot(snapshot);
    }

    public void RemoveTask(string id)
    {
        List<ScheduledTask> snapshot;
        lock (_lock) { _tasks.RemoveAll(t => t.Id == id); snapshot = [.. _tasks]; }
        SaveSnapshot(snapshot);
    }

    public void UpdateTask(ScheduledTask task)
    {
        List<ScheduledTask>? snapshot = null;
        lock (_lock)
        {
            var idx = _tasks.FindIndex(t => t.Id == task.Id);
            if (idx >= 0) { _tasks[idx] = task; snapshot = [.. _tasks]; }
        }
        if (snapshot != null) SaveSnapshot(snapshot);
    }

    public async Task ExecuteNowAsync(string taskId)
    {
        ScheduledTask? task;
        lock (_lock) { task = _tasks.FirstOrDefault(t => t.Id == taskId); }
        if (task == null) return;
        await ExecuteTaskAsync(task);
        List<ScheduledTask> snapshot;
        lock (_lock)
        {
            task.LastRun = DateTime.Now;
            snapshot = [.. _tasks];
        }
        SaveSnapshot(snapshot);
    }

    private async Task CheckTasksAsync()
    {
        if (Interlocked.CompareExchange(ref _checkRunning, 1, 0) != 0) return;
        try
        {
            await CheckTasksCoreAsync();
            LastCheckAt = DateTime.Now;
            LastCheckResult = "Scheduler healthy";
        }
        catch (Exception ex)
        {
            LastCheckAt = DateTime.Now;
            LastCheckResult = $"Scheduler check failed: {ex.Message}";
            throw;
        }
        finally { Interlocked.Exchange(ref _checkRunning, 0); }
    }

    private async Task CheckTasksCoreAsync()
    {
        var now = DateTime.Now;
        List<ScheduledTask> due;
        lock (_lock)
        {
            due = _tasks.Where(t => t.IsEnabled && t.NextRun <= now).ToList();
        }

        if (due.Count == 0) return;

        // Run concurrently — a Restart task now waits ~60s to warn players first, and
        // that must not delay other due tasks (e.g. other servers' restarts/backups).
        await Task.WhenAll(due.Select(ExecuteTaskAsync));

        List<ScheduledTask> snapshot;
        lock (_lock)
        {
            foreach (var task in due)
            {
                task.LastRun = now;
                task.NextRun = task.Frequency == ScheduleFrequency.Once
                    ? null
                    : ComputeNextRun(task);
                if (task.NextRun == null) task.IsEnabled = false;
            }
            snapshot = [.. _tasks];
        }
        SaveSnapshot(snapshot);
    }

    private async Task ExecuteTaskAsync(ScheduledTask task)
    {
        if (!_activeTasks.TryAdd(task.Id, 0))
        {
            task.LastResult = "Skipped — this task is already running";
            PublishTaskExecuted(task, task.LastResult);
            return;
        }

        var startedAt = System.Diagnostics.Stopwatch.StartNew();
        Models.GameServer? server = null;
        SemaphoreSlim? gate = null;
        var gateEntered = false;
        task.IsRunning = true;

        try
        {
            server = GetServer(task.ServerId)
                ?? throw new InvalidOperationException("server profile not found");
            gate = _serverGates.GetOrAdd(task.ServerId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            gateEntered = true;

            switch (task.Action)
            {
                case ScheduledActionType.Restart:
                    if (_manager.IsRunning(server.Id))
                    {
                        await _manager.WarnPlayersAsync(server, "Server restarting in 1 minute");
                        await Task.Delay(60_000);
                        await _manager.SendCommandAsync(server.Id, "server.save");
                        await Task.Delay(1000);
                        await _manager.StopAsync(server, $"Scheduled task \"{task.ActionText}\"");
                        if (server.BackupOnShutdown) await _backup.CreateBackupAsync(server);
                        await Task.Delay(3000);
                    }
                    await _manager.StartAsync(server);
                    break;
                case ScheduledActionType.Stop:
                    if (!_manager.IsRunning(server.Id))
                    {
                        task.LastResult = "Skipped — server is already stopped";
                        PublishTaskExecuted(task, task.LastResult);
                        return;
                    }
                    await _manager.StopAsync(server, $"Scheduled task \"{task.ActionText}\"");
                    if (server.BackupOnShutdown) await _backup.CreateBackupAsync(server);
                    break;
                case ScheduledActionType.Start:
                    if (_manager.IsRunning(server.Id))
                    {
                        task.LastResult = "Skipped — server is already running";
                        PublishTaskExecuted(task, task.LastResult);
                        return;
                    }
                    await _manager.StartAsync(server);
                    break;
                case ScheduledActionType.Backup:
                    if (!_manager.IsRunning(server.Id) &&
                        (server.LastStarted == null || server.LastStarted < DateTime.Now.AddHours(-24)))
                    {
                        task.LastResult = "Skipped — server hasn't run in the last 24h, nothing new to back up";
                        PublishTaskExecuted(task, task.LastResult);
                        return;
                    }
                    await _backup.CreateBackupAsync(server);
                    break;
                case ScheduledActionType.Update:
                    if (UpdateServer == null)
                        throw new InvalidOperationException("update handler is not available");
                    await UpdateServer(task.ServerId);
                    break;
                case ScheduledActionType.QuickCommand:
                    if (!_manager.IsRunning(server.Id))
                    {
                        task.LastResult = "Skipped — server is stopped";
                        PublishTaskExecuted(task, task.LastResult);
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(task.Command))
                        await _manager.SendCommandAsync(server.Id, task.Command);
                    break;
                case ScheduledActionType.Wipe:
                case ScheduledActionType.WipeMap:
                    if (!await ExecuteWipeAsync(server, task.Action == ScheduledActionType.Wipe))
                    {
                        task.LastResult = "Skipped — this game does not support scheduled wipes";
                        PublishTaskExecuted(task, task.LastResult);
                        return;
                    }
                    break;
                case ScheduledActionType.Broadcast:
                    if (!_manager.IsRunning(server.Id))
                    {
                        task.LastResult = "Skipped — server is stopped";
                        PublishTaskExecuted(task, task.LastResult);
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(task.Command))
                    {
                        var bPlugin = Games.GameRegistry.All.FirstOrDefault(p => p.GameId == server.GameId);
                        var bcmd    = bPlugin?.GetBroadcastCommand(task.Command);
                        if (bcmd != null)
                            await _manager.SendCommandAsync(server.Id, bcmd);
                        else
                        {
                            task.LastResult = "Skipped — this game does not support in-game broadcasts";
                            PublishTaskExecuted(task, task.LastResult);
                            return;
                        }
                    }
                    break;
            }
            task.LastResult = $"OK — {DateTime.Now:g}";
            task.ConsecutiveFailures = 0;
            PublishTaskExecuted(task, task.LastResult);
        }
        catch (Exception ex)
        {
            task.LastResult = $"Failed — {ex.Message}";
            task.ConsecutiveFailures++;
            PublishTaskExecuted(task, task.LastResult);
            if (server?.KeepOnline == true
                && (task.Action is ScheduledActionType.Start
                    or ScheduledActionType.Restart
                    or ScheduledActionType.Update
                    or ScheduledActionType.Wipe
                    or ScheduledActionType.WipeMap)
                && !_manager.IsRunning(server.Id))
            {
                _manager.QueueAlwaysOnRecovery(server, $"scheduled {task.Action} failure");
            }
        }
        finally
        {
            startedAt.Stop();
            task.LastDurationMilliseconds = startedAt.ElapsedMilliseconds;
            task.IsRunning = false;
            if (gateEntered) gate?.Release();
            _activeTasks.TryRemove(task.Id, out _);
        }
    }

    private void PublishTaskExecuted(ScheduledTask task, string result)
    {
        task.LastRun = DateTime.Now;
        var handlers = TaskExecuted?.GetInvocationList();
        if (handlers == null) return;
        foreach (var handler in handlers)
        {
            try { ((Action<ScheduledTask, string>)handler)(task, result); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Scheduler] TaskExecuted subscriber failed: {ex}");
            }
        }
    }

    // Returns false if the game doesn't support wipes (caller reports the skip).
    private async Task<bool> ExecuteWipeAsync(Models.GameServer server, bool fullWipe)
    {
        var plugin = Games.GameRegistry.All.FirstOrDefault(p => p.GameId == server.GameId);
        if (plugin is not Games.IWipePlugin wipePlugin)
            return false;

        // Warn players and stop the server (skip warning if already stopped)
        if (_manager.IsRunning(server.Id))
        {
            await _manager.WarnPlayersAsync(server, "Server wiping in 2 minutes — all world data will be reset");
            await Task.Delay(120_000);
            await _manager.StopAsync(server, fullWipe ? "Scheduled full wipe" : "Scheduled map wipe");
            await Task.Delay(3000);
        }

        // Wipes are destructive. A successful restorable backup is a hard precondition,
        // even when the server's normal backup toggle is off.
        await _backup.CreateBackupAsync(server);
        _manager.InjectLogLine(server.Id, "[Wipe] Safety backup completed.",
            Models.ConsoleMessageType.System);

        // Collect and delete wipe paths (glob-resolved)
        var patterns = fullWipe
            ? wipePlugin.GetFullWipePaths(server)
            : wipePlugin.GetMapWipePaths(server);

        int deleted = 0;
        var errors  = new System.Text.StringBuilder();
        var installRoot = System.IO.Path.GetFullPath(server.InstallPath)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;

        foreach (var pattern in patterns)
        {
            var absPattern = System.IO.Path.GetFullPath(System.IO.Path.Combine(server.InstallPath, pattern));
            if (!absPattern.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
            {
                errors.AppendLine($"Rejected unsafe wipe path: {pattern}");
                continue;
            }
            var dir        = System.IO.Path.GetDirectoryName(absPattern) ?? server.InstallPath;
            var glob       = System.IO.Path.GetFileName(absPattern);

            if (!System.IO.Directory.Exists(dir)) continue;

            // Delete matching files
            foreach (var file in System.IO.Directory.GetFiles(dir, glob))
            {
                try { System.IO.File.Delete(file); deleted++; }
                catch (Exception ex) { errors.AppendLine(ex.Message); }
            }

            // Delete matching directories (e.g. storage_1/*)
            if (glob == "*")
            {
                foreach (var sub in System.IO.Directory.GetDirectories(dir))
                {
                    try { System.IO.Directory.Delete(sub, recursive: true); deleted++; }
                    catch (Exception ex) { errors.AppendLine(ex.Message); }
                }
            }
        }

        var label = fullWipe ? "Full wipe" : "Map wipe";
        _manager.InjectLogLine(server.Id,
            $"[Wipe] {label} complete — {deleted} item(s) removed" +
            (errors.Length > 0 ? $" (errors: {errors})" : ""),
            Models.ConsoleMessageType.Warning);

        await _notifications.NotifyAsync(
            $"🗑 {label} — {server.DisplayName}",
            $"{label} complete on **{server.DisplayName}** — {deleted} item(s) removed. Restarting now.",
            "#D29922");

        await Task.Delay(2000);
        await _manager.StartAsync(server);
        return true;
    }

    private Models.GameServer? GetServer(string id)
        => GetServers?.Invoke().FirstOrDefault(s => s.Id == id)
           ?? _config.LoadServers().FirstOrDefault(s => s.Id == id);

    public static DateTime ComputeNextRun(ScheduledTask task, DateTime? from = null)
    {
        var now   = from ?? DateTime.Now;
        var today = now.Date + task.TimeOfDay;

        return task.Frequency switch
        {
            ScheduleFrequency.Daily    => today > now ? today : today.AddDays(1),
            ScheduleFrequency.Weekly   =>
                Enumerable.Range(0, 8)
                    .Select(d => today.AddDays(d))
                    .First(d => d.DayOfWeek == task.DayOfWeek && d > now),
            ScheduleFrequency.Interval => (task.LastRun ?? now).AddMinutes(Math.Max(1, task.IntervalMinutes)),
            ScheduleFrequency.Once     => today > now ? today : today.AddDays(1),
            _ => now.AddMinutes(1),
        };
    }

    private void SaveSnapshot(List<ScheduledTask> snapshot)
    {
        lock (_saveLock)
        {
            var temp = _file + ".tmp";
            System.IO.File.WriteAllText(temp, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
            System.IO.File.Move(temp, _file, overwrite: true);
        }
    }

    private void Load()
    {
        if (!System.IO.File.Exists(_file)) return;
        try
        {
            _tasks = JsonConvert.DeserializeObject<List<ScheduledTask>>(System.IO.File.ReadAllText(_file)) ?? [];
            var changed = false;
            foreach (var task in _tasks.Where(t => t.IsEnabled))
            {
                if (task.Frequency == ScheduleFrequency.Once && task.NextRun == null)
                {
                    task.IsEnabled = false;
                    task.LastResult = "Disabled — one-time task had no run time";
                    changed = true;
                }
                else if (task.Frequency != ScheduleFrequency.Once && task.NextRun == null)
                {
                    task.NextRun = ComputeNextRun(task);
                    changed = true;
                }
            }
            if (changed) SaveSnapshot([.. _tasks]);
        }
        catch (Exception ex)
        {
            LastCheckResult = $"Schedule file could not be loaded: {ex.Message}";
            try
            {
                var quarantine = _file + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                System.IO.File.Copy(_file, quarantine, overwrite: false);
            }
            catch { }
            _tasks = [];
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Elapsed -= OnTimerElapsed;
        _timer.Dispose();
        // A restart/wipe can still be finishing its warning delay while the app closes.
        // Dropping references is safer than disposing semaphores underneath active waiters.
        _serverGates.Clear();
        _activeTasks.Clear();
    }
}
