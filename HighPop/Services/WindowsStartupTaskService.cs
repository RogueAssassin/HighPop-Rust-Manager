using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HighPop.Services;

/// <summary>
/// Manages HighPop's per-user Windows logon task. Task Scheduler is more resilient than the
/// legacy Run-key entry and starts the manager directly in background/tray mode.
/// </summary>
public static class WindowsStartupTaskService
{
    public const string TaskName = "HighPop Rust Manager";
    private const string LegacyRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValue = "HighPop";

    public static string BuildTaskAction(string executablePath) =>
        $"\"{executablePath}\" --background";

    public static bool IsEnabled()
    {
        if (RunSchtasks(["/Query", "/TN", TaskName], out _)) return true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, false);
            return key?.GetValue(LegacyRunValue) != null;
        }
        catch { return false; }
    }

    public static bool SetEnabled(bool enabled, out string error)
    {
        error = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            error = "Windows Task Scheduler is only available on Windows.";
            return false;
        }

        var success = enabled ? CreateTask(out error) : DeleteTask(out error);
        if (!success) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, true);
            key?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
        }
        catch { }
        return true;
    }

    private static bool CreateTask(out string error)
    {
        var executable = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "HighPop.exe");
        return RunSchtasks([
            "/Create", "/TN", TaskName,
            "/SC", "ONLOGON",
            "/TR", BuildTaskAction(executable),
            "/RL", "LIMITED",
            "/F",
        ], out error);
    }

    private static bool DeleteTask(out string error)
    {
        if (!RunSchtasks(["/Query", "/TN", TaskName], out _))
        {
            error = string.Empty;
            return true;
        }
        return RunSchtasks(["/Delete", "/TN", TaskName, "/F"], out error);
    }

    private static bool RunSchtasks(IEnumerable<string> arguments, out string error)
    {
        error = string.Empty;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { }
                error = "Windows Task Scheduler did not respond within 10 seconds.";
                return false;
            }
            if (process.ExitCode == 0) return true;
            error = process.StandardError.ReadToEnd().Trim();
            if (string.IsNullOrWhiteSpace(error))
                error = process.StandardOutput.ReadToEnd().Trim();
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
