using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace HighPop.Services;

public static class SelfUpdateService
{
    public static async Task<bool> DownloadAndPrepareAsync(
        string zipUrl,
        IProgress<(int pct, string msg)>? progress = null)
    {
        var exePath = Environment.ProcessPath
                      ?? Path.Combine(AppContext.BaseDirectory, "HighPop.exe");
        var exeDir  = Path.GetDirectoryName(exePath)!;

        var updateDir = Path.Combine(exeDir, "assets", "data", "update");
        Directory.CreateDirectory(updateDir);
        var tempZip    = Path.Combine(updateDir, "_highpop_update.zip");
        var newExePath = Path.Combine(updateDir, "_highpop_new.exe");
        var batPath    = Path.Combine(updateDir, "_highpop_update.bat");

        try
        {
            Report(progress, 5, "Downloading update...");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            http.Timeout = TimeSpan.FromMinutes(5);
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("HighPop", UpdateCheckerService.GetCurrentVersion()));

            var bytes = await http.GetByteArrayAsync(zipUrl);
            var checksumText = await http.GetStringAsync(zipUrl + ".sha256");
            var expectedHash = checksumText.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (string.IsNullOrWhiteSpace(expectedHash)
                || !actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release checksum verification failed.");
            await File.WriteAllBytesAsync(tempZip, bytes);
            Report(progress, 60, "Extracting...");

            using var zip = ZipFile.OpenRead(tempZip);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("HighPop.exe", StringComparison.OrdinalIgnoreCase));

            if (entry == null)
                throw new InvalidOperationException("HighPop.exe was not found inside the release zip.");
            if (entry.Length < 1_000_000)
                throw new InvalidDataException("The release executable is unexpectedly small.");

            if (File.Exists(newExePath)) File.Delete(newExePath);
            entry.ExtractToFile(newExePath);
            Report(progress, 80, "Preparing update script...");

            var pid = Environment.ProcessId;
            var bat = $@"@echo off
:wait
tasklist /fi ""PID eq {pid}"" 2>nul | find ""{pid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait
)
move /y ""{newExePath}"" ""{exePath}""
start """" ""{exePath}""
del ""{tempZip}"" 2>nul
del ""%~f0""
";
            await File.WriteAllTextAsync(batPath, bat);
            Report(progress, 100, "Ready — restarting...");

            return true;
        }
        catch (Exception ex)
        {
            Report(progress, 0, $"Error: {ex.Message}");
            try { if (File.Exists(tempZip))    File.Delete(tempZip); }    catch { }
            try { if (File.Exists(newExePath)) File.Delete(newExePath); } catch { }
            try { if (File.Exists(batPath))    File.Delete(batPath); }    catch { }
            return false;
        }
    }

    public static bool ApplyAndRestart()
    {
        var exeDir  = Path.GetDirectoryName(
            Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "HighPop.exe"))!;
        var batPath = Path.Combine(exeDir, "assets", "data", "update", "_highpop_update.bat");

        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName        = batPath,
                UseShellExecute = true,
                WindowStyle     = ProcessWindowStyle.Hidden,
            });
        }
        catch { }

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (proc != null && !proc.HasExited) break;
            System.Threading.Thread.Sleep(100);
        }

        if (proc == null || proc.HasExited)
        {
            try { if (File.Exists(batPath)) File.Delete(batPath); } catch { }
            return false;
        }

        System.Windows.Application.Current.Shutdown();
        return true;
    }

    public static bool CleanupLeftovers()
    {
        var exeDir = Path.GetDirectoryName(
            Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "HighPop.exe"))!;

        var updateDir = Path.Combine(exeDir, "assets", "data", "update");
        var updateFailed = File.Exists(Path.Combine(updateDir, "_highpop_new.exe"));

        foreach (var name in new[] { "_highpop_update.bat", "_highpop_update.zip", "_highpop_new.exe" })
        {
            try
            {
                var path = Path.Combine(updateDir, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        return updateFailed;
    }

    private static void Report(IProgress<(int, string)>? p, int pct, string msg)
        => p?.Report((pct, msg));
}
