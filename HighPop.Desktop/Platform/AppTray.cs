using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HighPop.Services;

namespace HighPop.Desktop.Platform;

/// <summary>
/// System tray icon with an Open and Exit menu. Where the desktop has no tray (stock GNOME without an
/// AppIndicator extension) the icon is simply not shown, so closing the window never hides it there.
/// </summary>
internal sealed class AppTray : ITrayStatus
{
    private const string IconUri = "avares://HighPop.Desktop/Assets/highpop-mark.png";

    private readonly TrayIcon _icon;
    private Action? _open;
    private Action? _exit;

    public AppTray()
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open HighPop");
        open.Click += (_, _) => _open?.Invoke();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => _exit?.Invoke();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _icon = new TrayIcon
        {
            ToolTipText = "HighPop Rust Manager",
            Menu = menu,
            Icon = LoadIcon(),
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => _open?.Invoke();
    }

    public TrayIcons Icons => [_icon];

    /// <summary>Connects the menu to the main window once it exists.</summary>
    public void Attach(Action open, Action exit)
    {
        _open = open;
        _exit = exit;
    }

    public void SetStatus(int running, int total) =>
        Dispatcher.UIThread.Post(() => _icon.ToolTipText = $"HighPop - {running}/{total} servers running");

    public static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri(IconUri));
        return new WindowIcon(new Bitmap(stream));
    }
}
