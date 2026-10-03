namespace HighPop.Models;

using HighPop.Services;

public enum ConsoleMessageType { Info, Warning, Error, System, Input }

public class ConsoleMessage
{
    private string _text = string.Empty;
    private string _source = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Text
    {
        get => _text;
        set => _text = UiTextSanitizer.Normalize(value);
    }
    public ConsoleMessageType Type { get; set; } = ConsoleMessageType.Info;
    public string Source
    {
        get => _source;
        set => _source = UiTextSanitizer.Normalize(value, UiTextSanitizer.MaxLabelLength);
    }

    public string FormattedTime => Timestamp.ToString("HH:mm:ss");
    public string SourceLabel => string.IsNullOrWhiteSpace(Source) ? "HighPop" : Source;
}
