using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HighPop.Desktop.Views;

public partial class OnboardingWindow : Window
{
    public OnboardingWindow() => InitializeComponent();

    private void GotIt_Click(object? sender, RoutedEventArgs e) =>
        Close(DontShowAgainCheck.IsChecked == true);

    /// <summary>Shows the welcome dialog and returns true when "Don't show this again" was ticked.</summary>
    public static async Task<bool> ShowAsync(Window owner) =>
        await new OnboardingWindow().ShowDialog<bool>(owner);
}
