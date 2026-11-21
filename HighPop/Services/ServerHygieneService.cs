using System.IO;
using System.IO.Compression;
using HighPop.Games;
using HighPop.Models;

namespace HighPop.Services;

public class JunkItem
{
    public string Path        { get; set; } = string.Empty;
    public bool   IsDirectory { get; set; }
    public long   SizeBytes   { get; set; }
    public string Description { get; set; } = string.Empty;
    public string SizeText => SizeBytes > 1_000_000
        ? $"{SizeBytes / 1_000_000.0:F1} MB"
        : $"{SizeBytes / 1_000.0:F0} KB";
}

public sealed record LogArchiveResult(
    string? ArchivePath,
    int ArchivedFiles,
    long ArchivedBytes,
    int DeleteFailures,
    string? Error,
    int TruncatedFiles = 0)
{
    public bool Succeeded => Error == null;
}

/// <summary>
/// Finds leftover junk in a server's install directory that's safe to delete while the
/// Rust server is stopped: old log files, crash dumps, and stray SteamCMD temp files.
/// Oxide and Carbon data is always excluded.
/// </summary>
public class ServerHygieneService
{
    internal const int RetainedLogArchives = 2;
    /// <summary>
    /// A runaway log (a plugin printing in a loop) can reach tens of gigabytes. Zipping all of
    /// it would stall the start for many minutes and fill the disk, so only the newest part of
    /// each oversized file is kept; that is the part that explains what went wrong.
    /// </summary>
    internal const long MaxArchivedBytesPerLog = 64L * 1024 * 1024;
    private readonly string _logBackupRoot;

    public ServerHygieneService(ConfigService config)
        : this(Path.Combine(config.AssetsPath, "logsbackup")) { }

    internal ServerHygieneService(string logBackupRoot)
    {
        _logBackupRoot = logBackupRoot;
    }

    /// <summary>
    /// Archives the completed Rust, Oxide, and Carbon log generations before a new process
    /// starts. Source files are removed only after the ZIP has been closed successfully, and
    /// each profile retains a fixed two-start history.
    /// </summary>
    public LogArchiveResult ArchiveLogsForStart(GameServer server)
        => ArchiveLogsForStart(server, DateTimeOffset.Now);

    /// <summary>Size at which a running server's logs are reported as a likely runaway.</summary>
    public const long RunawayLogWarningBytes = 1024L * 1024 * 1024;

    private static (string Label, string Path)[] GetLogRoots(GameServer server) =>
    [
        ("server", RustPlugin.GetEffectiveLogDirectory(server)),
        ("oxide", Path.Combine(server.InstallPath, "oxide", "logs")),
        ("carbon", Path.Combine(server.InstallPath, "carbon", "logs")),
    ];

    /// <summary>
    /// Returns the largest file in the Rust, Oxide and Carbon log folders and the folders'
    /// total size. Unreadable folders are skipped; this only feeds a warning.
    /// </summary>
    public static (string? LargestFile, long LargestBytes, long TotalBytes) MeasureLogs(GameServer server)
    {
        string? largest = null;
        long largestBytes = 0, total = 0;
        foreach (var root in GetLogRoots(server))
        {
            try
            {
                if (!Directory.Exists(root.Path)) continue;
                foreach (var file in new DirectoryInfo(root.Path).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    total += file.Length;
                    if (file.Length > largestBytes) { largestBytes = file.Length; largest = file.FullName; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (largest, largestBytes, total);
    }

    internal LogArchiveResult ArchiveLogsForStart(GameServer server, DateTimeOffset timestamp)
    {
        var roots = GetLogRoots(server);
        var candidates = new List<(string FullPath, string EntryName, long Length)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var root in roots)
            {
                if (!Directory.Exists(root.Path)) continue;
                foreach (var file in Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories))
                {
                    var fullPath = Path.GetFullPath(file);
                    if (!seen.Add(fullPath)) continue;
                    var relative = Path.GetRelativePath(root.Path, fullPath)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    candidates.Add((fullPath, $"{root.Label}/{relative}", new FileInfo(fullPath).Length));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LogArchiveResult(null, 0, 0, 0,
                $"Log discovery failed; existing logs were left untouched: {ex.Message}");
        }

        var profileDirectory = Path.Combine(_logBackupRoot, BuildProfileDirectoryName(server));
        if (candidates.Count == 0)
        {
            PruneArchives(profileDirectory);
            return new LogArchiveResult(null, 0, 0, 0, null);
        }

        var stamp = timestamp.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss-fff");
        var finalPath = Path.Combine(profileDirectory, $"{stamp}.zip");
        if (File.Exists(finalPath))
            finalPath = Path.Combine(profileDirectory, $"{stamp}_{Guid.NewGuid():N}.zip");
        var temporaryPath = finalPath + ".tmp";

        try
        {
            Directory.CreateDirectory(profileDirectory);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var candidate in candidates)
                {
                    if (candidate.Length > MaxArchivedBytesPerLog)
                        AddLogTail(archive, candidate.FullPath, candidate.EntryName);
                    else
                        archive.CreateEntryFromFile(candidate.FullPath, candidate.EntryName, CompressionLevel.Fastest);
                }
            }

            File.Move(temporaryPath, finalPath);
            try { File.SetLastWriteTimeUtc(finalPath, timestamp.UtcDateTime); } catch { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            return new LogArchiveResult(null, 0, 0, 0,
                $"Log archive failed; existing logs were left untouched: {ex.Message}");
        }

        var deleteFailures = 0;
        foreach (var candidate in candidates)
        {
            try { File.Delete(candidate.FullPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                deleteFailures++;
            }
        }

        foreach (var root in roots) DeleteEmptyChildren(root.Path);
        PruneArchives(profileDirectory);
        return new LogArchiveResult(
            finalPath,
            candidates.Count,
            candidates.Sum(candidate => candidate.Length),
            deleteFailures,
            null,
            candidates.Count(candidate => candidate.Length > MaxArchivedBytesPerLog));
    }

    /// <summary>Stores only the last <see cref="MaxArchivedBytesPerLog"/> bytes of a runaway log.</summary>
    private static void AddLogTail(ZipArchive archive, string fullPath, string entryName)
    {
        var extension = Path.GetExtension(entryName);
        var tailName = entryName[..^extension.Length]
                       + $".last-{MaxArchivedBytesPerLog / (1024 * 1024)}MB" + extension;
        var entry = archive.CreateEntry(tailName, CompressionLevel.Fastest);
        using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        source.Seek(Math.Max(0, source.Length - MaxArchivedBytesPerLog), SeekOrigin.Begin);
        using var target = entry.Open();
        source.CopyTo(target);
    }

    private static string BuildProfileDirectoryName(GameServer server)
    {
        var name = string.IsNullOrWhiteSpace(server.DisplayName) ? server.ServerName : server.DisplayName;
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string((name ?? "server")
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "server";
        if (safeName.Length > 64) safeName = safeName[..64].TrimEnd(' ', '.');
        var id = new string((server.Id ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        if (id.Length > 8) id = id[..8];
        return string.IsNullOrEmpty(id) ? safeName : $"{safeName}_{id}";
    }

    private static void DeleteEmptyChildren(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void PruneArchives(string profileDirectory)
    {
        if (!Directory.Exists(profileDirectory)) return;
        try
        {
            foreach (var archive in Directory.EnumerateFiles(profileDirectory, "*.zip")
                         .Select(path => new FileInfo(path))
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                         .Skip(RetainedLogArchives))
            {
                try { archive.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public List<JunkItem> ScanJunk(GameServer server)
    {
        var items = new List<JunkItem>();
        if (!Directory.Exists(server.InstallPath)) return items;

        try
        {
            // Old log files
            foreach (var f in Directory.EnumerateFiles(server.InstallPath, "*.log", SearchOption.AllDirectories))
            {
                if (IsInModFolder(f)) continue;
                var fi = new FileInfo(f);
                items.Add(new JunkItem { Path = f, SizeBytes = fi.Length, Description = "Log file" });
            }

            // Stray crash dumps
            foreach (var f in Directory.EnumerateFiles(server.InstallPath, "*.dmp", SearchOption.AllDirectories))
            {
                if (IsInModFolder(f)) continue;
                var fi = new FileInfo(f);
                items.Add(new JunkItem { Path = f, SizeBytes = fi.Length, Description = "Crash dump" });
            }

            // Stray SteamCMD temp files left behind by an interrupted update
            foreach (var f in Directory.EnumerateFiles(server.InstallPath, "*.tmp", SearchOption.AllDirectories))
            {
                if (IsInModFolder(f)) continue;
                var fi = new FileInfo(f);
                items.Add(new JunkItem { Path = f, SizeBytes = fi.Length, Description = "Leftover temp file" });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return items;
    }

    // Rust mod frameworks own these folders; their logs and data are not disposable junk.
    private static readonly string[] ModFolderNames = ["oxide", "carbon"];

    private static bool IsInModFolder(string filePath)
    {
        var dir = System.IO.Path.GetDirectoryName(filePath) ?? "";
        return ModFolderNames.Any(name =>
            dir.Contains($"{System.IO.Path.DirectorySeparatorChar}{name}", StringComparison.OrdinalIgnoreCase));
    }

    public void DeleteJunk(IEnumerable<JunkItem> items)
    {
        foreach (var item in items)
        {
            try
            {
                if (item.IsDirectory) Directory.Delete(item.Path, recursive: true);
                else File.Delete(item.Path);
            }
            catch { }
        }
    }

    private static long DirSize(string path)
    {
        try { return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }
}
