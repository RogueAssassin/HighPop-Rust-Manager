using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace HighPop.Services;

/// <summary>
/// Self-update for the Linux desktop app. The release asset is HighPop-vX.Y.Z-linux-x64.tar.gz with a
/// .sha256 beside it, holding one top-level HPRM/ folder (the self-contained publish output).
/// Staging happens while the app runs; a small shell script swaps the files after the app has exited,
/// rolls back if a move fails, and starts the app again.
/// </summary>
public static class LinuxSelfUpdateService
{
    public const string ArchiveSuffix = "-linux-x64.tar.gz";
    internal const string Root = "HPRM/";
    internal const string MainBinary = "HighPop.Desktop";

    private const long MaxExtractedBytes = 1_500_000_000;

    private static string AppDir => AppContext.BaseDirectory.TrimEnd('/');
    private static string UpdateDir(string appDir) => Path.Combine(appDir, "assets", "data", "update");

    /// <summary>True on Linux when the app folder is writable, so the swap can succeed.</summary>
    public static bool IsSupported() => IsSupported(AppDir);

    internal static bool IsSupported(string appDir)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(Path.Combine(appDir, MainBinary))) return false;
        try
        {
            Directory.CreateDirectory(UpdateDir(appDir));
            var probe = Path.Combine(UpdateDir(appDir), ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static Task<bool> DownloadAndPrepareAsync(string url, IProgress<(int pct, string msg)>? progress = null) =>
        DownloadAndPrepareAsync(url, progress, AppDir, requireGitHub: true);

    internal static async Task<bool> DownloadAndPrepareAsync(
        string url, IProgress<(int pct, string msg)>? progress, string appDir, bool requireGitHub)
    {
        var updateDir = UpdateDir(appDir);
        var staged = Path.Combine(updateDir, "_staged");
        var script = Path.Combine(updateDir, "_apply_update.sh");
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new InvalidDataException("The update address is not valid.");
            if (requireGitHub && !(uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Updates are only downloaded from github.com over HTTPS.");

            Directory.CreateDirectory(updateDir);
            progress?.Report((5, "Downloading update..."));
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HighPop", UpdateCheckerService.GetCurrentVersion()));

            var bytes = await http.GetByteArrayAsync(uri);
            var checksum = await http.GetStringAsync(url + ".sha256");
            var expected = checksum.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(expected)
                || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release checksum verification failed.");

            progress?.Report((60, "Extracting..."));
            ExtractToStaging(bytes, staged);

            progress?.Report((85, "Preparing update script..."));
            await File.WriteAllTextAsync(script, UpdateScript);
            SetMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            progress?.Report((100, "Ready, restarting..."));
            return true;
        }
        catch (Exception ex)
        {
            progress?.Report((0, $"Error: {ex.Message}"));
            TryDelete(staged);
            TryDelete(script);
            return false;
        }
    }

    /// <summary>Starts the swap script detached from this process. The caller then exits the app.</summary>
    public static bool StartApply() => StartApply(AppDir, Environment.ProcessPath ?? Path.Combine(AppDir, MainBinary), Environment.ProcessId);

    internal static bool StartApply(string appDir, string exePath, int pid)
    {
        var updateDir = UpdateDir(appDir);
        var script = Path.Combine(updateDir, "_apply_update.sh");
        var staged = Path.Combine(updateDir, "_staged");
        if (!File.Exists(script) || !Directory.Exists(staged)) return false;

        try
        {
            // setsid keeps the script alive after this app exits, even when it was started from a terminal.
            var info = new ProcessStartInfo("setsid") { UseShellExecute = false };
            foreach (var arg in new[] { "-f", "/bin/sh", script, appDir, staged, pid.ToString(), exePath })
                info.ArgumentList.Add(arg);
            using var proc = Process.Start(info);
            if (proc == null) return false;
            proc.WaitForExit(5000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Removes staging left behind by an earlier update.</summary>
    public static void CleanupLeftovers() => CleanupLeftovers(AppDir);

    internal static void CleanupLeftovers(string appDir)
    {
        var updateDir = UpdateDir(appDir);
        foreach (var name in new[] { "_staged", "_old", "_apply_update.sh" })
            TryDelete(Path.Combine(updateDir, name));
    }

    /// <summary>
    /// Extracts the HPRM/ tree into <paramref name="stagingDir"/>. Links, devices and any path outside
    /// the staging folder are refused, and the result must contain the app binary.
    /// </summary>
    internal static int ExtractToStaging(byte[] archive, string stagingDir)
    {
        TryDelete(stagingDir);
        Directory.CreateDirectory(stagingDir);
        var stagingRoot = Path.GetFullPath(stagingDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var files = 0;
        long total = 0;
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile
                    or TarEntryType.Directory or TarEntryType.ExtendedAttributes or TarEntryType.GlobalExtendedAttributes))
                throw new InvalidDataException($"The release contains an unsupported entry type: {entry.EntryType}.");
            if (entry.EntryType is TarEntryType.ExtendedAttributes or TarEntryType.GlobalExtendedAttributes) continue;

            var name = entry.Name.Replace('\\', '/');
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if (!TryGetSafeRelativePath(name, out var relative)) continue;
            if (relative.Length == 0) continue;

            var destination = Path.GetFullPath(Path.Combine(stagingDir, relative));
            if (!destination.StartsWith(stagingRoot, StringComparison.Ordinal))
                throw new InvalidDataException("The release contains an unsafe path.");

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            total += entry.Length;
            if (total > MaxExtractedBytes) throw new InvalidDataException("The release is unexpectedly large.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            var keep = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                       | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                       | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            SetMode(destination, (entry.Mode & keep) | UnixFileMode.UserRead | UnixFileMode.UserWrite);
            files++;
        }

        if (!File.Exists(Path.Combine(stagingDir, MainBinary)) || !File.Exists(Path.Combine(stagingDir, MainBinary + ".dll")))
            throw new InvalidDataException("The release does not contain the HighPop desktop app.");
        // Archives made on Windows lose the execute bit, so make sure the app can start.
        SetMode(Path.Combine(stagingDir, MainBinary),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return files;
    }

    /// <summary>Returns the path below HPRM/, "" for the HPRM folder itself, and false for anything outside it.</summary>
    internal static bool TryGetSafeRelativePath(string entryName, out string relative)
    {
        relative = string.Empty;
        var name = entryName.TrimEnd('/');
        if (name == "HPRM") return true;
        if (!(name + "/").StartsWith(Root, StringComparison.Ordinal)) return false;
        var rest = name[Root.Length..];
        if (rest.StartsWith('/') || rest.Split('/').Any(part => part is "" or "." or "..")) return false;
        relative = rest;
        return true;
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Arguments: app folder, staged folder, pid of the running app, path to restart.
    // Program files are moved in whole (the old ones are kept aside and restored if a move fails);
    // user-visible assets are only added, never overwritten.
    private const string UpdateScript = """
        #!/bin/sh
        APP="$1"; STAGED="$2"; PID="$3"; EXE="$4"
        while kill -0 "$PID" 2>/dev/null; do sleep 1; done
        OLD="$APP/assets/data/update/_old"
        rm -rf "$OLD"; mkdir -p "$OLD"
        failed=0
        for src in "$STAGED"/* "$STAGED"/.[!.]*; do
          [ -e "$src" ] || continue
          name=$(basename "$src")
          [ "$name" = "assets" ] && continue
          if [ -e "$APP/$name" ]; then mv -f "$APP/$name" "$OLD/$name" || { failed=1; break; }; fi
          mv -f "$src" "$APP/$name" || { failed=1; break; }
        done
        if [ "$failed" = 1 ]; then
          for old in "$OLD"/* "$OLD"/.[!.]*; do
            [ -e "$old" ] || continue
            n=$(basename "$old"); rm -rf "$APP/$n"; mv -f "$old" "$APP/$n"
          done
        elif [ -d "$STAGED/assets" ]; then
          mkdir -p "$APP/assets"
          cp -a --update=none "$STAGED/assets/." "$APP/assets/" 2>/dev/null || cp -an "$STAGED/assets/." "$APP/assets/"
          # The assets README is documentation, so it always matches the installed version.
          [ -f "$STAGED/assets/README.txt" ] && cp -f "$STAGED/assets/README.txt" "$APP/assets/README.txt" 2>/dev/null
        fi
        rm -rf "$OLD" "$STAGED"
        nohup "$EXE" >/dev/null 2>&1 &
        rm -f "$0"
        """;
}
