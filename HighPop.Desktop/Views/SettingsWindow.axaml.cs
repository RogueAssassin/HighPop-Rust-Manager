using Avalonia.Controls;
using HighPop.ViewModels;

namespace HighPop.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    public static Task ShowAsync(Window? owner, SettingsViewModel settings, MainViewModel main)
    {
        var window = new SettingsWindow
        {
            DataContext = settings,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };
        // User management lives on the main view model, not the settings one.
        window.UsersSection.DataContext = main;
        return owner != null ? window.ShowDialog(owner) : Task.CompletedTask;
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
