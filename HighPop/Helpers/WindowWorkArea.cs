using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HighPop.Helpers;

/// <summary>
/// Makes custom-chrome WPF windows maximize to the monitor work area. Without
/// WM_GETMINMAXINFO handling, WindowChrome can extend behind the Windows taskbar.
/// </summary>
internal static class WindowWorkArea
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.AddHook(WindowProc);
        };
    }

    private static nint WindowProc(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return nint.Zero;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == nint.Zero) return nint.Zero;

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return nint.Zero;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = monitorInfo.WorkArea;
        var bounds = monitorInfo.Monitor;

        minMax.MaxPosition.X = Math.Abs(work.Left - bounds.Left);
        minMax.MaxPosition.Y = Math.Abs(work.Top - bounds.Top);
        minMax.MaxSize.X = Math.Abs(work.Right - work.Left);
        minMax.MaxSize.Y = Math.Abs(work.Bottom - work.Top);
        minMax.MaxTrackSize = minMax.MaxSize;

        Marshal.StructureToPtr(minMax, lParam, fDeleteOld: true);
        handled = true;
        return nint.Zero;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
