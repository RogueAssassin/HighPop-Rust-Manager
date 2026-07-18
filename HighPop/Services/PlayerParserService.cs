using System.Text.Json;
using HighPop.Models;

namespace HighPop.Services;

/// <summary>Parses Rust's WebRCON <c>playerlist</c> JSON response.</summary>
public static class PlayerParserService
{
    public static List<OnlinePlayer> ParseRustPlayerList(string response)
    {
        var result = new List<OnlinePlayer>();
        if (string.IsNullOrWhiteSpace(response)) return result;

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
            if (root.ValueKind != JsonValueKind.Array) return result;

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
        }
        catch { }

        return result;
    }

    private static string ReadText(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static int ReadInt(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), out number) ? number : 0;
    }
}
