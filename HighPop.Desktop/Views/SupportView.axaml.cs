using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HighPop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HighPop.Desktop.Views;

public partial class SupportView : UserControl
{
    private const string RepositoryUrl = "https://github.com/RogueAssassin/HighPop-Rust-Manager";

    public SupportView()
    {
        InitializeComponent();
        VersionText.Text = HighPop.AppInfo.VersionDisplay;
        PlatformText.Text = $".NET {Environment.Version.Major} · Avalonia · {RuntimeInformation.OSDescription}";
        AttachedToVisualTree += (_, _) => RefreshHealth();
    }

    private void RefreshHealth()
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
        return age.TotalMinutes >= 1 ? $"{age.TotalMinutes:0}m ago" : $"{age.TotalSeconds:0}s ago";
    }

    private void GitHub_Click(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        var path = Dialogs.SaveFile("Export redacted HighPop support bundle", "ZIP archive|*.zip",
            $"HighPop-support-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (string.IsNullOrEmpty(path)) return;

        ExportButton.IsEnabled = false;
        BundleStatus.Text = "Creating redacted support bundle...";
        try
        {
            await App.Services.GetRequiredService<SupportBundleService>().CreateAsync(path);
            BundleStatus.Text = $"Support bundle saved to {path}";
        }
        catch (Exception ex)
        {
            BundleStatus.Text = $"Support bundle failed: {ex.Message}";
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }
}
