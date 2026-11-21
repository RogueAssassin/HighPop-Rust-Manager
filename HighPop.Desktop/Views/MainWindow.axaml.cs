using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using HighPop.Services;
using HighPop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HighPop.Desktop.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// Tab shown first for a selected server. HIGHPOP_START_TAB (0-based) lets screenshots and
    /// smoke checks open a given tab without clicking, which synthetic input cannot always do.
    /// </summary>
    public static int StartTab { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("HIGHPOP_START_TAB"), out var tab) && tab >= 0 ? tab : 0;

    /// <summary>Sub-tab shown first inside the chosen group (HIGHPOP_START_SUB, 0-based).</summary>
    public static int StartSub { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("HIGHPOP_START_SUB"), out var sub) && sub >= 0 ? sub : 0;

    public static string VersionText { get; } = HighPop.AppInfo.VersionDisplay;

    /// <summary>Whether this install can replace itself, so the update banner offers Install.</summary>
    public static bool CanSelfUpdate { get; } = LinuxSelfUpdateService.IsSupported();
    public static bool CannotSelfUpdate => !CanSelfUpdate;

    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        try { Icon = Platform.AppTray.LoadIcon(); } catch (Exception ex) when (ex is IOException or ArgumentException) { }
        Closing += OnClosing;
        Opened += (_, _) => Dispatcher.UIThread.Post(StartupChecks, DispatcherPriority.Background);
        // HIGHPOP_START_PAGE=dashboard|machines opens that page first (for screenshots).
        var page = Environment.GetEnvironmentVariable("HIGHPOP_START_PAGE");
        if (page is "dashboard" or "machines" or "support")
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is not MainViewModel vm) return;
                if (page == "dashboard") vm.OpenDashboardCommand.Execute(null);
                else if (page == "support") vm.OpenSupportCommand.Execute(null);
                else vm.OpenMachinesCommand.Execute(null);
            }, DispatcherPriority.Background);
        if (Environment.GetEnvironmentVariable("HIGHPOP_OPEN_ADD") == "1")
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is MainViewModel vm) vm.OpenAddDialogCommand.Execute(null);
            }, DispatcherPriority.Background);
        if (Environment.GetEnvironmentVariable("HIGHPOP_OPEN_SETTINGS") == "1")
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is MainViewModel vm && vm.OpenSettingsCommand.CanExecute(null))
                    vm.OpenSettingsCommand.Execute(null);
            }, DispatcherPriority.Background);
    }

    private void SortCombo_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ComboBox combo || DataContext is not MainViewModel vm) return;
        var index = combo.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string?)i.Tag == vm.SortMode);
        combo.SelectedIndex = index >= 0 ? index : 0;
    }

    private void SortCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm
            && (sender as ComboBox)?.SelectedItem is ComboBoxItem { Tag: string mode }
            && vm.SortMode != mode)
            vm.SortMode = mode;
    }

    /// <summary>Brings the window back from the tray.</summary>
    public void ShowFromTray()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        });
    }

    /// <summary>Exits the manager without the close prompt. Running Rust servers are detached, not stopped.</summary>
    public void ExitApp()
    {
        _exiting = true;
        Dispatcher.UIThread.Post(() =>
            (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting || e.CloseReason != WindowCloseReason.WindowClosing) return;
        e.Cancel = true;
        _ = PromptCloseAsync();
    }

    private async Task PromptCloseAsync()
    {
        switch (await CloseWindow.ShowAsync(this))
        {
            case CloseWindow.CloseChoice.Exit: ExitApp(); break;
            case CloseWindow.CloseChoice.Minimize: Hide(); break;
        }
    }

    private async void StartupChecks()
    {
        // --background starts hidden, for launching alongside the desktop session.
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase)))
        {
            Hide();
            return;
        }

        if (Environment.GetEnvironmentVariable("HIGHPOP_SHOW_CLOSE") == "1")
        {
            await PromptCloseAsync();
            return;
        }

        // Screenshot aid: show the update banner without a newer release.
        if (Environment.GetEnvironmentVariable("HIGHPOP_DEMO_UPDATE") == "1" && DataContext is MainViewModel upd)
        {
            upd.LatestVersion = "v9.9.9";
            upd.UpdateAvailable = true;
            return;
        }

        // Screenshot aid: a fake remote server, so the remote page can be seen without a second machine.
        if (Environment.GetEnvironmentVariable("HIGHPOP_DEMO_REMOTE") == "1" && DataContext is MainViewModel demo)
        {
            var info = new RemoteServerInfo { Id = "demo", DisplayName = "Demo Remote", GameId = "rust", Status = "Running" };
            var remote = new RemoteServerViewModel("demo-machine", "Slave One", info, App.Services.GetRequiredService<RemoteMachineService>());
            demo.RemoteServers.Add(remote);
            demo.SelectedRemoteServer = remote;
            // Selecting starts polling, which clears the log, so add the sample lines afterwards.
            DispatcherTimer.RunOnce(() =>
            {
                remote.Log.Add(new Models.ConsoleMessage { Text = "Server started on port 28015", Type = Models.ConsoleMessageType.System });
                remote.Log.Add(new Models.ConsoleMessage { Text = "Players: 3/100", Type = Models.ConsoleMessageType.Info });
                remote.Log.Add(new Models.ConsoleMessage { Text = "Warning: slow frame detected", Type = Models.ConsoleMessageType.Warning });
            }, TimeSpan.FromSeconds(1));
            return;
        }

        var config = App.Services.GetRequiredService<ConfigService>();
        var forced = Environment.GetEnvironmentVariable("HIGHPOP_SHOW_ONBOARDING") == "1";
        if (config.HasSeenOnboarding && !forced) return;
        if (await OnboardingWindow.ShowAsync(this) && !forced)
        {
            config.HasSeenOnboarding = true;
            config.Save();
        }
    }
}
