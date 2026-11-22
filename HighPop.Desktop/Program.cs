using System.Runtime.InteropServices;
using Avalonia;

namespace HighPop.Desktop;

internal static class Program
{
    // Held for the life of the process so a second copy can see this one is running.
    private static Mutex? _singleInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        // Same name the WPF app used, so the two builds never run (and manage the same servers) together.
        if (OperatingSystem.IsWindows() && !AcquireSingleInstance())
        {
            MessageBoxW(IntPtr.Zero,
                "HighPop Rust Manager is already running. Check your system tray.",
                "HighPop already running", 0x40);
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static bool AcquireSingleInstance()
    {
        _singleInstance = new Mutex(true, "HighPop_Rust_Manager_SingleInstance", out var createdNew);
        return createdNew;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
