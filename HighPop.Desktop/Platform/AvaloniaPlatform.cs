using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HighPop.Desktop.Views;
using HighPop.Services;
using HighPop.ViewModels;

namespace HighPop.Desktop.Platform;

/// <summary>
/// Dialogs, pickers and the clipboard for the Avalonia app. The view-models ask for these
/// synchronously, so each call runs the toolkit's async dialog inside a nested UI loop.
/// </summary>
internal sealed class AvaloniaPlatform : IPlatformUi
{
    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public DialogResult ShowMessage(string text, string title, DialogButtons buttons, DialogImage image) =>
        Block(() => MessageWindow.ShowAsync(MainWindow, text, title, buttons, image));

    public string? PickSaveFile(string title, string filter, string? defaultFileName) => Block(async () =>
    {
        var storage = MainWindow?.StorageProvider;
        if (storage == null) return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultFileName,
            FileTypeChoices = ParseFilter(filter),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    });

    public string? PickOpenFile(string title, string filter) =>
        PickOpen(title, filter, allowMultiple: false).FirstOrDefault();

    public IReadOnlyList<string> PickOpenFiles(string title, string filter) =>
        PickOpen(title, filter, allowMultiple: true);

    public string? PickFolder(string title, string? initialFolder) => Block(async () =>
    {
        var storage = MainWindow?.StorageProvider;
        if (storage == null) return null;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(initialFolder);
        var folders = await storage.OpenFolderPickerAsync(options);
        return folders.FirstOrDefault()?.TryGetLocalPath();
    });

    public void SetClipboardText(string text)
    {
        var clipboard = MainWindow?.Clipboard;
        if (clipboard == null) return;
        _ = UiDispatcher.RunAsync(() => clipboard.SetTextAsync(text));
    }

    public RemoveServerResult ShowRemoveServer(string serverName) =>
        Block(() => RemoveServerWindow.ShowAsync(MainWindow, serverName));

    public void ShowSettings(SettingsViewModel settings, MainViewModel main) =>
        Block(async () =>
        {
            await Views.SettingsWindow.ShowAsync(MainWindow, settings, main);
            return true;
        });

    public bool SupportsSelfUpdate => LinuxSelfUpdateService.IsSupported();

    public Task<bool> DownloadUpdateAsync(string url, IProgress<(int pct, string msg)> progress) =>
        LinuxSelfUpdateService.DownloadAndPrepareAsync(url, progress);

    public bool ApplyUpdateAndRestart()
    {
        if (!LinuxSelfUpdateService.StartApply()) return false;
        // The script waits for this process to exit, so shut down now. Running Rust servers detach.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown());
        return true;
    }

    public bool IsStartWithSystemEnabled() => false;

    public bool SetStartWithSystem(bool enabled, out string error)
    {
        error = enabled ? "Starting with the system is not available in the desktop app yet." : string.Empty;
        return !enabled;
    }

    private static IReadOnlyList<string> PickOpen(string title, string filter, bool allowMultiple) => Block(async () =>
    {
        var storage = MainWindow?.StorageProvider;
        if (storage == null) return (IReadOnlyList<string>)[];
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = ParseFilter(filter),
        });
        return (IReadOnlyList<string>)files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
    });

    /// <summary>Converts "Name|*.a;*.b|Other|*.*" (the Windows filter syntax) to picker file types.</summary>
    private static IReadOnlyList<FilePickerFileType> ParseFilter(string filter)
    {
        var parts = filter.Split('|', StringSplitOptions.TrimEntries);
        var types = new List<FilePickerFileType>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            types.Add(new FilePickerFileType(parts[i]) { Patterns = patterns });
        }
        return types;
    }

    /// <summary>Runs async dialog work to completion from synchronous view-model code.</summary>
    private static T Block<T>(Func<Task<T>> work)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.InvokeAsync(() => Block(work)).GetTask().GetAwaiter().GetResult();

        var task = work();
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }
}
