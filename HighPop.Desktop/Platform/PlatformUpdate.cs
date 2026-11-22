using HighPop.Services;

namespace HighPop.Desktop.Platform;

/// <summary>Picks the self-updater for the host: the exe swap on Windows, the folder swap on Linux.</summary>
internal static class PlatformUpdate
{
    public static bool IsSupported { get; } = OperatingSystem.IsWindows()
        ? SelfUpdateService.IsSupported()
        : LinuxSelfUpdateService.IsSupported();

    public static Task<bool> DownloadAndPrepareAsync(string url, IProgress<(int pct, string msg)> progress) =>
        OperatingSystem.IsWindows()
            ? SelfUpdateService.DownloadAndPrepareAsync(url, progress)
            : LinuxSelfUpdateService.DownloadAndPrepareAsync(url, progress);

    public static bool StartApply() =>
        OperatingSystem.IsWindows() ? SelfUpdateService.StartApply() : LinuxSelfUpdateService.StartApply();

    /// <summary>Removes files an interrupted update left behind. True on Windows when the last swap never finished.</summary>
    public static bool CleanupLeftovers()
    {
        if (OperatingSystem.IsWindows()) return SelfUpdateService.CleanupLeftovers();
        LinuxSelfUpdateService.CleanupLeftovers();
        return false;
    }
}
