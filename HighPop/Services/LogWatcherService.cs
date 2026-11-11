using System.Collections.Concurrent;
using HighPop.Models;

namespace HighPop.Services;

public class LogWatcherService
{
    private readonly ServerManagerService _manager;
    private readonly NotificationService  _notifications;

    // serverId -> (keyword -> last trigger time)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _lastFired = new();

    public LogWatcherService(ServerManagerService manager, NotificationService notifications)
    {
        _manager       = manager;
        _notifications = notifications;
    }

    public void CheckLine(GameServer server, string line)
    {
        if (string.IsNullOrEmpty(line) || server.LogWatchRules.Count == 0) return;
        // Our own "[LogWatch] ... keyword "X"" line contains the keyword: matching it would
        // re-trigger the rule forever when its cooldown is 0.
        if (line.StartsWith("[LogWatch]", StringComparison.Ordinal)) return;
        // Players must not be able to stop or restart the server by typing a keyword in chat.
        var trimmed = line.TrimStart();
        var isChat = trimmed.StartsWith("[CHAT]", StringComparison.OrdinalIgnoreCase)
                  || trimmed.StartsWith("[TEAM CHAT]", StringComparison.OrdinalIgnoreCase);

        var cooldowns = _lastFired.GetOrAdd(server.Id, _ => new());
        var now = DateTime.Now;

        // Snapshot: the Log Watcher page can add or remove rules while log lines arrive.
        foreach (var rule in server.LogWatchRules.ToArray())
        {
            if (!rule.Enabled) continue;
            if (string.IsNullOrWhiteSpace(rule.Keyword)) continue;
            if (isChat && rule.Action is LogWatchAction.Stop or LogWatchAction.Restart) continue;
            if (!line.Contains(rule.Keyword, StringComparison.OrdinalIgnoreCase)) continue;

            var key = rule.Keyword.ToLowerInvariant();
            var cooldown = TimeSpan.FromMinutes(Math.Max(rule.CooldownMin, 0));
            // Claim the slot atomically so two lines arriving together fire the rule once.
            if (cooldowns.TryGetValue(key, out var last))
            {
                if (now - last < cooldown || !cooldowns.TryUpdate(key, now, last)) continue;
            }
            else if (!cooldowns.TryAdd(key, now)) continue;
            _ = TriggerAsync(server, rule, line).ContinueWith(
                t => _manager.InjectLogLine(server.Id,
                    $"[LogWatch] Action {rule.Action} failed: {t.Exception?.InnerException?.Message ?? t.Exception?.Message}",
                    ConsoleMessageType.Error),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private async Task TriggerAsync(GameServer server, LogWatchRule rule, string line)
    {
        var label = $"[LogWatch] Keyword \"{rule.Keyword}\" matched in {server.DisplayName}: {line.Trim()}";

        switch (rule.Action)
        {
            case LogWatchAction.Restart:
                await _notifications.NotifyAsync($"⚠ Log Watch — {server.DisplayName}",
                    $"Restarting server. Keyword: `{rule.Keyword}`\nLine: `{line.Trim()}`", "#D29922");
                await _manager.StopAsync(server, $"Log watcher restart triggered by \"{rule.Keyword}\"",
                    LifecycleInitiator.LogRule);
                var stoppedGeneration = server.LifecycleGeneration;
                await Task.Delay(3000);
                if (!ServerLifecycleRules.IsCurrent(server, stoppedGeneration)
                    || server.DesiredState != ServerDesiredState.Stopped)
                    break;
                await _manager.StartAsync(server, LifecycleInitiator.LogRule,
                    $"Log watcher restart triggered by \"{rule.Keyword}\"");
                break;

            case LogWatchAction.Stop:
                await _notifications.NotifyAsync($"⚠ Log Watch — {server.DisplayName}",
                    $"Stopping server. Keyword: `{rule.Keyword}`\nLine: `{line.Trim()}`", "#DA3633");
                await _manager.StopAsync(server, $"Log watcher stop triggered by \"{rule.Keyword}\"",
                    LifecycleInitiator.LogRule);
                break;

            case LogWatchAction.SendRcon:
                if (!string.IsNullOrWhiteSpace(rule.RconCommand))
                    await _manager.SendCommandAsync(server.Id, rule.RconCommand);
                break;

            case LogWatchAction.Notify:
                await _notifications.NotifyAsync($"ℹ Log Watch — {server.DisplayName}",
                    $"Keyword: `{rule.Keyword}`\nLine: `{line.Trim()}`", "#A855F7");
                break;
        }

        // Log to server console
        _manager.InjectLogLine(server.Id, $"[LogWatch] Action {rule.Action} triggered by keyword \"{rule.Keyword}\"",
            ConsoleMessageType.System);
    }
}
