// Runs HighPop.Core's Linux code paths on a real host and prints PASS/FAIL for each.
// Offline checks always run; --steamcmd and --oxide download from Valve and GitHub.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using HighPop;
using HighPop.Games;
using HighPop.Services;

// Same platforms HighPop.Core declares; without this every Core call is flagged by the analyzer.
[assembly: SupportedOSPlatform("windows")]
[assembly: SupportedOSPlatform("linux")]

const string Usage = """
    HighPop.LinuxCheck [--steamcmd] [--oxide] [--all] [--any-os] [--keep]
      (no flags)  offline checks: secrets, CPU/RAM/drives, network-by-process
      --steamcmd  download SteamCMD from Valve, extract it and run "steamcmd.sh +quit" (needs lib32gcc-s1)
      --oxide     download the Oxide Linux build from GitHub, verify its SHA-256 and install it into a fake server folder
      --all       both of the above
      --any-os    run on a non-Linux OS (developer sanity run; Linux-only checks are skipped)
      --keep      keep the temporary work folder
    """;

if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Usage); return 0; }
var runSteamCmd = args.Contains("--steamcmd") || args.Contains("--all");
var runOxide = args.Contains("--oxide") || args.Contains("--all");
var anyOs = args.Contains("--any-os");

Console.WriteLine($"HighPop {AppInfo.VersionDisplay} Linux check");
Console.WriteLine($"OS:   {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
Console.WriteLine($"Host: {Environment.MachineName}, {Environment.ProcessorCount} CPU(s), .NET {Environment.Version}");
Console.WriteLine();
if (!OperatingSystem.IsLinux() && !anyOs)
{
    Console.WriteLine("This check is for Linux. Use --any-os only for a developer sanity run.");
    return 2;
}

// --selfupdate-apply <archive-url> <app-folder>: downloads and stages an update for a test install, starts
// the swap script and exits, so the whole Linux self-update path can be tried without a GitHub release.
var selfUpdate = Array.IndexOf(args, "--selfupdate-apply");
if (selfUpdate >= 0 && args.Length > selfUpdate + 2)
{
    var updateUrl = args[selfUpdate + 1];
    var appFolder = Path.GetFullPath(args[selfUpdate + 2]);
    var staged = await LinuxSelfUpdateService.DownloadAndPrepareAsync(
        updateUrl, new Progress<(int pct, string msg)>(p => Console.WriteLine($"  [{p.pct}%] {p.msg}")), appFolder, requireGitHub: false);
    if (!staged) { Console.WriteLine("[FAIL] update was not staged"); return 1; }
    var started = LinuxSelfUpdateService.StartApply(appFolder, Path.Combine(appFolder, "HighPop.Desktop"), Environment.ProcessId);
    Console.WriteLine(started ? "[PASS] swap script started; this process now exits" : "[FAIL] swap script did not start");
    return started ? 0 : 1;
}

var workRoot = Path.Combine(Path.GetTempPath(), "highpop-linuxcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(workRoot);
// Use a throwaway key file so the check never touches a real ~/.config/highpop/secret.key.
Environment.SetEnvironmentVariable("HIGHPOP_KEY_FILE", Path.Combine(workRoot, "keys", "secret.key"));

var passed = 0;
var failed = 0;
async Task Check(string name, Func<Task<(bool Ok, string Detail)>> body)
{
    bool ok;
    string detail;
    try { (ok, detail) = await body(); }
    catch (Exception ex) { ok = false; detail = $"{ex.GetType().Name}: {ex.Message}"; }
    if (ok) passed++; else failed++;
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
    foreach (var line in detail.Split('\n')) Console.WriteLine($"       {line.TrimEnd()}");
}

try
{
    await Check("Runs on Linux x64", () => Task.FromResult((
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
        $"{RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}")));

    await Check("Secrets: encrypted with a private key file", () =>
    {
        const string secret = "check-secret-123";
        var sealedText = EncryptionService.Encrypt(secret);
        var roundTrip = EncryptionService.Decrypt(sealedText);
        var keyPath = EncryptionService.DefaultKeyPath();
        var detail = $"value starts with {sealedText[..Math.Min(5, sealedText.Length)]}, round trip {(roundTrip == secret ? "ok" : "WRONG")}";
        if (!OperatingSystem.IsLinux())
            return Task.FromResult((roundTrip == secret, detail + " (file modes skipped: not Linux)"));
        var keyMode = File.GetUnixFileMode(keyPath);
        var dirMode = File.GetUnixFileMode(Path.GetDirectoryName(keyPath)!);
        const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        var ok = sealedText.StartsWith("lnx1:") && !sealedText.Contains(secret) && roundTrip == secret
                 && new FileInfo(keyPath).Length == 32
                 && keyMode == (UnixFileMode.UserRead | UnixFileMode.UserWrite)
                 && (dirMode & others) == 0;
        return Task.FromResult((ok, $"{detail}; key file mode {keyMode}, folder mode {dirMode}"));
    });

    await Check("CPU, memory and drives from /proc", async () =>
    {
        using var metrics = new SystemMetricsService();
        float peakCpu = 0;
        // One dedicated thread per CPU: the thread pool can be slow to start this many busy threads.
        var burners = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new Thread(() =>
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(7)) { }
        }) { IsBackground = true }).ToList();
        burners.ForEach(thread => thread.Start());
        while (burners.Any(thread => thread.IsAlive))
        {
            peakCpu = Math.Max(peakCpu, metrics.CpuPercent);
            await Task.Delay(500);
        }
        await Task.Delay(500);
        var gcTotalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
        var ramOk = metrics.RamTotalMb > 0 && metrics.RamUsedMb > 0 && metrics.RamUsedMb <= metrics.RamTotalMb
                    && Math.Abs(metrics.RamTotalMb - gcTotalMb) <= gcTotalMb * 0.25;
        var cpuOk = peakCpu >= 25 && peakCpu <= 100;
        var drives = metrics.Drives;
        var virtualMount = drives.FirstOrDefault(d => new[] { "/proc", "/sys", "/dev", "/run", "/snap" }
            .Any(prefix => d.Name.StartsWith(prefix, StringComparison.Ordinal)));
        var drivesOk = drives.Count > 0 && drives.All(d => d.TotalGb > 0) && virtualMount == null;
        return (ramOk && cpuOk && drivesOk,
            $"CPU peak under load {peakCpu:F0}% (expected 25-100)\n" +
            $"RAM {metrics.RamUsedMb} / {metrics.RamTotalMb} MB (runtime reports {gcTotalMb} MB total)\n" +
            $"Drives: {string.Join(", ", drives.Select(d => $"{d.Name} {d.UsedGb}/{d.TotalGb} GB"))}");
    });

    await Check("Network: this process's sockets are found by PID", async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var accepted = await listener.AcceptTcpClientAsync();
            var pid = Environment.ProcessId;
            var listening = ProcessNetworkTracker.GetListeningPortsForPid(pid);
            var counts = ProcessNetworkTracker.GetConnectionCountsByPid();
            counts.TryGetValue(pid, out var mine);
            return (listening.Contains(port) && mine >= 3,
                $"listening on {port}: found {string.Join(",", listening)}; connections owned by PID {pid}: {mine} (expected 3 or more)");
        }
        finally { listener.Stop(); }
    });

    if (runSteamCmd)
        await Check("SteamCMD: download, extract and start", async () =>
        {
            var dir = Path.Combine(workRoot, "steamcmd");
            Directory.CreateDirectory(dir);
            var archive = Path.Combine(dir, HostPlatform.SteamCmdArchiveName);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                await File.WriteAllBytesAsync(archive,
                    await http.GetByteArrayAsync(HostPlatform.SteamCmdDownloadBase + HostPlatform.SteamCmdArchiveName));
            SteamCmdService.ExtractSteamCmdArchive(archive, dir);
            var script = Path.Combine(dir, HostPlatform.SteamCmdExecutableName);
            if (!File.Exists(script)) return (false, $"{HostPlatform.SteamCmdExecutableName} was not extracted");
            if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(script) & UnixFileMode.UserExecute) == 0)
                return (false, $"{HostPlatform.SteamCmdExecutableName} is not executable");

            var output = new StringBuilder();
            var psi = new ProcessStartInfo(script, "+quit")
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
            {
                process.Kill(entireProcessTree: true);
                return (false, "steamcmd.sh +quit did not finish in 3 minutes");
            }
            process.WaitForExit();
            string tail;
            lock (output) tail = string.Join("\n", output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(4));
            var hint = new[] { "lib32gcc", "No such file", "required file not found", "cannot execute" }
                           .Any(text => tail.Contains(text, StringComparison.OrdinalIgnoreCase))
                ? "\nHint: install the 32-bit runtime first (Ubuntu/Debian: sudo apt install lib32gcc-s1)."
                : string.Empty;
            return (process.ExitCode == 0, $"exit code {process.ExitCode}; last output:\n{tail}{hint}");
        });

    if (runOxide)
        await Check("Oxide: Linux build downloaded, SHA-256 verified and installed", async () =>
        {
            var server = Path.Combine(workRoot, "server");
            Directory.CreateDirectory(Path.Combine(server, "RustDedicated_Data", "Managed"));
            await File.WriteAllTextAsync(Path.Combine(server, HostPlatform.RustExecutableName), string.Empty);
            var progress = new Progress<(int pct, string msg)>(x => Console.WriteLine($"         {x.pct,3}% {x.msg}"));
            await new ModManagerService().InstallOxideAsync(GameRegistry.Get("rust")!, server, progress);
            var version = ModManagerService.GetInstalledOxideVersion(server);
            return (version != null, $"Oxide {version}; HighPop detects: {ModManagerService.GetDetectedFramework(server)}");
        });
}
finally
{
    if (args.Contains("--keep")) Console.WriteLine($"\nWork folder kept: {workRoot}");
    else { try { Directory.Delete(workRoot, recursive: true); } catch { } }
}

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed.");
if (!runSteamCmd || !runOxide)
    Console.WriteLine("Run with --all to include the SteamCMD and Oxide downloads.");
return failed == 0 ? 0 : 1;