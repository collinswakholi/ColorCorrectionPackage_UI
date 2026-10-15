using Avalonia;

namespace ColorCorrect.App.Models;

public enum LogLevel
{
    Debug,
    Info,
    Success,
    Warning,
    Error,
}

public sealed class LogEntry
{
    public string Timestamp { get; init; } = "";
    public string Message { get; init; } = "";
    public LogLevel Level { get; init; } = LogLevel.Info;
    public bool IsDetail { get; init; }

    public string TimeLabel => string.IsNullOrEmpty(Timestamp) ? "" : $"[{Timestamp}]";
    public string DisplayLine => IsDetail ? Message : $"{TimeLabel}  {Message}";
    public Thickness BlockMargin => IsDetail ? new Thickness(0, 0, 0, 1) : new Thickness(0, 8, 0, 2);

    public string Icon => Level switch
    {
        LogLevel.Debug => "·",
        LogLevel.Info => "i",
        LogLevel.Success => "✓",
        LogLevel.Warning => "!",
        LogLevel.Error => "×",
        _ => "",
    };
}
