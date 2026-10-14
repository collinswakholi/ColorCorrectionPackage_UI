using System;

namespace ColorCorrect.App.Services;

public enum ToastType
{
    Success,
    Error,
    Info,
    Warning,
}

public sealed record ToastRequest(string Message, ToastType Type, int DurationMs = 4000);

public static class ToastService
{
    private static string? _lastMessage;
    private static DateTime _lastAt;
    public static event Action<ToastRequest>? ToastRequested;

    public static void Show(string message, ToastType type = ToastType.Info, int durationMs = 4000)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (string.Equals(message, _lastMessage, StringComparison.Ordinal)
            && now - _lastAt < TimeSpan.FromSeconds(1.5))
        {
            return;
        }

        _lastMessage = message;
        _lastAt = now;
        ToastRequested?.Invoke(new ToastRequest(message, type, durationMs));
    }

    public static void ShowSuccess(string message, int durationMs = 4000) =>
        Show(message, ToastType.Success, durationMs);

    public static void ShowError(string message, int durationMs = 5000) =>
        Show(message, ToastType.Error, durationMs);

    public static void ShowWarning(string message, int durationMs = 4500) =>
        Show(message, ToastType.Warning, durationMs);

    public static void ShowInfo(string message, int durationMs = 3500) =>
        Show(message, ToastType.Info, durationMs);
}
