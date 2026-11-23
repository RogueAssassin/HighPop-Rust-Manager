using System.Net.Http.Headers;
using System.Security.Cryptography;
using HighPop.Models;
using HighPop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HighPop.Server;

internal static class Cli
{
    private const string Usage = """
        HighPop headless host

        highpop-server run                       run the supervisor (use this from systemd)
        highpop-server setup [--port N]          enable the Web API and create a token
        highpop-server list                      show server profiles
        highpop-server add <name> [--path DIR] [--autostart]
                                                 create a Rust server profile
        highpop-server install <name|id>         install or update Rust with SteamCMD
        highpop-server start|stop|restart <name|id>
                                                 control a server through the running host
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var cmd = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        try
        {
            return cmd switch
            {
                "run" => await new Host(HostServices.Build()).RunAsync(),
                "setup" => Setup(args),
                "list" => List(),
                "add" => Add(args),
                "install" => await InstallAsync(args),
                "start" or "stop" or "restart" => await ControlAsync(cmd, args),
                _ => Help()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int Help() { Console.WriteLine(Usage); return 0; }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static List<GameServer> Load(ConfigService config) => config.LoadServers()
        .Where(s => s.GameId.Equals("rust", StringComparison.OrdinalIgnoreCase)).ToList();

    private static ServerPortSet PortSetFor(GameServer server)
    {
        var rustPlus = server.GameSpecificSettings.TryGetValue("appPort", out var text)
            && int.TryParse(text, out var parsed) ? parsed : 0;
        return new ServerPortSet(server.ServerPort, server.QueryPort, server.RconPort, rustPlus);
    }

    private static GameServer Resolve(IEnumerable<GameServer> servers, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Give a server name or id.");
        var match = servers.FirstOrDefault(s => s.Id == key)
            ?? servers.FirstOrDefault(s => s.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException($"No server called '{key}'. Try 'highpop-server list'.");
    }

    private static int Setup(string[] args)
    {
        var config = new ConfigService();
        config.WebApiEnabled = true;
        if (args.Contains("--allow-remote")) config.WebApiAllowRemote = true;
        if (int.TryParse(Option(args, "--port"), out var port) && port is > 0 and < 65536) config.WebApiPort = port;
        if (string.IsNullOrWhiteSpace(config.WebApiToken))
            config.WebApiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        config.Save();
        Console.WriteLine($"Web API enabled on port {config.WebApiPort} ({(config.WebApiBindAll ? "network access allowed" : "this machine only; add --allow-remote to reach it from other computers")}).");
        Console.WriteLine($"The access token is stored in {config.SettingsFile} (kept private; not printed here).");
        Console.WriteLine("Restart the host for the change to take effect.");
        return 0;
    }

    private static int List()
    {
        var config = new ConfigService();
        var servers = Load(config);
        if (servers.Count == 0) { Console.WriteLine("No server profiles yet. Add one with 'highpop-server add <name>'."); return 0; }
        foreach (var s in servers)
        {
            var installed = File.Exists(Path.Combine(s.InstallPath, HostPlatform.RustExecutableName)) ? "installed" : "not installed";
            Console.WriteLine($"{s.DisplayName,-24} {s.Id}  port {s.ServerPort}  rcon {s.RconPort}  {installed}  {(s.AutoStart ? "autostart" : "manual")}");
        }
        return 0;
    }

    private static int Add(string[] args)
    {
        var name = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Give the server a name.");
        var config = new ConfigService();
        var servers = Load(config);
        if (servers.Any(s => s.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A server called '{name}' already exists.");

        var existing = servers.Select(PortSetFor).ToList();
        var ports = ServerPortAllocator.FindAvailable(
                new ServerPortSet(28015, 28017, 28016, 28082), existing, PortCheckerService.IsAvailable)
            ?? throw new InvalidOperationException("Could not find a free set of ports.");

        var path = Option(args, "--path") ?? Path.Combine(config.DefaultInstallRoot, name);
        var server = new GameServer
        {
            GameId = "rust",
            DisplayName = name,
            ServerName = name,
            InstallPath = Path.GetFullPath(path),
            ServerPort = ports.Game,
            QueryPort = ports.Query,
            RconPort = ports.Rcon,
            RconPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            MaxPlayers = 100,
            AutoStart = args.Contains("--autostart"),
        };
        server.GameSpecificSettings["appPort"] = ports.RustPlus.ToString();
        servers.Add(server);
        config.SaveServers(servers);
        Console.WriteLine($"Added '{name}' ({server.Id}) in {server.InstallPath}, game port {ports.Game}, RCON port {ports.Rcon}.");
        Console.WriteLine($"Next: highpop-server install \"{name}\"");
        return 0;
    }

    private static async Task<int> InstallAsync(string[] args)
    {
        using var sp = HostServices.Build();
        var config = sp.GetRequiredService<ConfigService>();
        var server = Resolve(Load(config), args.Skip(1).FirstOrDefault());
        await Installer.InstallAsync(sp, server, Console.WriteLine);
        return 0;
    }

    private static async Task<int> ControlAsync(string action, string[] args)
    {
        var config = new ConfigService();
        if (!config.WebApiRequired || string.IsNullOrWhiteSpace(config.WebApiToken))
            throw new InvalidOperationException("The Web API is off. Run 'highpop-server setup' and restart the host.");
        var server = Resolve(Load(config), args.Skip(1).FirstOrDefault());

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.WebApiToken);
        var url = $"http://127.0.0.1:{config.WebApiPort}/api/servers/{server.Id}/{action}";
        using var response = await http.PostAsync(url, new StringContent("{}"));
        Console.WriteLine(response.IsSuccessStatusCode
            ? $"{action} sent to '{server.DisplayName}'."
            : $"The host answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
}
