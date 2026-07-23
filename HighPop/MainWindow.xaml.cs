using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using HighPop.ViewModels;

namespace HighPop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Helpers.WindowWorkArea.Attach(this);
        DataContext = App.Services.GetRequiredService<MainViewModel>();
        try
        {
            var sri = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/HighPop;component/assets/brand/highpop.ico"));
            if (sri != null)
                using (sri.Stream)
                    Icon = BitmapFrame.Create(sri.Stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch { }

        Loaded += MainWindow_Loaded;
        StateChanged += (_, _) =>
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "☐";
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        var config = App.Services.GetRequiredService<Services.ConfigService>();
        if (config.HasSeenOnboarding) return;

        var dlg = new Views.OnboardingDialog { Owner = this };
        dlg.ShowDialog();
        if (dlg.DontShowAgain)
        {
            config.HasSeenOnboarding = true;
            config.Save();
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }
        DragMove();
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // Stops the click from bubbling up to TitleBar_MouseDown — DragMove() captures the mouse on
    // button-down and swallows the matching button-up, so UpdateBadge_Click below would otherwise
    // never fire no matter how precisely you click without moving the mouse.
    private void UpdateBadge_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void UpdateBadge_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
            vm.PerformUpdateCommand.Execute(null);
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new HighPop.Views.CloseDialog { Owner = this };
        dlg.ShowDialog();
        if (dlg.Result == HighPop.Views.CloseDialog.CloseResult.Close)
            System.Windows.Application.Current.Shutdown();
        else if (dlg.Result == HighPop.Views.CloseDialog.CloseResult.Minimize)
        {
            Hide();
            WindowState = WindowState.Minimized;
        }
    }
}
