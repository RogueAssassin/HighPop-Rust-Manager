using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using HighPop.ViewModels;

namespace HighPop.Views;

public partial class ServerDetailView : System.Windows.Controls.UserControl
{
    private bool _autoScroll = true;
    private INotifyCollectionChanged? _hookedLog;

    public ServerDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Unhook previous log
        if (_hookedLog != null)
        {
            _hookedLog.CollectionChanged -= OnLogChanged;
            _hookedLog = null;
        }

        if (e.NewValue is ServerViewModel vm)
        {
            _hookedLog = vm.FilteredLog;
            _hookedLog.CollectionChanged += OnLogChanged;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_autoScroll && DataContext is ServerViewModel vm && vm.FilteredLog.Count > 0)
            Dispatcher.BeginInvoke(() => LogList?.ScrollIntoView(vm.FilteredLog[^1]));
    }

    private void AutoScrollToggle_Changed(object sender, RoutedEventArgs e)
    {
        _autoScroll = AutoScrollToggle.IsChecked == true;
        if (_autoScroll && DataContext is ServerViewModel vm && vm.FilteredLog.Count > 0)
            LogList?.ScrollIntoView(vm.FilteredLog[^1]);
    }

    private void AddScheduleTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ServerViewModel vm) return;

        var dlg = new AddScheduleTaskDialog(vm.Server.QuickCommands) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        vm.AddScheduledTaskCommand.Execute(dlg.Result);
    }

}
