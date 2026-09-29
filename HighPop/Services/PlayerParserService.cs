using System.Text.Json;
using HighPop.Models;

namespace HighPop.Services;

/// <summary>Parses Rust's WebRCON <c>playerlist</c> JSON response.</summary>
public static class PlayerParserService
{
    public static List<OnlinePlayer> ParseRustPlayerList(string response)
        => TryParseRustPlayerList(response, out var players) ? players : [];

    public static bool TryParseRustPlayerList(string response, out List<OnlinePlayer> result)
    {
        result = [];
        if (string.IsNullOrWhiteSpace(response)) return false;

        try
        {
            using var first = JsonDocument.Parse(response);
            JsonDocument? nested = null;
            var root = first.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                nested = JsonDocument.Parse(root.GetString() ?? "[]");
                root = nested.RootElement;
            }
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProperty(root, "Message", out var message))
                {
                    nested?.Dispose();
                    nested = message.ValueKind == JsonValueKind.String
                        ? JsonDocument.Parse(message.GetString() ?? "[]")
                        : JsonDocument.Parse(message.GetRawText());
                    root = nested.RootElement;
                }
                else if (TryGetProperty(root, "Players", out var players))
                {
                    nested?.Dispose();
                    nested = JsonDocument.Parse(players.GetRawText());
                    root = nested.RootElement;
                }
            }
            if (root.ValueKind != JsonValueKind.Array)
            {
                nested?.Dispose();
                return false;
            }

            foreach (var item in root.EnumerateArray())
            {
                var name = ReadText(item, "DisplayName");
                var steamId = ReadText(item, "SteamID");
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(steamId)) continue;
                result.Add(new OnlinePlayer
                {
                    Name = name,
                    SteamId = steamId,
                    Ping = ReadInt(item, "Ping"),
                    ConnectedSeconds = ReadInt(item, "ConnectedSeconds"),
                });
            }
            nested?.Dispose();
            return true;
        }
        catch
        {
            result = [];
            return false;
        }
    }

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

    private static string ReadText(JsonElement item, string property)
    {
        if (!TryGetProperty(item, property, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static int ReadInt(JsonElement item, string property)
    {
        if (!TryGetProperty(item, property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), out number) ? number : 0;
    }
}
