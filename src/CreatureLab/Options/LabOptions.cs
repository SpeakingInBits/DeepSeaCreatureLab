namespace CreatureLab.Options;

/// <summary>
/// Strongly typed view of appsettings.json (bound from the configuration root).
/// Values staff can change during the event (public URL, auto-approve, snapshot
/// interval) start here and then live in <see cref="Services.RuntimeSettings"/>.
/// </summary>
public sealed class LabOptions
{
    /// <summary>Public tunnel URL, used for the QR code on the tank. Editable on the admin page.</summary>
    public string PublicBaseUrl { get; set; } = "";

    public AdminOptions Admin { get; set; } = new();
    public TankOptions Tank { get; set; } = new();
    public ModerationOptions Moderation { get; set; } = new();
    public RateLimitOptions RateLimit { get; set; } = new();
    public UploadOptions Upload { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public SnapshotOptions Snapshots { get; set; } = new();
    public DisplayOptions Display { get; set; } = new();
    public List<ProgramLink> Programs { get; set; } = [];
}

public sealed class AdminOptions
{
    /// <summary>Moderator PIN. Leave empty to have the server generate one at startup.</summary>
    public string Pin { get; set; } = "";
}

public sealed class TankOptions
{
    public int PopulationCap { get; set; } = 30;
}

public sealed class ModerationOptions
{
    public bool AutoApprove { get; set; }
    public int MaxPending { get; set; } = 100;
    public string ProfanityListPath { get; set; } = "profanity.txt";
}

public sealed class RateLimitOptions
{
    /// <summary>One submission per device (clientId) every this many seconds.</summary>
    public int SecondsPerDevice { get; set; } = 30;

    /// <summary>
    /// Submissions allowed per IP address per minute. Looser than the per-device limit
    /// because many phones on campus Wi-Fi share one public IP. 0 disables it.
    /// </summary>
    public int MaxPerIpPerMinute { get; set; } = 10;
}

public sealed class UploadOptions
{
    public int MaxBytes { get; set; } = 256 * 1024;
    public int MaxDimension { get; set; } = 512;
    public int MaxNameLength { get; set; } = 20;
}

public sealed class StorageOptions
{
    /// <summary>Folder for images and metadata, relative to the project folder (content root).</summary>
    public string DataPath { get; set; } = "../../data";
}

public sealed class SnapshotOptions
{
    public int IntervalMinutes { get; set; } = 5;
    public bool IncludeOverlay { get; set; } = true;
    public int MaxBytes { get; set; } = 25 * 1024 * 1024;
}

public sealed class DisplayOptions
{
    public string CreditLine { get; set; } = "Built by Computer Programming with C#, ASP.NET Core, and SignalR.";
}

public sealed class ProgramLink
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}
