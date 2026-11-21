using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HighPop.Services;

namespace HighPop.Desktop.Views;

/// <summary>A small modal message box with OK, OK/Cancel or Yes/No buttons.</summary>
internal static class MessageWindow
{
    public static Task<DialogResult> ShowAsync(Window? owner, string text, string title,
        DialogButtons buttons, DialogImage image)
    {
        var result = DialogResult.None;
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };

        Button MakeButton(string label, DialogResult value, bool isDefault = false)
        {
            var button = new Button { Content = label, MinWidth = 84, IsDefault = isDefault };
            button.Click += (_, _) => { result = value; window.Close(); };
            return button;
        }

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        switch (buttons)
        {
            case DialogButtons.YesNo:
                buttonRow.Children.Add(MakeButton("Yes", DialogResult.Yes, isDefault: true));
                buttonRow.Children.Add(MakeButton("No", DialogResult.No));
                break;
            case DialogButtons.OKCancel:
                buttonRow.Children.Add(MakeButton("OK", DialogResult.OK, isDefault: true));
                buttonRow.Children.Add(MakeButton("Cancel", DialogResult.Cancel));
                break;
            default:
                buttonRow.Children.Add(MakeButton("OK", DialogResult.OK, isDefault: true));
                break;
        }

        var glyph = image switch
        {
            DialogImage.Warning => "⚠",
            DialogImage.Error => "✖",
            DialogImage.Question => "?",
            DialogImage.Information => "ℹ",
            _ => string.Empty,
        };
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(20) };
        body.Children.Add(new TextBlock
        {
            Text = glyph.Length > 0 ? $"{glyph}  {text}" : text,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(buttonRow);
        window.Content = body;

        return ShowModalAsync(window, owner).ContinueWith(_ => result, TaskScheduler.Default);
    }

    internal static Task ShowModalAsync(Window window, Window? owner) =>
        owner != null ? window.ShowDialog(owner) : ShowStandalone(window);

    private static Task ShowStandalone(Window window)
    {
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        return closed.Task;
    }
}
