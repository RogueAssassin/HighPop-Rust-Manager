using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HighPop.ViewModels;

namespace HighPop.Desktop.Views;

public partial class PlayersTab : UserControl
{
    private ServerViewModel? _server;

    public PlayersTab()
    {
        InitializeComponent();
        // Follow the chat only while shown: the view model outlives this control.
        DataContextChanged += (_, _) => Hook(DataContext as ServerViewModel);
        AttachedToVisualTree += (_, _) => Hook(DataContext as ServerViewModel);
        DetachedFromVisualTree += (_, _) => Hook(null);
    }

    private void Hook(ServerViewModel? vm)
    {
        if (_server != null) _server.ChatLog.CollectionChanged -= OnChatChanged;
        _server = vm;
        if (_server != null) _server.ChatLog.CollectionChanged += OnChatChanged;
    }

    private void OnChatChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<ListBox>("ChatList") is { ItemCount: > 0 } list)
                list.ScrollIntoView(list.ItemCount - 1);
        }, DispatcherPriority.Background);
    }

    private void GiveAmountPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string amount } && DataContext is ServerViewModel vm)
            vm.Players.GiveAmount = amount;
    }
}
