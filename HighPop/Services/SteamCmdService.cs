using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HighPop.Services;

public class SteamCmdService
{
    private static readonly HttpClient _http = new();

    private readonly string _steamCmdDir;
    private readonly string _steamCmdExe;
    private Process? _currentProcess;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>(serverId, line) — serverId is "" for messages not tied to a specific server (e.g. downloading SteamCMD itself before any install starts).</summary>
    public event Action<string, string>? OutputReceived;
    public event Action<string, int>? ProgressChanged;
    public event Action<string, int, string>? ProgressDetailChanged;
    public event Action? Completed;

    public SteamCmdService(ConfigService config)
    {
        _steamCmdDir = config.SteamCmdPath;
        _steamCmdExe = Path.Combine(_steamCmdDir, "steamcmd.exe");
    }

    public bool IsInstalled => File.Exists(_steamCmdExe);

    public Task DownloadSteamCmdAsync(string serverId = "")
        => DownloadSteamCmdCoreAsync(serverId, 0, 100);

    private async Task DownloadSteamCmdCoreAsync(string serverId, int progressStart, int progressEnd)
    {
        await _downloadGate.WaitAsync();
        try
        {
            if (IsInstalled) return;
            Directory.CreateDirectory(_steamCmdDir);
            var zipPath = Path.Combine(_steamCmdDir, "steamcmd.zip");
            OutputReceived?.Invoke(serverId, "[HighPop] Downloading SteamCMD...");

            ReportProgress(serverId, progressStart, "Downloading SteamCMD");
            using var response = await _http.GetAsync(
                "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip",
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var destination = new FileStream(
                             zipPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 65_536, useAsync: true))
            {
                var buffer = new byte[65_536];
                long downloaded = 0;
                var lastReportedPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;
                    if (totalBytes is not > 0) continue;

                    var rawPercent = (int)Math.Clamp(downloaded * 100L / totalBytes.Value, 0, 100);
                    if (rawPercent == lastReportedPercent) continue;
                    lastReportedPercent = rawPercent;
                    var mapped = MapProgress(rawPercent, progressStart, Math.Max(progressStart, progressEnd - 2));
                    ReportProgress(serverId, mapped,
                        $"Downloading SteamCMD · {FormatBytes(downloaded)} / {FormatBytes(totalBytes.Value)}");
                }
            }

            OutputReceived?.Invoke(serverId, "[HighPop] Extracting SteamCMD...");
            ReportProgress(serverId, Math.Max(progressStart, progressEnd - 1), "Extracting SteamCMD");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, _steamCmdDir, overwriteFiles: true);
            File.Delete(zipPath);
            OutputReceived?.Invoke(serverId, "[HighPop] SteamCMD installed.");
            ReportProgress(serverId, progressEnd, "SteamCMD ready");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            try
            {
                var partialZip = Path.Combine(_steamCmdDir, "steamcmd.zip");
                if (File.Exists(partialZip)) File.Delete(partialZip);
            }
            catch { }
            OutputReceived?.Invoke(serverId, "[ERR] SteamCMD download failed: " + ex.Message);
            throw new InvalidOperationException("SteamCMD download failed", ex);
        }
        finally { _downloadGate.Release(); }
    }

    public async Task InstallOrUpdateAsync(string serverId, int appId, string installPath, string? login = null, string? password = null, string? branch = null)
    {
        if (!IsInstalled)
            await DownloadSteamCmdCoreAsync(serverId, 0, 10);
        else
            ReportProgress(serverId, 2, "SteamCMD ready");

        try
        {
            Directory.CreateDirectory(installPath);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Cannot create install directory: " + ex.Message, ex);
        }

        var loginArg  = (!string.IsNullOrWhiteSpace(login) && password != null)
            ? $"+login {QuoteToken(login)} {QuoteToken(password)}"
            : "+login anonymous";
        if (!string.IsNullOrEmpty(branch)
            && branch.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new InvalidOperationException("Steam branch contains unsupported characters.");
        var branchArg = string.IsNullOrEmpty(branch) ? "" : $" -beta {branch}";

        var args = $"+force_install_dir \"{installPath}\" {loginArg} +app_update {appId}{branchArg} validate +quit";
        await RunAsync(serverId, args, throwOnSteamError: true,
            progressStart: 10, progressEnd: 100);
    }

    /// <summary>
    /// Reads Steam's current branch build ID without modifying the installed server.
    /// A null result is deliberately non-destructive: callers must defer the update.
    /// </summary>
    public async Task<string?> GetLatestBuildIdAsync(string serverId, int appId, string? branch)
    {
        if (!IsInstalled) await DownloadSteamCmdAsync(serverId);

        var output = new List<string>();
        var outputLock = new object();
        void Capture(string line)
        {
            lock (outputLock) output.Add(line);
        }

        await RunAsync(
            serverId,
            $"+login anonymous +app_info_update 1 +app_info_print {appId} +quit",
            captureOutput: Capture,
            suppressOutput: true);

        string text;
        lock (outputLock) text = string.Join(Environment.NewLine, output);
        return TryParseBranchBuildId(text, branch, out var buildId) ? buildId : null;
    }

    public static bool TryParseBranchBuildId(string appInfo, string? branch, out string buildId)
    {
        buildId = string.Empty;
        if (string.IsNullOrWhiteSpace(appInfo)) return false;

        var safeBranch = string.IsNullOrWhiteSpace(branch) ? "public" : branch.Trim();
        var branchMatch = Regex.Match(
            appInfo,
            $"\"{Regex.Escape(safeBranch)}\"\\s*\\{{(?<body>.*?)\\}}",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!branchMatch.Success) return false;

        var buildMatch = Regex.Match(
            branchMatch.Groups["body"].Value,
            "\"buildid\"\\s*\"(?<id>\\d+)\"",
            RegexOptions.IgnoreCase);
        if (!buildMatch.Success) return false;

        buildId = buildMatch.Groups["id"].Value;
        return true;
    }

    public async Task RunAsync(
        string serverId,
        string arguments,
        bool throwOnSteamError = false,
        Action<string>? captureOutput = null,
        bool suppressOutput = false,
        int progressStart = 0,
        int progressEnd = 100)
    {
        await _runGate.WaitAsync();
        try
        {
        var psi = new ProcessStartInfo
        {
            FileName               = _steamCmdExe,
            Arguments              = arguments,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding  = System.Text.Encoding.UTF8,
        };

        string? steamError = null;
        var latestProgress = progressStart;
        if (!suppressOutput)
            ReportProgress(serverId, progressStart, "Starting SteamCMD");
        _currentProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _currentProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            captureOutput?.Invoke(e.Data);
            if (!suppressOutput)
            {
                OutputReceived?.Invoke(serverId, e.Data);
                if (TryParseProgress(e.Data, out var rawPercent, out var detail))
                {
                    var mapped = MapProgress(rawPercent, progressStart, Math.Max(progressStart, progressEnd - 2));
                    Interlocked.Exchange(ref latestProgress, mapped);
                    ReportProgress(serverId, mapped, detail);
                }
            }

            if (throwOnSteamError && e.Data.StartsWith("ERROR!", StringComparison.OrdinalIgnoreCase))
                steamError = e.Data;
        };
        _currentProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            captureOutput?.Invoke(e.Data);
            if (!suppressOutput) OutputReceived?.Invoke(serverId, "[ERR] " + e.Data);
        };

        try
        {
            _currentProcess.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            throw new InvalidOperationException(
                $"Failed to start SteamCMD process ({_steamCmdExe}): {ex.Message}", ex);
        }

        _currentProcess.BeginOutputReadLine();
        _currentProcess.BeginErrorReadLine();

        // Bounded heartbeat with elapsed time makes long validation phases visible without
        // flooding the console or blocking SteamCMD's output threads.
        using var cts = new CancellationTokenSource();
        var startedAt = Stopwatch.StartNew();
        var heartbeat = suppressOutput
            ? Task.CompletedTask
            : Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(15_000, cts.Token);
                        var elapsed = startedAt.Elapsed;
                        var detail = $"SteamCMD working · {elapsed:mm\\:ss} elapsed";
                        OutputReceived?.Invoke(serverId, $"[HighPop] {detail}");
                        ProgressDetailChanged?.Invoke(serverId, Volatile.Read(ref latestProgress), detail);
                    }
                }
                catch (TaskCanceledException) { }
            }, cts.Token);

        await _currentProcess.WaitForExitAsync();
        cts.Cancel();
        await heartbeat.ConfigureAwait(false);

        var exitCode = _currentProcess.ExitCode;
        Completed?.Invoke();

        if (steamError != null)
            throw new InvalidOperationException(steamError);

        if (exitCode != 0 && steamError == null)
        {
            var message = $"SteamCMD exited with code {exitCode}";
            OutputReceived?.Invoke(serverId, $"[HighPop] {message}");
            if (throwOnSteamError) throw new InvalidOperationException(message);
        }
        else if (exitCode == 0 && !suppressOutput)
        {
            ReportProgress(serverId, progressEnd, "SteamCMD update complete");
        }
        }
        finally
        {
            try { _currentProcess?.Dispose(); } catch { }
            _currentProcess = null;
            _runGate.Release();
        }
    }

    internal static bool TryParseProgress(string line, out int percent, out string detail)
    {
        percent = -1;
        detail = "SteamCMD working";
        if (string.IsNullOrWhiteSpace(line)) return false;

        // SteamCMD commonly writes `progress: 42.75 (bytes / bytes)` without a percent
        // symbol. Some builds include `%`, so accept both forms.
        var match = Regex.Match(
            line,
            @"progress\s*:\s*(?<pct>\d+(?:\.\d+)?)(?:\s*%)?",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success)
            match = Regex.Match(line, @"(?<pct>\d+(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant);
        if (!match.Success
            || !double.TryParse(match.Groups["pct"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value))
            return false;

        percent = (int)Math.Clamp(Math.Floor(value), 0, 100);
        detail = line.Contains("verifying", StringComparison.OrdinalIgnoreCase)
            ? $"Verifying Rust server files · {percent}%"
            : line.Contains("preallocating", StringComparison.OrdinalIgnoreCase)
                ? $"Preparing disk space · {percent}%"
                : line.Contains("committing", StringComparison.OrdinalIgnoreCase)
                    ? $"Committing Rust update · {percent}%"
                    : $"Downloading Rust server files · {percent}%";
        return true;
    }

    private void ReportProgress(string serverId, int percent, string detail)
    {
        percent = Math.Clamp(percent, 0, 100);
        ProgressChanged?.Invoke(serverId, percent);
        ProgressDetailChanged?.Invoke(serverId, percent, detail);
    }

    private static int MapProgress(int rawPercent, int start, int end)
    {
        rawPercent = Math.Clamp(rawPercent, 0, 100);
        start = Math.Clamp(start, 0, 100);
        end = Math.Clamp(end, start, 100);
        return start + (int)Math.Round((end - start) * rawPercent / 100d);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.##} {units[unit]}";
    }

    public void Cancel()
    {
        try
        {
            if (_currentProcess?.HasExited == false)
                _currentProcess.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    private static string QuoteToken(string value)
        => "\"" + value.Replace("\r", " ").Replace("\n", " ").Replace("\"", "\\\"") + "\"";
}
