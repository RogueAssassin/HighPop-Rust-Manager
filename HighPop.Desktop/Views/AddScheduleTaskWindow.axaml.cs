using Avalonia.Controls;
using Avalonia.Interactivity;
using HighPop.Models;
using HighPop.Services;

namespace HighPop.Desktop.Views;

public partial class AddScheduleTaskWindow : Window
{
    public AddScheduleTaskWindow() => InitializeComponent();

    public static async Task<ScheduledTask?> ShowAsync(Window? owner, IEnumerable<QuickCommand>? quickCommands)
    {
        var window = new AddScheduleTaskWindow
        {
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (quickCommands != null)
        {
            window.CbQuickCommand.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<QuickCommand>(
                (qc, _) => new TextBlock { Text = qc?.Label });
            window.CbQuickCommand.ItemsSource = quickCommands.ToList();
        }
        return owner != null ? await window.ShowDialog<ScheduledTask?>(owner) : null;
    }

    private void CbAction_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (CommandPanel == null || BroadcastPanel == null) return;
        CommandPanel.IsVisible = CbAction.SelectedIndex == 5;
        BroadcastPanel.IsVisible = CbAction.SelectedIndex == 8;
    }

    private void CbQuick_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (CbQuickCommand.SelectedItem is QuickCommand qc) TbCommand.Text = qc.Command;
    }

    private void CbFreq_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (DayPanel == null || IntervalPanel == null || TimePanel == null) return;
        DayPanel.IsVisible = CbFreq.SelectedIndex == 1;
        IntervalPanel.IsVisible = CbFreq.SelectedIndex == 3;
        TimePanel.IsVisible = CbFreq.SelectedIndex != 3;
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        // Combo index to enum: Wipe=6, WipeMap=7 and Broadcast=8 come after QuickCommand=5.
        var action = (ScheduledActionType)CbAction.SelectedIndex;

        if (action == ScheduledActionType.QuickCommand && string.IsNullOrWhiteSpace(TbCommand.Text))
        {
            Fail("Enter a console command to send.");
            return;
        }
        if (action == ScheduledActionType.Broadcast && string.IsNullOrWhiteSpace(TbBroadcast.Text))
        {
            Fail("Enter the message to broadcast.");
            return;
        }

        var freq = CbFreq.SelectedIndex switch
        {
            1 => ScheduleFrequency.Weekly,
            2 => ScheduleFrequency.Once,
            3 => ScheduleFrequency.Interval,
            _ => ScheduleFrequency.Daily,
        };

        var time = TimeSpan.Zero;
        if (freq != ScheduleFrequency.Interval && !TimeSpan.TryParse(TbTime.Text, out time))
        {
            Fail("Invalid time format. Use HH:mm.");
            return;
        }

        var intervalMinutes = 60;
        if (freq == ScheduleFrequency.Interval)
        {
            if (!int.TryParse(TbInterval.Text, out intervalMinutes) || intervalMinutes <= 0)
            {
                Fail("Enter a positive number for the repeat interval.");
                return;
            }
            if (CbIntervalUnit.SelectedIndex == 1) intervalMinutes *= 60;
        }

        // Monday is index 0 (DayOfWeek.Monday = 1); Sunday is index 6 and must map to DayOfWeek.Sunday = 0.
        var day = CbDay.SelectedIndex == 6 ? DayOfWeek.Sunday : (DayOfWeek)(CbDay.SelectedIndex + 1);

        var command = action switch
        {
            ScheduledActionType.QuickCommand => TbCommand.Text!.Trim(),
            ScheduledActionType.Broadcast => TbBroadcast.Text!.Trim(),
            _ => string.Empty,
        };

        Close(new ScheduledTask
        {
            Action = action,
            Command = command,
            Frequency = freq,
            TimeOfDay = time,
            DayOfWeek = freq == ScheduleFrequency.Weekly ? day : DayOfWeek.Monday,
            IntervalMinutes = intervalMinutes,
            IsEnabled = true,
        });
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
