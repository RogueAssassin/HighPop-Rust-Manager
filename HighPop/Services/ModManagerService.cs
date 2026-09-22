using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using HighPop.Games;

namespace HighPop.Services;

public sealed class InstalledModPlugin
{
    public string Name { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public DateTime LastModified { get; init; }
    public string StateText => IsEnabled ? "Enabled" : "Disabled";
}

public sealed record RogueRustInstallTarget(string Framework, string Directory)
{
    public string DllPath => Path.Combine(Directory, "Oxide.Ext.RogueRust.dll");
}

public sealed record ModFrameworkPaths(
    string Framework,
    string RootDirectory,
    string PluginDirectory,
    string ConfigDirectory,
    string ExtensionDirectory);

internal sealed record ModReleaseAsset(string Version, string Name, string DownloadUrl);

/// <summary>Installs and manages the two supported Rust server frameworks: Oxide/uMod and Carbon.</summary>
public sealed class ModManagerService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const string OxideLatestReleaseApi =
        "https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest";
    private const string CarbonLatestReleaseApi =
        "https://api.github.com/repos/CarbonCommunity/Carbon/releases/latest";
    internal const string RogueRustStableManifestUrl =
        "https://raw.githubusercontent.com/RogueAssassin/Oxide.Ext.RogueRust/main/update-manifest.json";
    internal const string RogueRustTestingManifestUrl =
        "https://raw.githubusercontent.com/RogueAssassin/Oxide.Ext.RogueRust/main/update-manifest-testing.json";

    public async Task InstallOxideAsync(
        IGamePlugin plugin,
        string installPath,
        IProgress<(int pct, string msg)>? progress = null)
    {
        if (!plugin.GameId.Equals("rust", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HighPop only installs Oxide for Rust.");
        EnsureRustInstalled(installPath);

        Report(progress, 0, "Resolving the latest official Oxide/uMod Windows release...");
        using var releaseRequest = new HttpRequestMessage(HttpMethod.Get, OxideLatestReleaseApi);
        releaseRequest.Headers.UserAgent.ParseAdd($"HighPop-Rust-Manager/{AppInfo.Version}");
        using var releaseResponse = await Http.SendAsync(releaseRequest);
        releaseResponse.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await releaseResponse.Content.ReadAsStringAsync());
        var asset = SelectOxideWindowsAsset(release.RootElement);

        Report(progress, 15, $"Downloading official Oxide/uMod {asset.Version} for Windows...");
        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        downloadRequest.Headers.UserAgent.ParseAdd($"HighPop-Rust-Manager/{AppInfo.Version}");
        using var downloadResponse = await Http.SendAsync(downloadRequest);
        downloadResponse.EnsureSuccessStatusCode();
        var bytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        ValidateOxideArchive(bytes);
        await ExtractArchiveAsync(bytes, installPath, "oxide", progress);
        var installedVersion = GetInstalledOxideVersion(installPath)
            ?? throw new InvalidDataException("Oxide extraction completed but Oxide.Core.dll was not installed.");
        Report(progress, 100,
            $"Oxide/uMod {installedVersion} installed from official release {asset.Version}. Restart Rust to load it.");
    }

    internal static ModReleaseAsset SelectOxideWindowsAsset(JsonElement release)
    {
        var version = release.TryGetProperty("tag_name", out var tag)
            ? tag.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("The latest Oxide release does not contain a version tag.");

        var asset = release.GetProperty("assets").EnumerateArray()
            .Select(item => new
            {
                Name = item.GetProperty("name").GetString() ?? string.Empty,
                Url = item.GetProperty("browser_download_url").GetString() ?? string.Empty,
            })
            .FirstOrDefault(item =>
                item.Name.Equals("Oxide.Rust.zip", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Oxide {version} does not contain the required Windows asset Oxide.Rust.zip.");

        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith(
                "/OxideMod/Oxide.Rust/releases/download/", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.EndsWith("/Oxide.Rust.zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The latest Oxide release contains an untrusted Windows download URL.");

        return new ModReleaseAsset(version, asset.Name, asset.Url);
    }

    internal static void ValidateOxideArchive(byte[] bytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var paths = archive.Entries
                .Select(entry => entry.FullName.Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!paths.Contains("RustDedicated_Data/Managed/Oxide.Core.dll")
                || !paths.Contains("RustDedicated_Data/Managed/Oxide.Rust.dll"))
                throw new InvalidDataException(
                    "The downloaded Oxide archive is missing its required Windows framework assemblies.");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            throw new InvalidDataException("The downloaded Oxide Windows archive is invalid.", ex);
        }
    }

    public async Task InstallCarbonAsync(
        string installPath,
        IProgress<(int pct, string msg)>? progress = null)
    {
        EnsureRustInstalled(installPath);
        Report(progress, 0, "Resolving the latest stable Carbon release...");

        using var request = new HttpRequestMessage(HttpMethod.Get, CarbonLatestReleaseApi);
        request.Headers.UserAgent.ParseAdd($"HighPop-Rust-Manager/{AppInfo.Version}");
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

    public async Task<string> InstallRogueRustAsync(
        string installPath,
        string channel,
        IProgress<(int pct, string msg)>? progress = null)
    {
        EnsureRustInstalled(installPath);
        var targets = GetRogueRustInstallTargets(installPath);
        if (targets.Count == 0)
            throw new InvalidOperationException("Install Oxide/uMod or Carbon before installing the RogueRust extension.");

        channel = NormalizeRogueRustChannel(channel);
        var manifestUrl = GetRogueRustManifestUrl(channel);
        Report(progress, 0, $"Resolving the RogueRust {channel} channel...");
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        request.Headers.UserAgent.ParseAdd($"HighPop-Rust-Manager/{AppInfo.Version}");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = manifest.RootElement;
        var version = root.GetProperty("Version").GetString();
        var downloadUrl = root.GetProperty("DownloadUrl").GetString();
        var expected = root.GetProperty("Sha256").GetString();
        var manifestChannel = root.GetProperty("Channel").GetInt32();
        var expectedChannel = channel == "Testing" ? 1 : 0;
        if (string.IsNullOrWhiteSpace(version) || manifestChannel != expectedChannel)
            throw new InvalidDataException($"The RogueRust {channel} manifest is invalid or identifies another channel.");
        if (!IsTrustedRogueRustDownloadUrl(downloadUrl))
            throw new InvalidDataException("The RogueRust manifest contains an untrusted download URL.");

        Report(progress, 20, $"Downloading RogueRust {version} ({channel})...");
        var bytes = await Http.GetByteArrayAsync(downloadUrl!);
        await InstallVerifiedRogueRustFilesAsync(targets, bytes, expected, progress);
        var frameworks = string.Join(" and ", targets.Select(target => target.Framework));
        Report(progress, 100, $"RogueRust {version} ({channel}) installed for {frameworks}. Restart Rust to load it.");
        return version;
    }

    internal static string NormalizeRogueRustChannel(string? channel) =>
        channel?.Equals("Testing", StringComparison.OrdinalIgnoreCase) == true
            ? "Testing"
            : "Stable";

    internal static string GetRogueRustManifestUrl(string? channel) =>
        NormalizeRogueRustChannel(channel) == "Testing"
            ? RogueRustTestingManifestUrl
            : RogueRustStableManifestUrl;

    private static bool IsTrustedRogueRustDownloadUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith(
            "/RogueAssassin/Oxide.Ext.RogueRust/releases/download/",
            StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.EndsWith("/Oxide.Ext.RogueRust.dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stages and verifies every target before replacing anything. If a later replacement fails,
    /// every earlier target is restored to its exact pre-install state.
    /// </summary>
    internal static async Task InstallVerifiedRogueRustFilesAsync(
        IReadOnlyList<RogueRustInstallTarget> targets,
        byte[] bytes,
        string? expectedSha256,
        IProgress<(int pct, string msg)>? progress = null,
        Action<int>? beforeReplaceForTest = null)
    {
        if (targets.Count == 0)
            throw new InvalidOperationException("No RogueRust installation targets were supplied.");

        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (string.IsNullOrWhiteSpace(expectedSha256)
            || !actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "RogueRust SHA-256 verification failed; the existing installation was not changed.");

        if (targets.Select(target => Path.GetFullPath(target.DllPath))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new InvalidOperationException("RogueRust installation targets must be unique.");

        var operationId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        var changes = new List<RogueRustInstallChange>();
        try
        {
            // Complete all fallible directory, staging, and backup work before committing a target.
            foreach (var target in targets)
            {
                Directory.CreateDirectory(target.Directory);
                var destination = target.DllPath;
                var temporary = destination + $".highpop-{operationId}.tmp";
                var backup = File.Exists(destination)
                    ? destination + $".bak-{operationId}"
                    : null;
                var change = new RogueRustInstallChange(target, destination, temporary, backup);
                changes.Add(change);
                await File.WriteAllBytesAsync(temporary, bytes);
                if (backup != null) File.Copy(destination, backup, overwrite: false);
            }

            for (var index = 0; index < changes.Count; index++)
            {
                beforeReplaceForTest?.Invoke(index);
                var change = changes[index];
                File.Move(change.TemporaryPath, change.DestinationPath, overwrite: true);
                change.Committed = true;
                Report(progress, 70 + ((index + 1) * 25 / changes.Count),
                    $"Installed the verified DLL for {change.Target.Framework}.");
            }

            foreach (var change in changes)
                PruneRogueRustBackups(change.DestinationPath, keep: 20);
        }
        catch (Exception installError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var change in changes.Where(change => change.Committed).Reverse())
            {
                try
                {
                    if (change.BackupPath != null)
                        File.Copy(change.BackupPath, change.DestinationPath, overwrite: true);
                    else if (File.Exists(change.DestinationPath))
                        File.Delete(change.DestinationPath);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            foreach (var change in changes)
            {
                try { if (File.Exists(change.TemporaryPath)) File.Delete(change.TemporaryPath); }
                catch { }
            }

            if (rollbackErrors.Count > 0)
                throw new AggregateException(
                    "RogueRust installation failed and one or more targets could not be restored.",
                    new[] { installError }.Concat(rollbackErrors));
            throw new InvalidOperationException(
                "RogueRust installation failed; every changed target was restored.", installError);
        }
    }

    private static void PruneRogueRustBackups(string destination, int keep)
    {
        var directory = Path.GetDirectoryName(destination);
        if (directory == null || !Directory.Exists(directory)) return;
        foreach (var old in new DirectoryInfo(directory)
                     .GetFiles(Path.GetFileName(destination) + ".bak-*")
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(keep))
        {
            try { old.Delete(); } catch { }
        }
    }

    public static string? GetInstalledRogueRustVersion(string installPath)
    {
        var dll = GetRogueRustInstallTargets(installPath)
            .Select(target => target.DllPath)
            .FirstOrDefault(File.Exists);
        if (dll == null)
        {
            // Preserve detection if a framework was removed after RogueRust was installed.
            dll = new[]
            {
                Path.Combine(installPath, "RustDedicated_Data", "Managed", "Oxide.Ext.RogueRust.dll"),
                Path.Combine(installPath, "carbon", "extensions", "Oxide.Ext.RogueRust.dll"),
            }.FirstOrDefault(File.Exists);
        }
        if (!File.Exists(dll)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(dll);
            return info.ProductVersion ?? info.FileVersion ?? "Installed";
        }
        catch { return "Installed"; }
    }

    /// <summary>
    /// Resolves every active framework target. Carbon loads extension DLLs from
    /// carbon/extensions; Oxide/uMod loads compatible extension assemblies from
    /// RustDedicated_Data/Managed. If both frameworks are detected, both copies are
    /// updated so HighPop never silently installs into the wrong loader.
    /// </summary>
    public static List<RogueRustInstallTarget> GetRogueRustInstallTargets(string installPath)
    {
        var targets = new List<RogueRustInstallTarget>();
        if (GetInstalledOxideVersion(installPath) != null)
            targets.Add(new("Oxide/uMod", Path.Combine(installPath, "RustDedicated_Data", "Managed")));
        if (IsCarbonInstalled(installPath))
            targets.Add(new("Carbon", Path.Combine(installPath, "carbon", "extensions")));
        return targets;
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
        File.Exists(Path.Combine(installPath, "carbon", "managed", "Carbon.Common.dll"))
        || File.Exists(Path.Combine(installPath, "carbon", "managed", "Carbon.dll"))
        || File.Exists(Path.Combine(installPath, "Carbon.Common", "Carbon.Common.dll"))
        || File.Exists(Path.Combine(installPath, "HarmonyMods", "Carbon.Loader.dll"));

    public static string GetDetectedFramework(string installPath)
    {
        var oxide = GetInstalledOxideVersion(installPath);
        var carbon = IsCarbonInstalled(installPath);
        return (oxide != null, carbon) switch
        {
            (true, true)  => "Conflict: Oxide and Carbon both detected",
            (true, false) => string.IsNullOrWhiteSpace(oxide) ? "Oxide / uMod" : $"Oxide / uMod {oxide}",
            (false, true) => GetCarbonVersion(installPath) is { Length: > 0 } version
                ? $"Carbon {version}"
                : "Carbon",
            _ => "Vanilla (no mod framework detected)",
        };
    }

    public static List<InstalledModPlugin> GetInstalledPlugins(string installPath)
    {
        var result = new List<InstalledModPlugin>();
        AddPlugins(result, "Oxide", Path.Combine(installPath, "oxide", "plugins"));
        AddPlugins(result, "Carbon", Path.Combine(installPath, "carbon", "plugins"));
        return result
            .OrderBy(p => p.Framework, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ModFrameworkPaths? GetActiveFrameworkPaths(string installPath)
    {
        var oxide = GetInstalledOxideVersion(installPath) != null;
        var carbon = IsCarbonInstalled(installPath);
        if (oxide == carbon) return null; // neither installed, or an unsupported dual-framework conflict

        return oxide
            ? new ModFrameworkPaths(
                "Oxide / uMod",
                Path.Combine(installPath, "oxide"),
                Path.Combine(installPath, "oxide", "plugins"),
                Path.Combine(installPath, "oxide", "config"),
                Path.Combine(installPath, "RustDedicated_Data", "Managed"))
            : new ModFrameworkPaths(
                "Carbon",
                Path.Combine(installPath, "carbon"),
                Path.Combine(installPath, "carbon", "plugins"),
                Path.Combine(installPath, "carbon", "configs"),
                Path.Combine(installPath, "carbon", "extensions"));
    }

    public static bool OpenExistingFolder(string path)
    {
        if (!Directory.Exists(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? GetCarbonVersion(string installPath)
    {
        var candidates = new[]
        {
            Path.Combine(installPath, "HarmonyMods", "Carbon.Loader.dll"),
            Path.Combine(installPath, "carbon", "managed", "Carbon.Common.dll"),
            Path.Combine(installPath, "Carbon.Common", "Carbon.Common.dll"),
        };
        foreach (var candidate in candidates.Where(File.Exists))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(candidate);
                return info.ProductVersion ?? info.FileVersion;
            }
            catch { }
        }
        return null;
    }

    private sealed record RogueRustInstallChange(
        RogueRustInstallTarget Target,
        string DestinationPath,
        string TemporaryPath,
        string? BackupPath)
    {
        public bool Committed { get; set; }
    }

    private static void AddPlugins(List<InstalledModPlugin> target, string framework, string directory)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                var lower = fileName.ToLowerInvariant();
                var enabled = lower.EndsWith(".cs", StringComparison.Ordinal)
                    || lower.EndsWith(".dll", StringComparison.Ordinal);
                var disabled = lower.EndsWith(".disabled", StringComparison.Ordinal)
                    || lower.EndsWith(".cs.off", StringComparison.Ordinal)
                    || lower.EndsWith(".dll.off", StringComparison.Ordinal);
                if (!enabled && !disabled) continue;

                var name = fileName;
                foreach (var suffix in new[] { ".disabled", ".off", ".cs", ".dll" })
                    if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        name = name[..^suffix.Length];

                target.Add(new InstalledModPlugin
                {
                    Name = name,
                    Framework = framework,
                    FileName = fileName,
                    FullPath = path,
                    IsEnabled = enabled && !disabled,
                    LastModified = File.GetLastWriteTime(path),
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

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

    private static void Report(IProgress<(int pct, string msg)>? progress, int pct, string message) =>
        progress?.Report((pct, message));
}
