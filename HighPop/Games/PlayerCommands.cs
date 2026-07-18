namespace HighPop.Games;

/// <summary>Sanitized Rust WebRCON commands used by HighPop's moderation tools.</summary>
internal static class RustRcon
{
    public const string Family = "rust";

    public static string Kick(string player) => $"kick \"{Clean(player)}\" \"Kicked by admin\"";
    public static string Kick(string player, string reason) => $"kick \"{Clean(player)}\" \"{Clean(reason)}\"";
    public static string Ban(string player) => $"ban \"{Clean(player)}\" \"Banned by admin\"";
    public static string Ban(string player, string reason) => $"ban \"{Clean(player)}\" \"{Clean(reason)}\"";
    public static string Unban(string player) => $"unban \"{Clean(player)}\"";
    public static string Players() => "playerlist";
    public static string Broadcast(string message) => $"say \"{Clean(message)}\"";

    private static string Clean(string? value) => (value ?? string.Empty)
        .Replace("\r", " ")
        .Replace("\n", " ")
        .Replace("\"", "'")
        .Trim();
}
