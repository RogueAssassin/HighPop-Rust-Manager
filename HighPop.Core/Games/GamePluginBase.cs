using System.IO;
using HighPop.Models;

namespace HighPop.Games;

public abstract class GamePluginBase : IGamePlugin
{
    public abstract string GameId { get; }
    public abstract string GameName { get; }
    public abstract string Description { get; }
    public abstract string Category { get; }
    public abstract int SteamAppId { get; }
    public abstract string Executable { get; }
    public abstract int DefaultPort { get; }
    public abstract int DefaultQueryPort { get; }
    public abstract int DefaultMaxPlayers { get; }
    public virtual string SteamBranch => "public";
    public virtual bool HasRcon => true;
    public virtual bool SupportsOxide => false;
    public virtual List<string> ConfigFiles => [];

    public string? GetSteamInstalledBuildId(GameServer server)
    {
        try
        {
            var path = Path.Combine(server.InstallPath, "steamapps", $"appmanifest_{SteamAppId}.acf");
            if (!File.Exists(path)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(path), "\"buildid\"\\s*\"(\\d+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    protected virtual bool FilterUnityShaderNoise => false;

    public virtual bool IsNoiseLine(string line)
    {
        if (!FilterUnityShaderNoise) return false;
        return line.Contains("shader is not supported on this GPU")
            || line.Contains("Shader Unsupported:")
            || line.Contains("Shader Did you use #pragma only_renderers")
            || line.Contains("Shader If subshaders removal was intentional")
            || line.Contains("3D Noise requires higher shader capabilities")
            || line.Contains("Microsoft Media Foundation video decoding")
            || line.StartsWith("WARNING: Shader ");
    }

    public abstract string BuildStartArguments(GameServer server);
    public abstract Dictionary<string, string> GetDefaultSettings();
    public abstract List<ConfigField> GetConfigFields();
    public virtual string? GetStopCommand(GameServer server) => "quit";
    public virtual Task PreStartAsync(GameServer server) => Task.CompletedTask;
    public virtual string? ValidateBeforeStart(GameServer server) => null;

    public virtual string? GetKickCommand(string playerName) => null;
    public virtual string? GetKickCommand(string playerName, string reason) => GetKickCommand(playerName);
    public virtual string? GetBanCommand(string playerName) => null;
    public virtual string? GetBanCommand(string playerName, string reason) => GetBanCommand(playerName);
    public virtual string? GetUnbanCommand(string playerName) => null;
    public virtual string? GetPlayersCommand() => null;
    public virtual string EngineFamily => string.Empty;
    public virtual string? GetBroadcastCommand(string message) => RustRcon.Broadcast(message);

    public override string ToString() => GameName;

    protected static void WriteConfigIfMissing(string path, string content)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    protected string S(GameServer server, string key, string fallback = "")
        => server.GameSpecificSettings.TryGetValue(key, out var value) ? value : fallback;

    protected List<ConfigField> BaseFields() =>
    [
        new() { Key = "serverName", Label = "Server name", FieldType = ConfigFieldType.Text, DefaultValue = "HighPop Rust Server" },
        new() { Key = "maxPlayers", Label = "Max players", FieldType = ConfigFieldType.Number, DefaultValue = DefaultMaxPlayers.ToString(), Min = 1, Max = 1000 },
        new() { Key = "serverPass", Label = "Password", FieldType = ConfigFieldType.Password, DefaultValue = "" },
        new() { Key = "steamBranch", Label = "Steam branch", FieldType = ConfigFieldType.Text, DefaultValue = SteamBranch,
                Description = "Rust Dedicated Server SteamCMD branch. Leave as public for the stable release." },
    ];
}
