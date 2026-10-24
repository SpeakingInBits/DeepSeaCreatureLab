using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CreatureLab.Models;
using CreatureLab.Options;
using Microsoft.Extensions.Options;

namespace CreatureLab.Services;

public sealed record GalleryEntry(
    string File,
    string CreatureName,
    string? ArtistName,
    string ArtistDisplay,
    Habitat Habitat,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt);

/// <summary>
/// Builds the post-event zip: creatures/*.png, snapshots/*.png, creatures.json
/// (no client IDs) and a self-contained index.html gallery with a time-lapse.
/// </summary>
public sealed class GalleryExporter(CreatureStore store, SnapshotStore snapshots, IOptions<LabOptions> options, TimeProvider time)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Writes the zip to a temp file that deletes itself when the returned stream is closed,
    /// so a big export (lots of snapshots) never has to fit in memory.
    /// </summary>
    public Stream CreateZip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"creature-lab-{Guid.NewGuid():N}.zip");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);

        try
        {
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entries = new List<GalleryEntry>();
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var creature in store.Gallery())
                {
                    var source = store.ImagePath(creature);
                    if (!File.Exists(source)) continue;

                    var name = Unique(FileNameFor(creature), usedNames);
                    zip.CreateEntryFromFile(source, $"creatures/{name}", CompressionLevel.NoCompression);
                    entries.Add(new GalleryEntry(
                        $"creatures/{name}", creature.CreatureName, creature.ArtistName, creature.ArtistDisplay,
                        creature.Habitat, creature.CreatedAt, creature.DecidedAt));
                }

                var snapshotFiles = new List<string>();
                foreach (var snapshot in snapshots.List())
                {
                    var name = $"snapshots/{Path.GetFileName(snapshot)}";
                    zip.CreateEntryFromFile(snapshot, name, CompressionLevel.NoCompression);
                    snapshotFiles.Add(name);
                }

                WriteText(zip, "creatures.json", JsonSerializer.Serialize(entries, JsonOptions));
                WriteText(zip, "index.html", GalleryPage.Render(entries, snapshotFiles, options.Value.Programs));
            }

            file.Position = 0;
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public string DownloadName() =>
        $"creature-lab-gallery-{TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone):yyyyMMdd-HHmm}.zip";

    /// <summary>{createdAt}-{creatureName}-by-{artistName}.png, made filesystem-safe.</summary>
    public string FileNameFor(Creature creature)
    {
        var created = TimeZoneInfo.ConvertTime(creature.CreatedAt, time.LocalTimeZone);
        return $"{created:yyyyMMdd-HHmmss}-{Slug(creature.CreatureName)}-by-{Slug(creature.ArtistDisplay)}.png";
    }

    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return slug.Length > 0 ? slug : "creature";
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(name)}-{n}.png";
        }
        return candidate;
    }

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
