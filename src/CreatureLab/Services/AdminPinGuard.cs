using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CreatureLab.Services;

/// <summary>
/// Checks the moderator PIN and slows down guessing: after 5 wrong PINs in 10 minutes
/// from one IP, that IP is locked out for the rest of the window. The booth laptop
/// itself (loopback) is never locked out, so staff can always moderate locally.
/// </summary>
public sealed class AdminPinGuard
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly byte[] _pin;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _failures = [];

    public AdminPinGuard(string pin, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);
        _pin = Encoding.UTF8.GetBytes(pin.Trim());
        _time = time;
    }

    /// <summary>
    /// Uses the configured PIN. If there isn't one, makes up a 6-digit PIN, saves it to
    /// data/admin-pin.txt so it survives restarts (moderator phones stay logged in), and
    /// prints it to the server console, which only the booth laptop can see.
    /// </summary>
    public static AdminPinGuard Create(string? configuredPin, string dataDirectory, TimeProvider time, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(configuredPin)) return new AdminPinGuard(configuredPin, time);

        var path = Path.Combine(dataDirectory, "admin-pin.txt");
        var pin = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        if (pin.Length == 0)
        {
            pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(path, pin);
        }

        logger.LogWarning(
            "No Admin:Pin configured, so using the PIN saved in {Path}: {Pin}. Set Admin:Pin in appsettings.Local.json to choose your own.",
            path, pin);
        return new AdminPinGuard(pin, time);
    }

    public bool IsLockedOut(IPAddress? ip)
    {
        if (ip is null || IPAddress.IsLoopback(ip)) return false;

        lock (_gate)
        {
            return RecentFailures(Key(ip)) >= MaxFailures;
        }
    }

    public bool Verify(string? candidate, IPAddress? ip)
    {
        if (IsLockedOut(ip)) return false;

        var ok = !string.IsNullOrEmpty(candidate) &&
                 CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate.Trim()), _pin);

        if (!ok && ip is not null && !IPAddress.IsLoopback(ip))
        {
            lock (_gate)
            {
                var key = Key(ip);
                if (!_failures.TryGetValue(key, out var list))
                {
                    list = [];
                    _failures[key] = list;
                }
                list.Add(_time.GetUtcNow());
            }
        }

        return ok;
    }

    private int RecentFailures(string key)
    {
        if (!_failures.TryGetValue(key, out var list)) return 0;

        var cutoff = _time.GetUtcNow() - Window;
        list.RemoveAll(at => at < cutoff);
        if (list.Count == 0) _failures.Remove(key);
        return list.Count;
    }

    private static string Key(IPAddress ip) => (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
}
