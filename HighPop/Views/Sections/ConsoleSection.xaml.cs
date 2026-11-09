using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HighPop.ViewModels;
using Microsoft.Win32;

namespace HighPop.Views.Sections;

/// <summary>
/// Live console. Sections are created when opened and dropped when the user leaves them, so every
/// subscription to the long-lived view model or to static events is made on Loaded and removed on
/// Unloaded; otherwise each visit would leak a copy of this control.
/// </summary>
public partial class ConsoleSection : System.Windows.Controls.UserControl
{
    private bool _autoScroll = true;
    private bool _scrollPending;
    private bool _attached;
    private INotifyCollectionChanged? _hookedLog;
    private ScrollViewer? _logScroller;
    private Window? _hostWindow;

    public ConsoleSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => { if (_attached) HookLog(); };
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) RequestScrollToEnd(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_attached)
        {
            _attached = true;
            // While an RDP session is disconnected or the window is minimised WPF stops rendering,
            // so the console misses its scroll-to-end. Re-render and catch up when the user returns.
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null)
            {
                _hostWindow.Activated += OnHostWindowActivated;
                _hostWindow.StateChanged += OnHostWindowActivated;
            }
        }
        HookLog();
        AttachLogScroller();
        RequestScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_attached) return;
        _attached = false;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_hostWindow != null)
        {
            _hostWindow.Activated -= OnHostWindowActivated;
            _hostWindow.StateChanged -= OnHostWindowActivated;
            _hostWindow = null;
        }
        if (_hookedLog != null)
        {
            _hookedLog.CollectionChanged -= OnLogChanged;
            _hookedLog = null;
        }
        if (_logScroller != null)
        {
            _logScroller.ScrollChanged -= OnLogScrollChanged;
            _logScroller = null;
        }
    }

    private void HookLog()
    {
        var log = (DataContext as ServerViewModel)?.FilteredLog;
        if (ReferenceEquals(log, _hookedLog)) return;
        if (_hookedLog != null) _hookedLog.CollectionChanged -= OnLogChanged;
        _hookedLog = log;
        if (_hookedLog != null) _hookedLog.CollectionChanged += OnLogChanged;
        RequestScrollToEnd();
    }

    private void AttachLogScroller()
    {
        if (_logScroller != null || LogList == null) return;
        LogList.ApplyTemplate();
        _logScroller = FindDescendant<ScrollViewer>(LogList);
        if (_logScroller != null) _logScroller.ScrollChanged += OnLogScrollChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) => RequestScrollToEnd();

    // When the log grows or the panel is re-measured (for example after an RDP reconnect resizes the
    // window), stay pinned to the newest line.
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_autoScroll && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0))
            RequestScrollToEnd();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
            Dispatcher.InvokeAsync(RefreshAfterResume, DispatcherPriority.Background);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(RefreshAfterResume, DispatcherPriority.Background);

    private void OnHostWindowActivated(object? sender, EventArgs e)
    {
        if (_hostWindow?.WindowState != WindowState.Minimized) RequestScrollToEnd();
    }

    private void RefreshAfterResume()
    {
        LogList?.InvalidateMeasure();
        RequestScrollToEnd();
    }

    /// <summary>
    /// Coalesces scroll requests (a burst of 250 lines schedules one scroll) and runs after layout,
    /// so the viewer scrolls to the real end even with virtualised, wrapping rows.
    /// </summary>
    private void RequestScrollToEnd()
    {
        if (!_autoScroll || _scrollPending) return;
        _scrollPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _scrollPending = false;
            if (!_autoScroll || !_attached) return;
            AttachLogScroller();
            _logScroller?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void AutoScrollToggle_Changed(object sender, RoutedEventArgs e)
    {
        _autoScroll = AutoScrollToggle.IsChecked == true;
        RequestScrollToEnd();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
