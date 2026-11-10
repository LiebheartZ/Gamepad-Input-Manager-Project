namespace GDM.Models;

public enum DiagnosticLogLevel
{
    Information,
    Warning,
    Error,
    Critical
}

public sealed record DiagnosticLogEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public DiagnosticLogLevel Level { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Details { get; init; }
}
