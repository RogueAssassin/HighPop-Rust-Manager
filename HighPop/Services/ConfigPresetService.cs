using System.IO;
using System.Text.Json;
using HighPop.Games;
using HighPop.Models;

namespace HighPop.Services;

public class ConfigPreset
{
    public string GameId      { get; set; } = string.Empty;
    public string Name        { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>Config file path relative to the server install directory.</summary>
    public string ConfigFile  { get; set; } = string.Empty;
    /// <summary>Optional Rust procedural map size used to order and describe map presets.</summary>
    public int? MapSize { get; set; }
    /// <summary>Key-value pairs to merge into the config file (KEY=VALUE format).</summary>
    public Dictionary<string, string> Values { get; set; } = [];
}

public class ConfigPresetService
{
    private static readonly string PresetsDir = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!, "assets", "presets");

    public List<ConfigPreset> GetPresetsForGame(string gameId)
    {
        if (!Directory.Exists(PresetsDir)) return [];
        var result = new List<ConfigPreset>();
        foreach (var file in Directory.GetFiles(PresetsDir, "*.json"))
        {
            try
            {
                var preset = JsonSerializer.Deserialize<ConfigPreset>(File.ReadAllText(file),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (preset != null && preset.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase))
                    result.Add(preset);
            }
            catch { }
        }
        return result
            .OrderBy(preset => preset.MapSize.HasValue ? 1 : 0)
            .ThenBy(preset => preset.MapSize ?? 0)
            .ThenBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Applies a preset to the server's config file.
    /// Backs up the original file first.
    /// Returns the path of the backup, or null if the config file was not found.
    /// </summary>
    public string? ApplyPreset(GameServer server, ConfigPreset preset)
    {
        var identity = server.GameSpecificSettings.TryGetValue("identity", out var configuredIdentity)
            ? configuredIdentity
            : "highpop";
        var relativePath = preset.ConfigFile.Replace("{identity}", identity, StringComparison.OrdinalIgnoreCase);
        var root = Path.GetFullPath(server.InstallPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var configPath = Path.GetFullPath(Path.Combine(server.InstallPath, relativePath));
        if (!configPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Preset config path must stay inside the server installation folder.");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        // Backup original
        var backupPath = string.Empty;
        if (File.Exists(configPath))
        {
            backupPath = configPath + $".bak_{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Copy(configPath, backupPath, overwrite: true);
        }
        else
        {
            File.WriteAllText(configPath, "# Created by HighPop Rust Manager\n");
        }

        var lines = File.ReadAllLines(configPath).ToList();

        foreach (var (key, value) in preset.Values)
        {
            var matches = new List<int>();
            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                // Skip comments
                if (trimmed.StartsWith("//") || trimmed.StartsWith("#") || trimmed.StartsWith(";"))
                    continue;

                // Match KEY=VALUE or KEY VALUE
                var sep = trimmed.IndexOf('=');
                if (sep < 0) sep = trimmed.IndexOf(' ');
                if (sep < 0) continue;

                var lineKey = trimmed[..sep].Trim();
                if (!lineKey.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                matches.Add(i);
            }

            if (matches.Count == 0)
            {
                lines.Add($"{key} {value}");
                continue;
            }

            // Rust uses the last active assignment. Keep that location authoritative and
            // preserve older duplicates as audit comments instead of leaving two live values.
            foreach (var duplicateIndex in matches.Take(matches.Count - 1))
                lines[duplicateIndex] = $"# Duplicate removed by HighPop preset: {lines[duplicateIndex].Trim()}";

            var authoritativeIndex = matches[^1];
            var authoritative = lines[authoritativeIndex];
            var authoritativeTrimmed = authoritative.TrimStart();
            var indent = authoritative.Length - authoritativeTrimmed.Length;
            var leadingSpaces = authoritative[..indent];
            var usesEquals = authoritativeTrimmed.IndexOf('=') >= 0;
            lines[authoritativeIndex] = usesEquals
                    ? $"{leadingSpaces}{key}={value}"
                    : $"{leadingSpaces}{key} {value}";
        }

        File.WriteAllLines(configPath, lines);
        SynchronizeLaunchSettings(server, preset);
        if (server.GameId.Equals("rust", StringComparison.OrdinalIgnoreCase))
            RustPlugin.LoadServerConfigVariables(server);
        return string.IsNullOrEmpty(backupPath) ? configPath : backupPath;
    }

    private static void SynchronizeLaunchSettings(GameServer server, ConfigPreset preset)
    {
        if (preset.Values.TryGetValue("server.maxplayers", out var maxPlayersText)
            && int.TryParse(maxPlayersText, out var maxPlayers) && maxPlayers > 0)
            server.MaxPlayers = maxPlayers;

        var mapSizeText = preset.Values.TryGetValue("server.worldsize", out var configuredMapSize)
            ? configuredMapSize
            : preset.MapSize?.ToString();
        if (int.TryParse(mapSizeText, out var mapSize) && mapSize is >= 1000 and <= 6000)
            server.GameSpecificSettings["worldSize"] = mapSize.ToString();

        if (preset.Values.TryGetValue("server.saveinterval", out var saveIntervalText)
            && int.TryParse(saveIntervalText, out var saveInterval) && saveInterval > 0)
            server.GameSpecificSettings["saveInterval"] = saveInterval.ToString();
    }
}
