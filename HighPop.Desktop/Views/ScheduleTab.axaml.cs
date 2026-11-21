using Avalonia.Controls;
using Avalonia.Interactivity;
using HighPop.ViewModels;

namespace HighPop.Desktop.Views;

public partial class ScheduleTab : UserControl
{
    public ScheduleTab() => InitializeComponent();

    private async void AddTask_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ServerViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        var task = await AddScheduleTaskWindow.ShowAsync(owner, vm.Server.QuickCommands);
        if (task != null) vm.Automation.AddScheduledTaskCommand.Execute(task);
    }
}
