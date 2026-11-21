using Newtonsoft.Json;

namespace HighPop.Services;

/// <summary>
/// GameServer initialises collections such as the default server.cfg variables. With
/// Newtonsoft's default (Auto) handling, loading a saved profile appends its items to those
/// defaults and duplicates every variable, which then fails start validation. Profile
/// loading, importing and cloning must replace the collections instead.
/// </summary>
internal static class ProfileJson
{
    public static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ObjectCreationHandling = ObjectCreationHandling.Replace
    });

    /// <summary>
    /// Earlier builds saved profiles whose variables were doubled by the merge described above.
    /// Keep the last copy of each name, which is the one that carries the saved value.
    /// </summary>
    public static void RepairDuplicateVariables(Models.GameServer server)
    {
        var variables = server.RustServerVariables;
        if (variables == null) return;
        var keep = variables
            .Select((variable, index) => (variable, index))
            .Where(item => !string.IsNullOrWhiteSpace(item.variable.Name))
            .GroupBy(item => item.variable.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Take(group.Count() - 1))
            .Select(item => item.variable)
            .ToList();
        foreach (var duplicate in keep) variables.Remove(duplicate);
    }

    public static T? Clone<T>(T value) => JToken(value).ToObject<T>(Serializer);

    private static Newtonsoft.Json.Linq.JToken JToken<T>(T value) =>
        Newtonsoft.Json.Linq.JToken.FromObject(value!);
}