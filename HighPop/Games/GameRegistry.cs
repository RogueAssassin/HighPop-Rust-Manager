using System.IO;
using System.Text.Json;

namespace HighPop.Games;

/// <summary>
/// Registry for the production Rust profile and user-created local profiles.
/// </summary>
public static class GameRegistry
{
    private static readonly Dictionary<string, IGamePlugin> Plugins = new();

    static GameRegistry() => Register(new RustPlugin());

    public static void Register(IGamePlugin plugin) => Plugins[plugin.GameId] = plugin;

    public static void Unregister(string gameId) => Plugins.Remove(gameId);

    public static IGamePlugin? Get(string gameId)
        => Plugins.TryGetValue(gameId, out var plugin) ? plugin : null;

    public static IEnumerable<IGamePlugin> All => Plugins.Values;

    public static IEnumerable<IGrouping<string, IGamePlugin>> ByCategory
        => Plugins.Values.GroupBy(plugin => plugin.Category);

    public static void LoadCustomPlugins(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var definitions = JsonSerializer.Deserialize<List<CustomGameDefinition>>(
                File.ReadAllText(path), JsonOptions());
            if (definitions == null) return;
            foreach (var definition in definitions)
                if (!string.IsNullOrWhiteSpace(definition.GameId)
                    && !definition.GameId.Equals("rust", StringComparison.OrdinalIgnoreCase))
                    Register(new CustomGamePlugin(definition));
        }
        catch { }
    }

    public static void SaveCustomPlugin(CustomGameDefinition definition, string path)
    {
        if (definition.GameId.Equals("rust", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The built-in Rust profile cannot be replaced by a custom profile.");

        var definitions = ListCustomPlugins(path);
        var index = definitions.FindIndex(item =>
            item.GameId.Equals(definition.GameId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) definitions[index] = definition;
        else definitions.Add(definition);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(definitions, JsonOptions(writeIndented: true)));
        File.Move(temp, path, overwrite: true);
    }

    public static List<CustomGameDefinition> ListCustomPlugins(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<CustomGameDefinition>>(
                File.ReadAllText(path), JsonOptions()) ?? [];
        }
        catch { return []; }
    }

    public static void RemoveCustomPlugin(string gameId, string path)
    {
        if (gameId.Equals("rust", StringComparison.OrdinalIgnoreCase)) return;
        Unregister(gameId);
        var definitions = ListCustomPlugins(path);
        definitions.RemoveAll(item => item.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(definitions, JsonOptions(writeIndented: true)));
        File.Move(temp, path, overwrite: true);
    }

    private static JsonSerializerOptions JsonOptions(bool writeIndented = false) => new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = writeIndented,
    };
}
