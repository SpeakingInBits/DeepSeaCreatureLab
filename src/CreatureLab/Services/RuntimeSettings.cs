using System.Text.Json;

namespace CreatureLab.Services;

public sealed record LabSettings(string PublicBaseUrl, bool AutoApprove, int SnapshotIntervalMinutes);

/// <summary>A partial update from the admin page; null means "leave as is".</summary>
public sealed record SettingsUpdate(string? PublicBaseUrl, bool? AutoApprove, int? SnapshotIntervalMinutes);

/// <summary>
/// Settings that staff change during the event. The public URL and snapshot interval
/// are saved to data/settings.json so a server restart keeps them. Auto-approve is
/// deliberately not saved: after a restart it always comes back to the configured
/// default (off), so nothing reaches the TV unreviewed by accident.
/// </summary>
public sealed class RuntimeSettings
{
    public const int MaxSnapshotIntervalMinutes = 240;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] PagePaths = ["/draw", "/tank", "/admin"];

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly ILogger<RuntimeSettings> _logger;
    private LabSettings _current;

    public RuntimeSettings(string dataDirectory, LabSettings defaults, ILogger<RuntimeSettings> logger)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "settings.json");
        _logger = logger;
        _current = defaults with
        {
            PublicBaseUrl = NormalizeUrl(defaults.PublicBaseUrl, out _) ?? "",
            SnapshotIntervalMinutes = Math.Clamp(defaults.SnapshotIntervalMinutes, 0, MaxSnapshotIntervalMinutes),
        };
        LoadSaved();
    }

    public LabSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public bool TryUpdate(SettingsUpdate update, out LabSettings result, out string? error)
    {
        lock (_gate)
        {
            var next = _current;

            if (update.PublicBaseUrl is not null)
            {
                var url = NormalizeUrl(update.PublicBaseUrl, out error);
                if (url is null)
                {
                    result = _current;
                    return false;
                }
                next = next with { PublicBaseUrl = url };
            }

            if (update.SnapshotIntervalMinutes is { } minutes)
            {
                if (minutes is < 0 or > MaxSnapshotIntervalMinutes)
                {
                    error = $"Snapshot interval must be between 0 (off) and {MaxSnapshotIntervalMinutes} minutes.";
                    result = _current;
                    return false;
                }
                next = next with { SnapshotIntervalMinutes = minutes };
            }

            if (update.AutoApprove is { } autoApprove)
            {
                next = next with { AutoApprove = autoApprove };
            }

            _current = next;
            Persist();
            result = next;
            error = null;
            return true;
        }
    }

    /// <summary>
    /// Accepts what staff are likely to paste ("https://x.trycloudflare.com/draw/",
    /// with spaces, etc.) and returns just the base URL. Empty clears it.
    /// Returns null (with an error) when it isn't an http(s) URL.
    /// </summary>
    public static string? NormalizeUrl(string raw, out string? error)
    {
        error = null;
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return "";

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Enter the full URL, starting with https://";
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var page in PagePaths)
        {
            if (path.EndsWith(page, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^page.Length];
                break;
            }
        }

        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    private void LoadSaved()
    {
        if (!File.Exists(_path)) return;

        try
        {
            var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(_path), JsonOptions);
            if (saved is null) return;

            if (saved.PublicBaseUrl is not null && NormalizeUrl(saved.PublicBaseUrl, out _) is { } url)
            {
                _current = _current with { PublicBaseUrl = url };
            }
            if (saved.SnapshotIntervalMinutes is { } minutes)
            {
                _current = _current with { SnapshotIntervalMinutes = Math.Clamp(minutes, 0, MaxSnapshotIntervalMinutes) };
            }

            _logger.LogInformation("Using saved settings from {Path} (public URL: {Url}).", _path,
                _current.PublicBaseUrl.Length > 0 ? _current.PublicBaseUrl : "not set");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring unreadable {Path}.", _path);
        }
    }

    private void Persist()
    {
        try
        {
            var saved = new SavedSettings(_current.PublicBaseUrl, _current.SnapshotIntervalMinutes);
            File.WriteAllText(_path, JsonSerializer.Serialize(saved, JsonOptions));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not save settings to {Path}; they will reset on restart.", _path);
        }
    }

    private sealed record SavedSettings(string? PublicBaseUrl, int? SnapshotIntervalMinutes);
}
