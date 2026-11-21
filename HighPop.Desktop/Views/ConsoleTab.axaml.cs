using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using HighPop.ViewModels;

namespace HighPop.Desktop.Views;

public partial class ConsoleTab : UserControl
{
    private ServerViewModel? _server;

    public ConsoleTab()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    /// <summary>Keeps the newest line in view unless the operator paused the console.</summary>
    private void Attach()
    {
        if (_server != null) _server.FilteredLog.CollectionChanged -= OnLogChanged;
        _server = DataContext as ServerViewModel;
        if (_server != null)
        {
            _server.FilteredLog.CollectionChanged += OnLogChanged;
            ScrollToEnd();
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _server is { IsConsolePaused: false }
            && this.FindControl<ToggleButton>("AutoScrollToggle")?.IsChecked != false)
            ScrollToEnd();
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        var list = this.FindControl<ListBox>("LogList");
        if (list?.ItemCount > 0)
            list.ScrollIntoView(list.ItemCount - 1);
    }, DispatcherPriority.Background);
}
