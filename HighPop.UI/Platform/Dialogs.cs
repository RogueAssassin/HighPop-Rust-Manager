namespace HighPop.Services;

public enum DialogButtons { OK, OKCancel, YesNo }
public enum DialogImage { None, Information, Question, Warning, Error }
public enum DialogResult { None, OK, Cancel, Yes, No }

/// <summary>Native dialogs and the clipboard, implemented once per UI toolkit.</summary>
public interface IPlatformUi
{
    DialogResult ShowMessage(string text, string title, DialogButtons buttons, DialogImage image);
    /// <param name="filter">Windows-style filter, e.g. "Zip archive|*.zip|All files|*.*".</param>
    string? PickSaveFile(string title, string filter, string? defaultFileName);
    string? PickOpenFile(string title, string filter);
    IReadOnlyList<string> PickOpenFiles(string title, string filter);
    string? PickFolder(string title, string? initialFolder);
    void SetClipboardText(string text);
    RemoveServerResult ShowRemoveServer(string serverName);

    /// <summary>False when this app cannot replace itself yet, so it must not offer to install.</summary>
    bool SupportsSelfUpdate { get; }

    /// <summary>Downloads an update package and stages it. Returns false on failure.</summary>
    Task<bool> DownloadUpdateAsync(string url, IProgress<(int pct, string msg)> progress);
    /// <summary>Starts the staged update and asks the app to close. Returns false if it could not start.</summary>
    bool ApplyUpdateAndRestart();

    /// <summary>"Start with Windows" on Windows, a systemd user service or autostart entry on Linux.</summary>
    bool IsStartWithSystemEnabled();
    bool SetStartWithSystem(bool enabled, out string error);

    void ShowSettings(HighPop.ViewModels.SettingsViewModel settings, HighPop.ViewModels.MainViewModel main);
}

public enum RemoveServerResult { Cancel, RemoveOnly, RemoveWithFiles }

/// <summary>Anything that can hand over a typed password and then wipe it.</summary>
public interface IPasswordSource
{
    string Password { get; }
    void Clear();
}

/// <summary>
/// Lets a command take whatever password control the toolkit has. A control that already
/// implements <see cref="IPasswordSource"/> is used directly; otherwise any object with a
/// string <c>Password</c> property and a <c>Clear()</c> method (the WPF PasswordBox) is wrapped.
/// </summary>
public static class PasswordSources
{
    public static IPasswordSource? From(object? control) => control switch
    {
        null => null,
        IPasswordSource source => source,
        _ => ReflectedPasswordSource.TryCreate(control),
    };

    private sealed class ReflectedPasswordSource(object control,
        System.Reflection.PropertyInfo property, System.Reflection.MethodInfo clear) : IPasswordSource
    {
        public string Password => property.GetValue(control) as string ?? string.Empty;
        public void Clear() => clear.Invoke(control, null);

        public static ReflectedPasswordSource? TryCreate(object control)
        {
            var type = control.GetType();
            var property = type.GetProperty("Password");
            var clear = type.GetMethod("Clear", Type.EmptyTypes);
            return property?.PropertyType == typeof(string) && clear != null
                ? new ReflectedPasswordSource(control, property, clear)
                : null;
        }
    }
}

/// <summary>The tray or notification-area icon, when the platform has one.</summary>
public interface ITrayStatus { void SetStatus(int running, int total); }

/// <summary>
/// Static entry point so view-models stay free of any UI toolkit. The app sets
/// <see cref="Platform"/> once at startup. Without one (smoke tests, headless host) every call
/// answers "none" or null and never blocks.
/// </summary>
public static class Dialogs
{
    public static IPlatformUi? Platform { get; set; }

    public static DialogResult Show(string text, string title,
        DialogButtons buttons = DialogButtons.OK, DialogImage image = DialogImage.None) =>
        Platform?.ShowMessage(text, title, buttons, image) ?? DialogResult.None;

    public static string? SaveFile(string title, string filter, string? defaultFileName = null) =>
        Platform?.PickSaveFile(title, filter, defaultFileName);

    public static string? OpenFile(string title, string filter) =>
        Platform?.PickOpenFile(title, filter);

    public static IReadOnlyList<string> OpenFiles(string title, string filter) =>
        Platform?.PickOpenFiles(title, filter) ?? [];

    public static RemoveServerResult RemoveServer(string serverName) =>
        Platform?.ShowRemoveServer(serverName) ?? RemoveServerResult.Cancel;

    public static void Settings(HighPop.ViewModels.SettingsViewModel settings, HighPop.ViewModels.MainViewModel main) =>
        Platform?.ShowSettings(settings, main);

    public static Task<bool> DownloadUpdateAsync(string url, IProgress<(int pct, string msg)> progress) =>
        Platform?.DownloadUpdateAsync(url, progress) ?? Task.FromResult(false);

    public static bool SupportsSelfUpdate => Platform?.SupportsSelfUpdate ?? false;

    public static bool ApplyUpdateAndRestart() => Platform?.ApplyUpdateAndRestart() ?? false;

    public static bool IsStartWithSystemEnabled() => Platform?.IsStartWithSystemEnabled() ?? false;

    public static bool SetStartWithSystem(bool enabled, out string error)
    {
        if (Platform == null) { error = string.Empty; return true; }
        return Platform.SetStartWithSystem(enabled, out error);
    }

    public static string? Folder(string title, string? initialFolder = null) =>
        Platform?.PickFolder(title, initialFolder);

    public static void CopyText(string text) => Platform?.SetClipboardText(text);
}
