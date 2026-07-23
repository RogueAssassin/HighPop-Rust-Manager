using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using HighPop.Games;

namespace HighPop.Services;

/// <summary>Installs and manages the two supported Rust server frameworks: Oxide/uMod and Carbon.</summary>
public sealed class ModManagerService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const string OxideDownloadUrl = "https://umod.org/games/rust/download";
    private const string CarbonLatestReleaseApi =
        "https://api.github.com/repos/CarbonCommunity/Carbon/releases/latest";

    public async Task InstallOxideAsync(
        IGamePlugin plugin,
        string installPath,
        IProgress<(int pct, string msg)>? progress = null)
    {
        if (!plugin.GameId.Equals("rust", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HighPop only installs Oxide for Rust.");
        EnsureRustInstalled(installPath);

        Report(progress, 0, "Downloading the latest Oxide/uMod build for Rust...");
        var bytes = await Http.GetByteArrayAsync(OxideDownloadUrl);
        await ExtractArchiveAsync(bytes, installPath, "oxide", progress);
        Report(progress, 100, "Oxide/uMod installed. Restart Rust to load the framework.");
    }

    public async Task InstallCarbonAsync(
        string installPath,
        IProgress<(int pct, string msg)>? progress = null)
    {
        EnsureRustInstalled(installPath);
        Report(progress, 0, "Resolving the latest stable Carbon release...");

        using var request = new HttpRequestMessage(HttpMethod.Get, CarbonLatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("HighPop-Rust-Manager/0.2");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var asset = release.RootElement.GetProperty("assets").EnumerateArray()
            .Select(item => new
            {
                Name = item.GetProperty("name").GetString() ?? string.Empty,
                Url = item.GetProperty("browser_download_url").GetString() ?? string.Empty,
            })
            .FirstOrDefault(item =>
                item.Name.EndsWith("Carbon.Windows.Release.zip", StringComparison.OrdinalIgnoreCase)
                || (item.Name.Contains("Windows", StringComparison.OrdinalIgnoreCase)
                    && item.Name.Contains("Release", StringComparison.OrdinalIgnoreCase)
                    && item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("The latest Carbon release has no Windows release archive.");

        Report(progress, 15, $"Downloading {asset.Name}...");
        var bytes = await Http.GetByteArrayAsync(asset.Url);
        await ExtractArchiveAsync(bytes, installPath, "carbon", progress);
        Report(progress, 100, "Carbon installed. Restart Rust to load the framework.");
    }

    public static string? GetInstalledOxideVersion(string installPath)
    {
        var dll = Path.Combine(installPath, "RustDedicated_Data", "Managed", "Oxide.Core.dll");
        if (!File.Exists(dll)) dll = Path.Combine(installPath, "oxide", "Oxide.Core.dll");
        if (!File.Exists(dll)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(dll);
            return info.ProductVersion ?? info.FileVersion;
        }
        catch { return null; }
    }

    public static bool IsCarbonInstalled(string installPath) =>
        Directory.Exists(Path.Combine(installPath, "carbon"))
        || Directory.Exists(Path.Combine(installPath, "Carbon.Common"))
        || File.Exists(Path.Combine(installPath, "HarmonyMods", "Carbon.Loader.dll"));

    public static void OpenPluginFolder(IGamePlugin plugin, string installPath)
    {
        if (!plugin.GameId.Equals("rust", StringComparison.OrdinalIgnoreCase)) return;
        OpenFolder(Path.Combine(installPath, "oxide", "plugins"));
    }

    public static void OpenCarbonPluginFolder(string installPath) =>
        OpenFolder(Path.Combine(installPath, "carbon", "plugins"));

    private static async Task ExtractArchiveAsync(
        byte[] bytes,
        string installPath,
        string prefix,
        IProgress<(int pct, string msg)>? progress)
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"highpop_{prefix}_{Guid.NewGuid():N}.zip");
        try
        {
            await File.WriteAllBytesAsync(archivePath, bytes);
            Directory.CreateDirectory(installPath);
            Report(progress, 65, $"Extracting {prefix} into the Rust server...");
            ZipFile.ExtractToDirectory(archivePath, installPath, overwriteFiles: true);
        }
        finally
        {
            try { File.Delete(archivePath); } catch { }
        }
    }

    private static void EnsureRustInstalled(string installPath)
    {
        if (!File.Exists(Path.Combine(installPath, "RustDedicated.exe")))
            throw new InvalidOperationException("Install the Rust dedicated server before installing a mod framework.");
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private static void Report(IProgress<(int pct, string msg)>? progress, int pct, string message) =>
        progress?.Report((pct, message));
}
