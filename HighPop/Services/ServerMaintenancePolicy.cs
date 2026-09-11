namespace HighPop.Services;

/// <summary>
/// Pure maintenance-policy helpers shared by the UI and smoke tests. Keeping the timing
/// rules here makes update countdowns and stop bounds deterministic and reviewable.
/// </summary>
public static class ServerMaintenancePolicy
{
    public static int ClampGracefulStopTimeout(int seconds) => Math.Clamp(seconds, 15, 600);

    public static int ClampUpdateWarningMinutes(int minutes) => Math.Clamp(minutes, 1, 30);

    /// <summary>
    /// Returns descending notification checkpoints in seconds. The first item is emitted
    /// immediately; callers wait the delta between subsequent items.
    /// </summary>
    public static IReadOnlyList<int> GetUpdateCountdownSeconds(int warningMinutes)
    {
        var totalSeconds = ClampUpdateWarningMinutes(warningMinutes) * 60;
        var checkpoints = new[] { totalSeconds, 15 * 60, 10 * 60, 5 * 60, 3 * 60, 60, 30, 10 }
            .Where(seconds => seconds <= totalSeconds)
            .Distinct()
            .OrderByDescending(seconds => seconds)
            .ToList();
        return checkpoints;
    }

    public static string FormatCountdown(int seconds) => seconds >= 60
        ? $"{seconds / 60} minute{(seconds == 60 ? string.Empty : "s")}"
        : $"{seconds} seconds";
}
