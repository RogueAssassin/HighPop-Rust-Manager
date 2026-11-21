using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace HighPop.Services;

/// <summary>One plugin published on the RogueRust plugin catalog.</summary>
public sealed class RogueRustCatalogPlugin
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>File name inside the framework's plugins folder, e.g. <c>RogueRustServerInfo.cs</c>.</summary>
    public string FileName { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string DocumentationUrl { get; init; } = string.Empty;

    /// <summary>Version from the installed file's <c>[Info]</c> attribute; null when not installed.</summary>
    public string? InstalledVersion { get; init; }

    public bool IsInstalled => InstalledVersion != null;
    public bool UpdateAvailable => IsInstalled && InstalledVersion!.Length > 0
        && RogueRustPluginCatalogService.CompareVersions(Version, InstalledVersion) > 0;

    public string VersionText => Version.Length == 0 ? string.Empty : $"v{Version}";
    public string StatusText => !IsInstalled
        ? "Not installed"
        : UpdateAvailable
            ? $"Update available: v{InstalledVersion} → v{Version}"
            : InstalledVersion!.Length == 0 ? "Installed" : $"Installed v{InstalledVersion}";
    public string ActionText => !IsInstalled ? "⬇ Install" : UpdateAvailable ? "⬆ Update" : "↻ Reinstall";

    public RogueRustCatalogPlugin WithInstalledVersion(string? installedVersion) => new()
    {
        Name = Name,
        Category = Category,
        Version = Version,
        Description = Description,
        FileName = FileName,
        DownloadUrl = DownloadUrl,
        DocumentationUrl = DocumentationUrl,
        InstalledVersion = installedVersion,
    };
}

/// <summary>
/// Reads the public plugin catalog at <see cref="RogueGamingEndpoints.PluginsUrl"/> and installs
/// its single-file plugins into the active Oxide/uMod or Carbon plugins folder. Downloads are
/// restricted to <c>https://plugins.roguegaming.com.au/plugins/*.cs</c>, size-capped, checked to
/// be an Oxide/Carbon plugin whose <c>[Info]</c> name matches the file, and swapped in atomically
/// so the framework's hot-reload never sees a partial file.
/// </summary>
public sealed partial class RogueRustPluginCatalogService
{
    private const int MaxCatalogBytes = 4 * 1024 * 1024;
    private const int MaxPluginBytes = 4 * 1024 * 1024;
    private const int BackupsToKeep = 5;
    private static readonly Uri CatalogUri = new(RogueGamingEndpoints.PluginsUrl + "/");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private IReadOnlyList<RogueRustCatalogPlugin>? _memory;

    public RogueRustPluginCatalogService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(RogueGamingEndpoints.UserAgent);
    }

    /// <summary>Downloads (or returns the in-memory copy of) the published catalog.</summary>
    public async Task<IReadOnlyList<RogueRustCatalogPlugin>> LoadAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!refresh && _memory != null) return _memory;
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _memory != null) return _memory;
            try
            {
                var bytes = await GetCappedAsync(CatalogUri, MaxCatalogBytes, cancellationToken).ConfigureAwait(false);
                var plugins = ParseCatalog(Encoding.UTF8.GetString(bytes));
                if (plugins.Count == 0)
                    throw new InvalidOperationException("The RogueRust plugin catalog did not list any plugins.");
                _memory = plugins;
                return plugins;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                if (_memory != null) return _memory;
                throw new InvalidOperationException(
                    $"The RogueRust plugin catalog ({RogueGamingEndpoints.PluginsHost}) is unavailable: {ex.Message}", ex);
            }
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>Returns <paramref name="catalog"/> annotated with the versions installed in <paramref name="installPath"/>.</summary>
    public static List<RogueRustCatalogPlugin> WithInstalledVersions(
        IEnumerable<RogueRustCatalogPlugin> catalog,
        string installPath)
    {
        var pluginDirectory = ModManagerService.GetActiveFrameworkPaths(installPath)?.PluginDirectory;
        return catalog
            .Select(plugin => plugin.WithInstalledVersion(pluginDirectory == null
                ? null
                : ReadInstalledVersion(Path.Combine(pluginDirectory, plugin.FileName))))
            .ToList();
    }

    /// <summary>Installs or updates one plugin and returns the installed version.</summary>
    public async Task<string> InstallAsync(
        RogueRustCatalogPlugin plugin,
        string installPath,
        CancellationToken cancellationToken = default)
    {
        var paths = ModManagerService.GetActiveFrameworkPaths(installPath)
            ?? throw new InvalidOperationException(
                "Install exactly one mod framework (Oxide/uMod or Carbon) before installing RogueRust plugins.");
        if (!IsValidPluginFileName(plugin.FileName))
            throw new InvalidOperationException($"'{plugin.FileName}' is not a valid plugin file name.");
        if (!Uri.TryCreate(plugin.DownloadUrl, UriKind.Absolute, out var downloadUri)
            || !IsTrustedPluginDownload(downloadUri, plugin.FileName))
            throw new InvalidOperationException(
                $"Refusing to download {plugin.FileName}: it is not served from {RogueGamingEndpoints.PluginsUrl}/plugins/.");

        var bytes = await GetCappedAsync(downloadUri, MaxPluginBytes, cancellationToken).ConfigureAwait(false);
        var source = DecodeUtf8(bytes);
        var info = ReadInfoAttribute(source);
        var expectedName = Path.GetFileNameWithoutExtension(plugin.FileName);
        if (!LooksLikePlugin(source) || info == null
            || !info.Value.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The downloaded {plugin.FileName} is not a valid Oxide/Carbon plugin named {expectedName}.");

        Directory.CreateDirectory(paths.PluginDirectory);
        var destination = Path.Combine(paths.PluginDirectory, plugin.FileName);
        // The staging name does not end in .cs, so Oxide/Carbon file watchers ignore it until the
        // atomic rename below replaces the live plugin in one step.
        var staging = destination + ".highpop-download";
        await File.WriteAllBytesAsync(staging, bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(destination)) BackupExisting(paths.RootDirectory, destination);
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch (IOException) { }
        }
        return info.Value.Version;
    }

    // ── Parsing ────────────────────────────────────────────────────────────────

    public static List<RogueRustCatalogPlugin> ParseCatalog(string html)
    {
        var plugins = new List<RogueRustCatalogPlugin>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match section in CategoryRegex().Matches(html))
        {
            var category = CleanText(section.Groups["title"].Value);
            foreach (Match card in CardRegex().Matches(section.Groups["body"].Value))
            {
                var body = card.Groups["body"].Value;
                var name = CleanText(FirstGroup(H3Regex(), body));
                var version = CleanText(FirstGroup(VersionRegex(), body)).TrimStart('v', 'V');
                var description = CleanText(FirstGroup(ParagraphRegex(), body));

                string? download = null, documentation = null;
                foreach (Match link in HrefRegex().Matches(body))
                {
                    if (!Uri.TryCreate(CatalogUri, WebUtility.HtmlDecode(link.Groups["href"].Value), out var uri))
                        continue;
                    if (download == null && uri.AbsolutePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        download = uri.AbsoluteUri;
                    else if (documentation == null
                             && RogueGamingEndpoints.IsHttpsOnHost(uri, RogueGamingEndpoints.PluginsHost)
                             && uri.AbsolutePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                        documentation = uri.AbsoluteUri;
                }
                if (download == null || !Uri.TryCreate(download, UriKind.Absolute, out var downloadUri)) continue;
                var fileName = Path.GetFileName(downloadUri.AbsolutePath);
                if (!IsTrustedPluginDownload(downloadUri, fileName) || !seen.Add(fileName)) continue;

                plugins.Add(new RogueRustCatalogPlugin
                {
                    Name = UiTextSanitizer.Normalize(name.Length > 0 ? name : Path.GetFileNameWithoutExtension(fileName),
                        UiTextSanitizer.MaxLabelLength),
                    Category = UiTextSanitizer.Normalize(category.Length > 0 ? category : "Plugins",
                        UiTextSanitizer.MaxLabelLength),
                    Version = UiTextSanitizer.Normalize(version, 32),
                    Description = UiTextSanitizer.Normalize(description, 1_024),
                    FileName = fileName,
                    DownloadUrl = downloadUri.AbsoluteUri,
                    DocumentationUrl = documentation ?? CatalogUri.AbsoluteUri,
                });
            }
        }
        return plugins;
    }

    public static bool IsTrustedPluginDownload(Uri uri, string fileName) =>
        RogueGamingEndpoints.IsHttpsOnHost(uri, RogueGamingEndpoints.PluginsHost)
        && string.IsNullOrEmpty(uri.Query)
        && IsValidPluginFileName(fileName)
        && uri.AbsolutePath.Equals("/plugins/" + fileName, StringComparison.Ordinal);

    public static bool IsValidPluginFileName(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && PluginFileNameRegex().IsMatch(fileName);

    public static (string Name, string Author, string Version)? ReadInfoAttribute(string source)
    {
        var match = InfoRegex().Match(source);
        return match.Success
            ? (match.Groups["name"].Value, match.Groups["author"].Value, match.Groups["version"].Value)
            : null;
    }

    private static bool LooksLikePlugin(string source) =>
        source.Contains("namespace Oxide.Plugins", StringComparison.Ordinal)
        || source.Contains("namespace Carbon.Plugins", StringComparison.Ordinal);

    /// <summary>Compares dotted versions numerically; non-numeric parts compare as 0.</summary>
    public static int CompareVersions(string? left, string? right)
    {
        static int[] Parse(string? value) => (value ?? string.Empty).Trim().TrimStart('v', 'V')
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(new string(part.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
            .ToArray();
        var a = Parse(left);
        var b = Parse(right);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static string? ReadInstalledVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(stream.Length, 64 * 1024)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return ReadInfoAttribute(DecodeUtf8(buffer.AsSpan(0, read).ToArray()))?.Version ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    private static void BackupExisting(string frameworkRoot, string destination)
    {
        try
        {
            var backupDirectory = Path.Combine(frameworkRoot, "highpop-plugin-backups");
            Directory.CreateDirectory(backupDirectory);
            var stem = Path.GetFileNameWithoutExtension(destination);
            File.Copy(destination,
                Path.Combine(backupDirectory, $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}.cs.bak"),
                overwrite: true);
            foreach (var old in new DirectoryInfo(backupDirectory).GetFiles($"{stem}-*.cs.bak")
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(BackupsToKeep))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<byte[]> GetCappedAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // Redirects must stay on the trusted host.
        var final = response.RequestMessage?.RequestUri;
        if (final != null && !RogueGamingEndpoints.IsHttpsOnHost(final, RogueGamingEndpoints.PluginsHost))
            throw new HttpRequestException($"The download was redirected away from {RogueGamingEndpoints.PluginsHost}.");
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new IOException($"The download from {uri.Host} exceeds {maxBytes / (1024 * 1024)} MB.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new IOException($"The download from {uri.Host} exceeds {maxBytes / (1024 * 1024)} MB.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static string FirstGroup(Regex regex, string input)
    {
        var match = regex.Match(input);
        return match.Success ? match.Groups["v"].Value : string.Empty;
    }

    private static string CleanText(string html) =>
        WhitespaceRegex().Replace(WebUtility.HtmlDecode(TagRegex().Replace(html, " ")), " ").Trim();

    [GeneratedRegex(@"<section\b[^>]*class=""[^""]*\bcategory\b[^""]*""[^>]*>\s*<h2[^>]*>(?<title>.*?)</h2>(?<body>.*?)</section>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CategoryRegex();
    [GeneratedRegex(@"<article\b[^>]*class=""[^""]*\bcard\b[^""]*""[^>]*>(?<body>.*?)</article>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CardRegex();
    [GeneratedRegex(@"<h3[^>]*>(?<v>.*?)</h3>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex H3Regex();
    [GeneratedRegex(@"<span\b[^>]*class=""[^""]*\bversion\b[^""]*""[^>]*>(?<v>.*?)</span>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
    [GeneratedRegex(@"<p[^>]*>(?<v>.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphRegex();
    [GeneratedRegex(@"<a\b[^>]*\bhref=""(?<href>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();
    [GeneratedRegex(@"^[A-Za-z0-9_]{1,96}\.cs$")]
    private static partial Regex PluginFileNameRegex();
    [GeneratedRegex(@"\[Info\(\s*""(?<name>[^""]*)""\s*,\s*""(?<author>[^""]*)""\s*,\s*""(?<version>[^""]*)""")]
    private static partial Regex InfoRegex();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
