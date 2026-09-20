using System.Text;
using System.Text.RegularExpressions;
using HighPop.Models;

namespace HighPop.Services;

public sealed record ParsedConsoleLine(string Text, ConsoleMessageType Type, string Source);

public static class ConsoleOutputParser
{
    private static readonly Regex Ansi = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))",
        RegexOptions.Compiled);
    private static readonly Regex Error = new(
        @"(?i)(?:\b(?:error|exception|fatal|failed|failure|panic|crash)\b|NullReferenceException|StackTrace)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BenignErrorCount = new(
        @"(?i)(?:\b0\s+errors?\b|\bno\s+errors?\b|\bwithout\s+errors?\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Warning = new(
        @"(?i)\b(?:warn|warning|deprecated|retrying|timeout)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ParsedConsoleLine? Parse(
        string? raw,
        bool fromStandardError,
        ConsoleMessageType previousType = ConsoleMessageType.Info)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var withoutAnsi = Ansi.Replace(raw.Replace('\r', ' '), string.Empty);
        var builder = new StringBuilder(withoutAnsi.Length);
        foreach (var ch in withoutAnsi)
            if (ch == '\t' || !char.IsControl(ch)) builder.Append(ch);
        var text = builder.ToString().TrimEnd();
        if (string.IsNullOrWhiteSpace(text)) return null;

        var continuation = text.StartsWith("   at ", StringComparison.Ordinal)
            || text.StartsWith("at ", StringComparison.Ordinal)
            || text.StartsWith("--- End of", StringComparison.Ordinal)
            || text.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase);
        var type = Error.IsMatch(text) && !BenignErrorCount.IsMatch(text)
            ? ConsoleMessageType.Error
            : Warning.IsMatch(text)
                ? ConsoleMessageType.Warning
                : continuation && previousType is ConsoleMessageType.Error or ConsoleMessageType.Warning
                    ? previousType
                    : ConsoleMessageType.Info;

        // Unity and Rust frequently use stderr for ordinary server output. Preserve the stream
        // as a source label, but classify severity from the content instead of painting it red.
        var source = DetectSource(text, fromStandardError);
        return new ParsedConsoleLine(text, type, source);
    }

    private static string DetectSource(string text, bool fromStandardError)
    {
        if (text.Contains("[RogueRust]", StringComparison.OrdinalIgnoreCase)) return "RogueRust";
        if (text.Contains("[Carbon]", StringComparison.OrdinalIgnoreCase)) return "Carbon";
        if (text.Contains("[Oxide]", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[uMod]", StringComparison.OrdinalIgnoreCase)) return "Oxide";
        if (text.StartsWith("[RCON]", StringComparison.OrdinalIgnoreCase)) return "WebRCON";
        if (text.StartsWith("[HighPop]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[PRE-FLIGHT]", StringComparison.OrdinalIgnoreCase)) return "HighPop";
        return fromStandardError ? "Rust stderr" : "Rust";
    }
}
