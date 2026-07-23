using HighPop.ViewModels;

namespace HighPop.Views;

public partial class SettingsWindow : System.Windows.Window
{
    public SettingsWindow(SettingsViewModel vm, object mainViewModel)
    {
        InitializeComponent();
        DataContext = mainViewModel;
        SettingsViewControl.DataContext = vm;
        StateChanged += (_, _) =>
            MaximizeButton.Content = WindowState == System.Windows.WindowState.Maximized ? "❐" : "☐";
    }

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == System.Windows.WindowState.Maximized
                ? System.Windows.WindowState.Normal
                : System.Windows.WindowState.Maximized;
            return;
        }
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeClick(object sender, System.Windows.RoutedEventArgs e) =>
        WindowState = System.Windows.WindowState.Minimized;

    private void MaximizeClick(object sender, System.Windows.RoutedEventArgs e) =>
        WindowState = WindowState == System.Windows.WindowState.Maximized
            ? System.Windows.WindowState.Normal
            : System.Windows.WindowState.Maximized;

    private void CloseClick(object sender, System.Windows.RoutedEventArgs e) => Close();
}
