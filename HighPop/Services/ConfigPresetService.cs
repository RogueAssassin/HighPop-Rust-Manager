using System.IO;
using System.Text.Json;
using HighPop.Models;

namespace HighPop.Services;

public class ConfigPreset
{
    public string GameId      { get; set; } = string.Empty;
    public string Name        { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>Config file path relative to the server install directory.</summary>
    public string ConfigFile  { get; set; } = string.Empty;
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
        return result;
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
            bool found = false;
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

                // Preserve original line indent
                var indent = lines[i].Length - lines[i].TrimStart().Length;
                var leadingSpaces = lines[i][..indent];
                var usesEquals = trimmed.IndexOf('=') >= 0;
                lines[i] = usesEquals
                    ? $"{leadingSpaces}{key}={value}"
                    : $"{leadingSpaces}{key} {value}";
                found = true;
                break;
            }

            if (!found)
                lines.Add($"{key} {value}");
        }

        File.WriteAllLines(configPath, lines);
        return string.IsNullOrEmpty(backupPath) ? configPath : backupPath;
    }
}
