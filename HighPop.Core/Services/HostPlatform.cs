namespace HighPop.Services;

/// <summary>
/// Operating-system specific names in one place, so services do not each repeat an
/// <see cref="OperatingSystem"/> check for a file name.
/// </summary>
public static class HostPlatform
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>The Rust dedicated server binary: <c>RustDedicated.exe</c> or <c>RustDedicated</c>.</summary>
    public static string RustExecutableName => IsWindows ? "RustDedicated.exe" : "RustDedicated";

    public static string SteamCmdExecutableName => IsWindows ? "steamcmd.exe" : "steamcmd.sh";

    /// <summary>The archive Valve publishes for this OS.</summary>
    public static string SteamCmdArchiveName => IsWindows ? "steamcmd.zip" : "steamcmd_linux.tar.gz";

    public const string SteamCmdDownloadBase = "https://steamcdn-a.akamaihd.net/client/installer/";
}