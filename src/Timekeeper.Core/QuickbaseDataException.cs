namespace Timekeeper.Core;

/// <summary>A bad Quickbase field, with a safe link to its source record when known.</summary>
public sealed class QuickbaseDataException(string message, string? recordUrl = null) : InvalidOperationException(message)
{
    public string? RecordUrl { get; } = recordUrl;
}
