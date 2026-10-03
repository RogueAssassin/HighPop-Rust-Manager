using System.Text;

namespace HighPop.Services;

/// <summary>
/// Keeps external server text inside the subset WPF/DirectWrite can shape safely.
/// Rust, plugins, remote APIs, and WebRCON are all untrusted text boundaries.
/// </summary>
public static class UiTextSanitizer
{
    public const int MaxConsoleTextLength = 16_384;
    public const int MaxLabelLength = 256;
    private const string TruncatedMarker = " … [truncated by HPRM]";

    public static string Normalize(string? value, int maxLength = MaxConsoleTextLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength <= 0) return string.Empty;

        var requiresRewrite = value.Length > maxLength;
        for (var index = 0; !requiresRewrite && index < value.Length; index++)
        {
            var ch = value[index];
            if (char.IsHighSurrogate(ch))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    index++;
                    continue;
                }
                requiresRewrite = true;
            }
            else if (char.IsLowSurrogate(ch) || (char.IsControl(ch) && ch != '\t'))
            {
                requiresRewrite = true;
            }
        }

        if (!requiresRewrite) return value;

        var truncated = value.Length > maxLength;
        var contentLimit = truncated
            ? Math.Max(0, maxLength - TruncatedMarker.Length)
            : maxLength;
        var builder = new StringBuilder(Math.Min(maxLength, value.Length));

        for (var index = 0; index < value.Length && builder.Length < contentLimit; index++)
        {
            var ch = value[index];
            if (char.IsHighSurrogate(ch))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    if (builder.Length + 2 > contentLimit)
                    {
                        truncated = true;
                        break;
                    }
                    builder.Append(ch);
                    builder.Append(value[++index]);
                }
                else
                {
                    builder.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(ch))
            {
                builder.Append('\uFFFD');
            }
            else if (char.IsControl(ch) && ch != '\t')
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(ch);
            }
        }

        if (truncated && builder.Length + TruncatedMarker.Length <= maxLength)
            builder.Append(TruncatedMarker);
        return builder.ToString();
    }
}
