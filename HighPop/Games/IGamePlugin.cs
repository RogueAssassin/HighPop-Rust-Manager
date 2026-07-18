using HighPop.Models;

namespace HighPop.Games;

/// <summary>
/// Describes the Rust Dedicated Server integration used throughout HighPop.
/// The interface keeps the runtime boundary explicit without exposing support
/// for unrelated games or install systems.
/// </summary>
public interface IGamePlugin
{
    string GameId { get; }
    string GameName { get; }
    string Description { get; }
    string Category { get; }
    int SteamAppId { get; }
    string Executable { get; }
    int DefaultPort { get; }
    int DefaultQueryPort { get; }
    int DefaultMaxPlayers { get; }
    string SteamBranch { get; }
    bool HasRcon { get; }
    bool SupportsOxide { get; }
    List<string> ConfigFiles { get; }

    bool IsNoiseLine(string line);
    string BuildStartArguments(GameServer server);
    Dictionary<string, string> GetDefaultSettings();
    List<ConfigField> GetConfigFields();
    string? GetSteamInstalledBuildId(GameServer server);
    string? GetStopCommand(GameServer server);
    Task PreStartAsync(GameServer server);
    string? ValidateBeforeStart(GameServer server);

    string? GetKickCommand(string playerName);
    string? GetKickCommand(string playerName, string reason);
    string? GetBanCommand(string playerName);
    string? GetBanCommand(string playerName, string reason);
    string? GetUnbanCommand(string playerName);
    string? GetPlayersCommand();
    string? GetBroadcastCommand(string message);
    string EngineFamily { get; }
}

public class ConfigField
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ConfigFieldType FieldType { get; set; } = ConfigFieldType.Text;
    public string DefaultValue { get; set; } = string.Empty;
    public string[]? Options { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
}

public enum ConfigFieldType { Text, Number, Password, Toggle, Dropdown, Slider }
