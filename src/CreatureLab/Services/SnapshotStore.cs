namespace CreatureLab.Services;

/// <summary>Timed screenshots of the tank, saved as data/snapshots/{yyyyMMdd-HHmmss}.png (local time).</summary>
public sealed class SnapshotStore(string dataDirectory, TimeProvider time)
{
    public string SnapshotsDirectory { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "snapshots");

    public string Save(byte[] png)
    {
        Directory.CreateDirectory(SnapshotsDirectory);

        var stamp = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).ToString("yyyyMMdd-HHmmss");
        var name = $"{stamp}.png";
        for (var n = 2; File.Exists(Path.Combine(SnapshotsDirectory, name)); n++) name = $"{stamp}-{n}.png";

        File.WriteAllBytes(Path.Combine(SnapshotsDirectory, name), png);
        return name;
    }

    /// <summary>Full paths, oldest first (the names sort by time).</summary>
    public IReadOnlyList<string> List() => Directory.Exists(SnapshotsDirectory)
        ? Directory.GetFiles(SnapshotsDirectory, "*.png").Order(StringComparer.Ordinal).ToList()
        : [];
}
