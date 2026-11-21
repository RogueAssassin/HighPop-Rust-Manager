using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighPop.Models;
using HighPop.Services;

namespace HighPop.ViewModels;

/// <summary>
/// The Log Watcher and Schedule pages of one server: keyword rules and scheduled tasks.
/// Owned by <see cref="ServerViewModel"/>, which creates it the first time either page
/// needs it. Quick commands stay on the core (the Console page runs them) and the
/// auto-update timer stays with the start/stop lifecycle.
/// </summary>
public partial class ServerAutomationViewModel : BaseViewModel
{
    private readonly ServerViewModel      _owner;
    private readonly ScheduledTaskService _scheduler;

    internal ServerAutomationViewModel(ServerViewModel owner, ScheduledTaskService scheduler)
    {
        _owner     = owner;
        _scheduler = scheduler;
        foreach (var r in Server.LogWatchRules) LogWatchRules.Add(r);
        RefreshScheduledTasks();
    }

    private GameServer Server => _owner.Server;

    // ── Log Watcher rules ────────────────────────────────────────────────────

    [ObservableProperty] private string _newWatchKeyword    = string.Empty;
    [ObservableProperty] private LogWatchAction _newWatchAction = LogWatchAction.Notify;
    [ObservableProperty] private string _newWatchRcon       = string.Empty;
    [ObservableProperty] private int    _newWatchCooldown   = 5;

    public LogWatchAction[] LogWatchActions { get; } = Enum.GetValues<LogWatchAction>();
    public ObservableCollection<LogWatchRule> LogWatchRules { get; } = [];

    [RelayCommand]
    private void AddLogWatchRule()
    {
        if (string.IsNullOrWhiteSpace(NewWatchKeyword)) return;
        var rule = new LogWatchRule
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
    private void RemoveLogWatchRule(LogWatchRule? rule)
    {
        if (rule == null) return;
        LogWatchRules.Remove(rule);
        Server.LogWatchRules.Remove(rule);
    }

    // ── Scheduled tasks ──────────────────────────────────────────────────────

    [ObservableProperty] private List<ScheduledTask> _scheduledTasks = [];

    [RelayCommand]
    internal void RefreshScheduledTasks()
        => ScheduledTasks = _scheduler.Tasks.Where(t => t.ServerId == Server.Id).ToList();

    [RelayCommand]
    private void AddScheduledTask(ScheduledTask? task)
    {
        if (task == null) return;
        task.ServerId   = Server.Id;
        task.ServerName = Server.DisplayName;
        _scheduler.AddTask(task);
        RefreshScheduledTasks();
    }

    [RelayCommand]
    private void RemoveScheduledTask(ScheduledTask? task)
    {
        if (task == null) return;
        _scheduler.RemoveTask(task.Id);
        RefreshScheduledTasks();
    }

    [RelayCommand]
    private async Task RunScheduledTaskNowAsync(ScheduledTask? task)
    {
        if (task == null || task.IsRunning) return;
        _owner.AddActionLog($"Scheduled {task.ActionText} started manually");
        await _scheduler.ExecuteNowAsync(task.Id);
        RefreshScheduledTasks();
    }
}
