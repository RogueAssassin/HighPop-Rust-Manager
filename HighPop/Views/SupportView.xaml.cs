using System.Diagnostics;
using System.Windows;

namespace HighPop.Views;

public partial class SupportView : System.Windows.Controls.UserControl
{
    private const string RepositoryUrl = "https://github.com/RogueAssassin/HighPop-Rust-Manager";

    public SupportView() => InitializeComponent();

    private void GitHubButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true }); }
        catch { }
    }
}
