using System.Collections.Concurrent;

namespace CreatureLab.Services;

/// <summary>
/// Which SignalR connections belong to visitor phones: for the "phones connected" counter, and
/// so a release is only accepted from a phone that has the draw page open and connected.
/// </summary>
public sealed class ConnectionTracker(TimeProvider time)
{
    /// <summary>How long after its connection drops a phone may still release (a cellular blip shouldn't block anyone).</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(2);
    private const int PruneThreshold = 5_000;

    private readonly ConcurrentDictionary<string, string> _visitorConnections = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new();

    public void AddVisitor(string connectionId, string clientId) => _visitorConnections[connectionId] = clientId;

    public void Remove(string connectionId)
    {
        if (!_visitorConnections.TryRemove(connectionId, out var clientId)) return;

        var now = time.GetUtcNow();
        _lastSeen[clientId] = now;
        if (_lastSeen.Count > PruneThreshold)
        {
            foreach (var (id, at) in _lastSeen)
            {
                if (now - at > RecentWindow) _lastSeen.TryRemove(id, out _);
            }
        }
    }

    /// <summary>Distinct devices, so a phone with two tabs open counts once.</summary>
    public int ConnectedPhones => _visitorConnections.Values.Distinct().Count();

    /// <summary>True if this device is connected now, or was within <see cref="RecentWindow"/>.</summary>
    public bool ConnectedRecently(string clientId) =>
        _visitorConnections.Values.Contains(clientId) ||
        (_lastSeen.TryGetValue(clientId, out var at) && time.GetUtcNow() - at <= RecentWindow);
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
