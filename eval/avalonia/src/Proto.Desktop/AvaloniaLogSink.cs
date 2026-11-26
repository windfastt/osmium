using Avalonia.Logging;

namespace Proto.Desktop;

/// <summary>Avalonia's own log: warnings and errors from every area, plus the platform and rendering notes.</summary>
internal sealed class AvaloniaLogSink : ILogSink
{
    public bool IsEnabled(LogEventLevel level, string area) =>
        level >= LogEventLevel.Warning || (level >= LogEventLevel.Information && area is "Win32Platform" or "Visual" or "OpenGL" or "Platform");

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        EventLog.Write("avalonia-log", new { level = level.ToString(), area, source = source?.GetType().Name, message = messageTemplate });

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        EventLog.Write("avalonia-log", new
        {
            level = level.ToString(), area, source = source?.GetType().Name, message = messageTemplate,
            values = propertyValues.Select(v => v?.ToString()).ToArray(),
        });
}
