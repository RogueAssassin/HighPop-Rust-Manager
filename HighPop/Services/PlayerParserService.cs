using System.Text.Json;
using HighPop.Models;

namespace HighPop.Services;

/// <summary>Parses Rust's WebRCON <c>playerlist</c> JSON response.</summary>
public static class PlayerParserService
{
    private static readonly string[] WrapperProperties = ["Message", "Players", "Content", "Data"];

    public static List<OnlinePlayer> ParseRustPlayerList(string response)
        => TryParseRustPlayerList(response, out var players) ? players : [];

    public static bool TryParseRustPlayerList(string response, out List<OnlinePlayer> result)
    {
        result = [];
        if (string.IsNullOrWhiteSpace(response)) return false;

        foreach (var candidate in GetJsonCandidates(response))
        {
            if (!TryParseCandidate(candidate, depth: 0, out result)) continue;
            return true;
        }

        result = [];
        return false;
    }

    private static IEnumerable<string> GetJsonCandidates(string response)
    {
        var normalized = response.Trim().TrimStart('\uFEFF', '\0');
        yield return normalized;

        // Some RCON bridges prepend the command name or a log label before the JSON.
        // Rust's playerlist is an array, so safely retry only the bounded array section.
        var arrayStart = normalized.IndexOf('[');
        var arrayEnd = normalized.LastIndexOf(']');
        if (arrayStart > 0 && arrayEnd > arrayStart)
            yield return normalized[arrayStart..(arrayEnd + 1)];
    }

    private static bool TryParseCandidate(string candidate, int depth, out List<OnlinePlayer> result)
    {
        result = [];
        try
        {
            using var document = JsonDocument.Parse(candidate);
            return TryParseElement(document.RootElement, depth, out result);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseElement(JsonElement element, int depth, out List<OnlinePlayer> result)
    {
        result = [];
        if (depth > 5) return false;

        if (element.ValueKind == JsonValueKind.String)
        {
            var nested = element.GetString();
            if (string.IsNullOrWhiteSpace(nested)) return false;
            foreach (var candidate in GetJsonCandidates(nested))
            {
                if (TryParseCandidate(candidate, depth + 1, out result)) return true;
            }
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in WrapperProperties)
            {
                if (TryGetProperty(element, property, out var wrapped)
                    && TryParseElement(wrapped, depth + 1, out result))
                    return true;
            }

            // Accept a single player object from compatible RCON proxies.
            if (LooksLikePlayer(element))
            {
                AddPlayer(element, result);
                return result.Count == 1;
            }
            return false;
        }

        if (element.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                AddPlayer(item, result);
        }

        // An empty JSON array is a valid playerlist response for an empty server.
        return result.Count > 0 || element.GetArrayLength() == 0;
    }

    private static bool LooksLikePlayer(JsonElement item) =>
        HasProperty(item, "SteamID") || HasProperty(item, "DisplayName") || HasProperty(item, "Name");

    private static void AddPlayer(JsonElement item, List<OnlinePlayer> result)
    {
        var name = UiTextSanitizer.Normalize(
            ReadText(item, "DisplayName", "Name", "Username"), UiTextSanitizer.MaxLabelLength);
        var steamId = UiTextSanitizer.Normalize(
            ReadText(item, "SteamID", "SteamId", "Steam64ID", "Id"), UiTextSanitizer.MaxLabelLength);
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(steamId)) return;

        result.Add(new OnlinePlayer
        {
            Name = name,
            SteamId = steamId,
            Ping = ReadInt(item, "Ping", "Latency"),
            ConnectedSeconds = ReadInt(item, "ConnectedSeconds", "ConnectionSeconds", "Duration"),
        });
    }

    private static bool HasProperty(JsonElement item, string property) =>
        TryGetProperty(item, property, out _);

    private static bool TryGetProperty(JsonElement item, string property, out JsonElement value)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in item.EnumerateObject())
            {
                if (!candidate.Name.Equals(property, StringComparison.OrdinalIgnoreCase)) continue;
                value = candidate.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string ReadText(JsonElement item, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (!TryGetProperty(item, property, out var value)) continue;
            return value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : value.ToString();
        }
        return string.Empty;
    }

    private static int ReadInt(JsonElement item, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (!TryGetProperty(item, property, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt32(out var number)) return Math.Max(0, number);
                if (value.TryGetDouble(out var floating))
                    return (int)Math.Clamp(floating, 0, int.MaxValue);
            }
            if (double.TryParse(value.ToString(), out var parsed))
                return (int)Math.Clamp(parsed, 0, int.MaxValue);
        }
        return 0;
    }
}
