using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace HighPop.Helpers;

/// <summary>
/// HighPop usually sits minimised or in the tray on a game-server box. Once the window has been out
/// of sight for a short while, compact the managed heap and hand the process's idle pages back to
/// Windows so RAM goes to Rust instead. Pages fault back in cheaply when the window is reopened.
/// </summary>
internal static class BackgroundMemoryTrim
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);

    public static void Attach(Window window)
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, window.Dispatcher) { Interval = Delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsOutOfSight(window)) Trim();
        };

        void Schedule()
        {
            timer.Stop();
            if (IsOutOfSight(window)) timer.Start();
        }

        window.StateChanged += (_, _) => Schedule();
        window.IsVisibleChanged += (_, _) => Schedule();
    }

    private static bool IsOutOfSight(Window window) =>
        !window.IsVisible || window.WindowState == WindowState.Minimized;

    private static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            EmptyWorkingSet(GetCurrentProcess());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            // Trimming is only an optimisation; never let it affect the app.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
