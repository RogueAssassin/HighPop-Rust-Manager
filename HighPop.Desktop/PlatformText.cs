namespace HighPop.Desktop;

/// <summary>Wording that differs between Windows and Linux, so one screen reads correctly on both.</summary>
public static class PlatformText
{
    public static string Firewall { get; } = OperatingSystem.IsWindows()
        ? "Automatically manage Windows Firewall rules when this server starts"
        : "Automatically manage firewall rules when this server starts (Windows only)";

    public static string FirewallNote { get; } = OperatingSystem.IsWindows()
        ? "Requires HighPop to run as Administrator. Disable this if Group Policy or another firewall product manages the ports."
        : "On Linux open the ports in ufw or your firewall yourself; HighPop does not change firewall rules here.";

    public static string RamLimitNote { get; } = OperatingSystem.IsWindows()
        ? "Hard Windows process limit. If Rust exceeds it, Windows can terminate the server. Use 0 for unlimited."
        : "Windows enforces this limit; on Linux set a cgroup memory limit instead. Use 0 for unlimited.";
}