using Avalonia.Controls;
using HighPop.Services;

namespace HighPop.Desktop.Views;

/// <summary>
/// A masked text box that hands its text to view-model commands through <see cref="IPasswordSource"/>,
/// so the command never reads the visual tree and can wipe the field after use.
/// </summary>
public sealed class PasswordBox : TextBox, IPasswordSource
{
    // Keep the standard TextBox look.
    protected override Type StyleKeyOverride => typeof(TextBox);

    public PasswordBox() => PasswordChar = '●';

    public string Password => Text ?? string.Empty;

    // IPasswordSource.Clear() is satisfied by the inherited TextBox.Clear().
}
