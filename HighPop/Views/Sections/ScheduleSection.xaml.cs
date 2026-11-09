using System.Windows;
using HighPop.ViewModels;

namespace HighPop.Views.Sections;

public partial class ScheduleSection : System.Windows.Controls.UserControl
{
    public ScheduleSection() => InitializeComponent();

    private void AddScheduleTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ServerViewModel vm) return;

        var dlg = new AddScheduleTaskDialog(vm.Server.QuickCommands) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        vm.AddScheduledTaskCommand.Execute(dlg.Result);
    }
}
