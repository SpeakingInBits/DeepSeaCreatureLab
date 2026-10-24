namespace CreatureLab.Services;

public enum RateLimitReason { None, Device, Network }

public readonly record struct RateLimitDecision(bool Allowed, TimeSpan RetryAfter, RateLimitReason Reason)
{
    public static RateLimitDecision Allow { get; } = new(true, TimeSpan.Zero, RateLimitReason.None);
}

/// <summary>
/// Limits submissions per device (one every N seconds) and per IP address (M per minute).
/// The per-IP budget is looser because a whole campus Wi-Fi network can share one IP.
/// </summary>
public sealed class SubmissionRateLimiter(TimeSpan perDevice, int maxPerIpPerMinute, TimeProvider time)
{
    private static readonly TimeSpan IpWindow = TimeSpan.FromMinutes(1);
    private const int PruneThreshold = 5_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastByDevice = [];
    private readonly Dictionary<string, Queue<DateTimeOffset>> _recentByIp = [];

    public RateLimitDecision TryAcquire(string clientId, string ip)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();

            if (_lastByDevice.TryGetValue(clientId, out var last) && now - last < perDevice)
            {
                return new RateLimitDecision(false, perDevice - (now - last), RateLimitReason.Device);
            }

            if (maxPerIpPerMinute > 0)
            {
                if (!_recentByIp.TryGetValue(ip, out var recent))
                {
                    recent = new Queue<DateTimeOffset>();
                    _recentByIp[ip] = recent;
                }

                while (recent.Count > 0 && now - recent.Peek() >= IpWindow) recent.Dequeue();

                if (recent.Count >= maxPerIpPerMinute)
                {
                    return new RateLimitDecision(false, IpWindow - (now - recent.Peek()), RateLimitReason.Network);
                }

                recent.Enqueue(now);
            }

            _lastByDevice[clientId] = now;
            PruneIfLarge(now);
            return RateLimitDecision.Allow;
        }
    }

    private void PruneIfLarge(DateTimeOffset now)
    {
        if (_lastByDevice.Count > PruneThreshold)
        {
            foreach (var (key, at) in _lastByDevice.ToList())
            {
                if (now - at >= perDevice) _lastByDevice.Remove(key);
            }
        }

        if (_recentByIp.Count > PruneThreshold)
        {
            foreach (var (key, queue) in _recentByIp.ToList())
            {
                while (queue.Count > 0 && now - queue.Peek() >= IpWindow) queue.Dequeue();
                if (queue.Count == 0) _recentByIp.Remove(key);
            }
        }
    }
}
