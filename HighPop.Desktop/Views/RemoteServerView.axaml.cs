using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using HighPop.ViewModels;

namespace HighPop.Desktop.Views;

public partial class RemoteServerView : UserControl
{
    private RemoteServerViewModel? _server;

    public RemoteServerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    /// <summary>Follows the newest console line of the selected remote server.</summary>
    private void Attach()
    {
        if (_server != null) _server.Log.CollectionChanged -= OnLogChanged;
        _server = DataContext as RemoteServerViewModel;
        if (_server != null)
        {
            _server.Log.CollectionChanged += OnLogChanged;
            ScrollToEnd();
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) ScrollToEnd();
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        var list = this.FindControl<ListBox>("LogList");
        if (list?.ItemCount > 0)
            list.ScrollIntoView(list.ItemCount - 1);
    }, DispatcherPriority.Background);
}
