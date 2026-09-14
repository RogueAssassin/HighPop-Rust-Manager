using System.Diagnostics;
using System.Windows;
using HighPop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace HighPop.Views;

public partial class SupportView : System.Windows.Controls.UserControl
{
    private const string RepositoryUrl = "https://github.com/RogueAssassin/HighPop-Rust-Manager";

    public SupportView() => InitializeComponent();

    private void SupportView_Loaded(object sender, RoutedEventArgs e)
    {
        var snapshots = App.Services.GetRequiredService<ServerManagerService>().GetHealthSnapshots();
        HealthSummaryText.Text = snapshots.Count == 0
            ? "No Rust server processes are currently attached."
            : string.Join(Environment.NewLine, snapshots.Select(snapshot =>
                $"{snapshot.DisplayName}: {snapshot.LifecyclePhase} · process "
                + $"{FormatAge(snapshot.ProcessObservedUtc)} · Rust {FormatAge(snapshot.RustReadyUtc)} · "
                + $"WebRCON {FormatAge(snapshot.RconReadyUtc)} · players {FormatAge(snapshot.LastPlayerSampleUtc)}"));
    }

    private static string FormatAge(DateTime? timestampUtc)
    {
        if (!timestampUtc.HasValue) return "not observed";
        var age = DateTime.UtcNow - timestampUtc.Value;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalMinutes >= 1
            ? $"{age.TotalMinutes:0}m ago"
            : $"{age.TotalSeconds:0}s ago";
    }

    private void GitHubButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true }); }
        catch { }
    }

    private async void ExportSupportBundleButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export redacted HighPop support bundle",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"HighPop-support-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            AddExtension = true,
            DefaultExt = ".zip",
        };
        if (dialog.ShowDialog() != true) return;

        ExportSupportBundleButton.IsEnabled = false;
        SupportBundleStatus.Text = "Creating redacted support bundle...";
        try
        {
            await App.Services.GetRequiredService<SupportBundleService>().CreateAsync(dialog.FileName);
            SupportBundleStatus.Text = $"Support bundle saved to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            SupportBundleStatus.Text = $"Support bundle failed: {ex.Message}";
        }
        finally
        {
            ExportSupportBundleButton.IsEnabled = true;
        }
    }
}
