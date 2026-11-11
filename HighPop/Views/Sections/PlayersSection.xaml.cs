using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using HighPop.ViewModels;

namespace HighPop.Views.Sections;

public partial class PlayersSection : System.Windows.Controls.UserControl
{
    private INotifyCollectionChanged? _hookedChat;

    public PlayersSection()
    {
        InitializeComponent();
        // Hook only while shown: the view model outlives this control.
        Loaded += (_, _) => HookChat(DataContext as ServerViewModel);
        Unloaded += (_, _) => HookChat(null);
        DataContextChanged += (_, e) => { if (IsLoaded) HookChat(e.NewValue as ServerViewModel); };
    }

    private void HookChat(ServerViewModel? vm)
    {
        if (_hookedChat != null) _hookedChat.CollectionChanged -= OnChatChanged;
        _hookedChat = vm?.ChatLog;
        if (_hookedChat != null) _hookedChat.CollectionChanged += OnChatChanged;
    }

    // Chat always follows the newest line; it is short and has no pause control.
    private void OnChatChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (ChatList?.Items.Count > 0) ChatList.ScrollIntoView(ChatList.Items[^1]);
        }, DispatcherPriority.Background);
    }

    private void GiveAmountPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Content: string amount } && DataContext is ServerViewModel vm)
            vm.Players.GiveAmount = amount;
    }
}
