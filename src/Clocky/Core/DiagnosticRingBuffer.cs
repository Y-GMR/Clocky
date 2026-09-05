using System;
using System.Collections.Generic;

namespace Clocky.Core;

public record DiagnosticEvent(
    DateTime Timestamp,
    string Subsystem,
    string Message,
    string? ExceptionDetails
);

public static class DiagnosticRingBuffer
{
    private const int Capacity = 200;
    private static readonly Queue<DiagnosticEvent> _events = new(Capacity);
    private static readonly object _lock = new();

    public static void Log(string subsystem, string message, Exception? ex = null)
    {
        string? exDetails = ex != null ? $"{ex.GetType().Name}: {ex.Message}" : null;
        var entry = new DiagnosticEvent(DateTime.UtcNow, subsystem, message, exDetails);

        lock (_lock)
        {
            if (_events.Count >= Capacity)
            {
                _events.Dequeue();
            }
            _events.Enqueue(entry);
        }
    }

    public static void Log(string subsystem, Exception ex)
    {
        Log(subsystem, ex.Message, ex);
    }

    public static IReadOnlyList<DiagnosticEvent> GetRecentEvents()
    {
        lock (_lock)
        {
            return _events.ToArray();
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }
}
