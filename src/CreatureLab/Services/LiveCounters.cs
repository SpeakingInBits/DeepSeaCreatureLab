using System.Collections.Concurrent;

namespace CreatureLab.Services;

/// <summary>Which SignalR connections belong to visitor phones, for the "phones connected" counter.</summary>
public sealed class ConnectionTracker
{
    private readonly ConcurrentDictionary<string, string> _visitorConnections = new();

    public void AddVisitor(string connectionId, string clientId) => _visitorConnections[connectionId] = clientId;

    public void Remove(string connectionId) => _visitorConnections.TryRemove(connectionId, out _);

    /// <summary>Distinct devices, so a phone with two tabs open counts once.</summary>
    public int ConnectedPhones => _visitorConnections.Values.Distinct().Count();
}

/// <summary>
/// Counts messages flowing through the server (HTTP API calls, hub calls, and events
/// pushed to browsers) over the last minute, for the live system panel.
/// </summary>
public sealed class MessageCounter(TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _stamps = new();

    public void Record()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            _stamps.Enqueue(now);
            Trim(now);
        }
    }

    public int PerMinute()
    {
        lock (_gate)
        {
            Trim(time.GetUtcNow());
            return _stamps.Count;
        }
    }

    private void Trim(DateTimeOffset now)
    {
        while (_stamps.Count > 0 && now - _stamps.Peek() > Window) _stamps.Dequeue();
    }
}
