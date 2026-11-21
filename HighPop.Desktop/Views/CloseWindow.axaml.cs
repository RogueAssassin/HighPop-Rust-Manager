using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HighPop.Desktop.Views;

public partial class CloseWindow : Window
{
    public enum CloseChoice { Cancel, Minimize, Exit }

    public CloseWindow() => InitializeComponent();

    private void Minimize_Click(object? sender, RoutedEventArgs e) => Close(CloseChoice.Minimize);
    private void Exit_Click(object? sender, RoutedEventArgs e) => Close(CloseChoice.Exit);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(CloseChoice.Cancel);

    public static async Task<CloseChoice> ShowAsync(Window owner) =>
        await new CloseWindow().ShowDialog<CloseChoice>(owner);
}
