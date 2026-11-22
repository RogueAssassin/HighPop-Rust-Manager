using System.Windows;
using System.Windows.Threading;
using HighPop.Services;
using HighPop.Views;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace HighPop.Services;

/// <summary>The WPF UI thread, plugged into <see cref="UiDispatcher"/>.</summary>
internal sealed class WpfUiThread : IUiThread
{
    private static Dispatcher? Dispatcher
    {
        get
        {
            var dispatcher = Application.Current?.Dispatcher;
            return dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished
                ? null
                : dispatcher;
        }
    }

    public bool IsAvailable => Dispatcher != null;

    public bool CheckAccess() => Dispatcher?.CheckAccess() ?? true;

    public void Post(Action action, UiPriority priority) =>
        _ = Dispatcher?.InvokeAsync(action, Map(priority));

    public Task InvokeAsync(Action action) =>
        Dispatcher is { } dispatcher ? dispatcher.InvokeAsync(action).Task : Task.CompletedTask;

    public Task<T> InvokeAsync<T>(Func<T> func) =>
        Dispatcher is { } dispatcher ? dispatcher.InvokeAsync(func).Task : Task.FromResult(func());

    private static DispatcherPriority Map(UiPriority priority) =>
        priority == UiPriority.Background ? DispatcherPriority.Background : DispatcherPriority.Normal;
}

/// <summary>Windows implementation of the dialogs, pickers, clipboard and update hooks.</summary>
internal sealed class WpfPlatform : IPlatformUi
{
    private static Window? Owner => Application.Current?.MainWindow;

    public DialogResult ShowMessage(string text, string title, DialogButtons buttons, DialogImage image)
    {
        var result = MessageBox.Show(text, title, buttons switch
        {
            DialogButtons.OKCancel => MessageBoxButton.OKCancel,
            DialogButtons.YesNo => MessageBoxButton.YesNo,
            _ => MessageBoxButton.OK,
        }, image switch
        {
            DialogImage.Information => MessageBoxImage.Information,
            DialogImage.Question => MessageBoxImage.Question,
            DialogImage.Warning => MessageBoxImage.Warning,
            DialogImage.Error => MessageBoxImage.Error,
            _ => MessageBoxImage.None,
        });
        return result switch
        {
            MessageBoxResult.OK => DialogResult.OK,
            MessageBoxResult.Cancel => DialogResult.Cancel,
            MessageBoxResult.Yes => DialogResult.Yes,
            MessageBoxResult.No => DialogResult.No,
            _ => DialogResult.None,
        };
    }

    public string? PickSaveFile(string title, string filter, string? defaultFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName ?? string.Empty,
            AddExtension = true,
            DefaultExt = DefaultExtension(filter, defaultFileName),
        };
        return Show(dialog) ? dialog.FileName : null;
    }

    /// <summary>
    /// The extension appended when the user types none: the first filter pattern (so a double
    /// extension such as ".hprm-server.json" is kept), else the default file name's extension.
    /// </summary>
    internal static string DefaultExtension(string filter, string? defaultFileName)
    {
        var parts = filter.Split('|');
        if (parts.Length > 1)
        {
            var pattern = parts[1].Split(';')[0].Trim();
            if (pattern.Length > 2 && pattern.StartsWith("*.", StringComparison.Ordinal)
                && pattern.IndexOf('*', 1) < 0)
                return pattern[1..];
        }
        return System.IO.Path.GetExtension(defaultFileName ?? string.Empty);
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return Show(dialog) ? dialog.FileName : null;
    }

    public IReadOnlyList<string> PickOpenFiles(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title, Filter = filter, Multiselect = true, CheckFileExists = true,
        };
        return Show(dialog) ? dialog.FileNames : [];
    }

    public string? PickFolder(string title, string? initialFolder)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = title,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = initialFolder ?? string.Empty,
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    public void SetClipboardText(string text) => Clipboard.SetText(text);

    public RemoveServerResult ShowRemoveServer(string serverName)
    {
        var dialog = new RemoveServerDialog(serverName) { Owner = Owner };
        dialog.ShowDialog();
        return dialog.Result;
    }

    public void ShowSettings(HighPop.ViewModels.SettingsViewModel settings, HighPop.ViewModels.MainViewModel main)
    {
        var window = new SettingsWindow(settings, main) { Owner = Owner };
        window.ShowDialog();
    }

    public bool SupportsSelfUpdate => true;

    public Task<bool> DownloadUpdateAsync(string url, IProgress<(int pct, string msg)> progress) =>
        SelfUpdateService.DownloadAndPrepareAsync(url, progress);

    public bool ApplyUpdateAndRestart()
    {
        if (!SelfUpdateService.StartApply()) return false;
        System.Windows.Application.Current.Shutdown();
        return true;
    }

    public bool IsStartWithSystemEnabled() => WindowsStartupTaskService.IsEnabled();

    public bool SetStartWithSystem(bool enabled, out string error) =>
        WindowsStartupTaskService.SetEnabled(enabled, out error);

    private static bool Show(Microsoft.Win32.FileDialog dialog) =>
        (Owner != null ? dialog.ShowDialog(Owner) : dialog.ShowDialog()) == true;
}
