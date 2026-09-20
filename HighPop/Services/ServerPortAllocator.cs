namespace HighPop.Services;

public readonly record struct ServerPortSet(int Game, int Query, int Rcon, int RustPlus)
{
    public IEnumerable<(int Port, string Protocol, string Label)> Endpoints()
    {
        yield return (Game, "UDP", "Game");
        yield return (Query, "UDP", "Query");
        yield return (Rcon, "TCP", "WebRCON");
        yield return (RustPlus, "TCP", "Rust+");
    }
}

public static class ServerPortAllocator
{
    public static IReadOnlyList<string> Validate(
        ServerPortSet candidate,
        IEnumerable<ServerPortSet> existing,
        Func<int, string, bool>? isAvailable = null)
    {
        var errors = new List<string>();
        var endpoints = candidate.Endpoints().ToList();
        foreach (var endpoint in endpoints.Where(endpoint => endpoint.Port is <= 0 or > 65535))
            errors.Add($"{endpoint.Label} port must be between 1 and 65535.");

        foreach (var duplicate in endpoints.Where(endpoint => endpoint.Port > 0)
                     .GroupBy(endpoint => endpoint.Port).Where(group => group.Count() > 1))
            errors.Add($"Port {duplicate.Key} is assigned more than once in this server profile.");

        var reserved = existing.SelectMany(set => set.Endpoints())
            .Where(endpoint => endpoint.Port > 0)
            .GroupBy(endpoint => endpoint.Port)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(item => item.Label).Distinct()));
        foreach (var endpoint in endpoints.Where(endpoint => endpoint.Port > 0))
        {
            if (reserved.TryGetValue(endpoint.Port, out var owner))
                errors.Add($"{endpoint.Label} port {endpoint.Port} is already assigned ({owner}).");
            else if (isAvailable != null && !isAvailable(endpoint.Port, endpoint.Protocol))
                errors.Add($"{endpoint.Label} port {endpoint.Port}/{endpoint.Protocol} is already in use on Windows.");
        }

        return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static ServerPortSet? FindAvailable(
        ServerPortSet preferred,
        IEnumerable<ServerPortSet> existing,
        Func<int, string, bool>? isAvailable = null,
        int maxAttempts = 5000)
    {
        var existingSnapshot = existing.ToList();
        for (var offset = 0; offset < maxAttempts; offset++)
        {
            var candidate = new ServerPortSet(
                preferred.Game + offset,
                preferred.Query > 0 ? preferred.Query + offset : 0,
                preferred.Rcon > 0 ? preferred.Rcon + offset : 0,
                preferred.RustPlus > 0 ? preferred.RustPlus + offset : 0);
            if (Validate(candidate, existingSnapshot, isAvailable).Count == 0)
                return candidate;
        }
        return null;
    }
}
