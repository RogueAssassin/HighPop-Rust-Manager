using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using HighPop.Services;

namespace HighPop.Desktop.Views;

/// <summary>Asks whether to remove a server from HighPop only, or delete its files too.</summary>
internal static class RemoveServerWindow
{
    public static Task<RemoveServerResult> ShowAsync(Window? owner, string serverName)
    {
        var result = RemoveServerResult.Cancel;
        var window = new Window
        {
            Title = "Remove server",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };

        Button MakeButton(string label, RemoveServerResult value)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => { result = value; window.Close(); };
            return button;
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(MakeButton("Remove from HighPop only", RemoveServerResult.RemoveOnly));
        buttons.Children.Add(MakeButton("Remove and delete files", RemoveServerResult.RemoveWithFiles));
        buttons.Children.Add(MakeButton("Cancel", RemoveServerResult.Cancel));

        var body = new StackPanel { Spacing = 16, Margin = new Thickness(20) };
        body.Children.Add(new TextBlock
        {
            Text = $"Remove \"{serverName}\"?\n\nRemoving it from HighPop keeps the server files on disk. " +
                   "Deleting the files cannot be undone.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        body.Children.Add(buttons);
        window.Content = body;

        return MessageWindow.ShowModalAsync(window, owner).ContinueWith(_ => result, TaskScheduler.Default);
    }
}
