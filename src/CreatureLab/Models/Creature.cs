using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CreatureLab.Models;

public enum Habitat { Swimmer, BottomCrawler, Floater }

/// <summary>
/// Pending → Approved or Rejected. Approved → Removed (by a moderator) or Retired
/// (pushed out of the tank by the population cap; still part of the gallery).
/// </summary>
public enum CreatureStatus { Pending, Approved, Rejected, Removed, Retired }

public sealed record Creature(
    Guid Id,
    string CreatureName,    // required, trimmed, max 20 chars
    string? ArtistName,     // optional, trimmed, max 20 chars; null shows as "a mystery diver"
    Habitat Habitat,
    string ImageFile,       // relative to the data folder, e.g. "images/{Id}.png"
    CreatureStatus Status,
    string ClientId,        // random ID stored in the visitor's localStorage; not personal data
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt)
{
    public const string MysteryDiver = "a mystery diver";

    [JsonIgnore]
    public string ArtistDisplay => ArtistName ?? MysteryDiver;
}

/// <summary>What browsers see. Never includes the ClientId.</summary>
public sealed record CreatureSummary(
    Guid Id,
    string CreatureName,
    string? ArtistName,
    string ArtistDisplay,
    Habitat Habitat,
    CreatureStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    string ImageUrl)
{
    public static CreatureSummary From(Creature c) => new(
        c.Id, c.CreatureName, c.ArtistName, c.ArtistDisplay, c.Habitat, c.Status,
        c.CreatedAt, c.DecidedAt, $"/api/creatures/{c.Id}/image");
}

/// <summary>Payload for events that only need to say which creature.</summary>
public sealed record CreatureRef(Guid Id);

public static partial class ClientIds
{
    /// <summary>Client IDs are random GUIDs generated in the browser; accept anything GUID-shaped.</summary>
    public static bool IsValid(string? clientId) => clientId is not null && Pattern().IsMatch(clientId);

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex Pattern();
}
