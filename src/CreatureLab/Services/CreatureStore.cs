using System.Text.Json;
using System.Text.Json.Serialization;
using CreatureLab.Models;

namespace CreatureLab.Services;

public sealed record NewCreature(string CreatureName, string? ArtistName, Habitat Habitat, string ClientId);

public enum StoreOutcome { Ok, NotFound, InvalidState, PendingQueueFull }

/// <summary>Result of a store operation. <see cref="Retired"/> lists creatures pushed out by the population cap.</summary>
public sealed record StoreResult(StoreOutcome Outcome, Creature? Creature, IReadOnlyList<Creature> Retired)
{
    public static StoreResult Fail(StoreOutcome outcome, Creature? creature = null) => new(outcome, creature, []);
}

public sealed record StoreCounts(int SubmissionsTotal, int SubmissionsToday, int ApprovedTotal, int InTank, int Pending);

/// <summary>
/// All creatures, in memory, guarded by one lock. Every change rewrites
/// data/creatures.json (fine at ~150 drawings) and images live in data/images.
/// </summary>
public sealed class CreatureStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _gate = new();
    private readonly List<Creature> _creatures = [];
    private readonly string _metadataPath;
    private readonly TimeProvider _time;
    private readonly ILogger<CreatureStore> _logger;

    public CreatureStore(string dataDirectory, int populationCap, int maxPending, TimeProvider time, ILogger<CreatureStore> logger)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        ImagesDirectory = Path.Combine(DataDirectory, "images");
        PopulationCap = Math.Max(1, populationCap);
        MaxPending = Math.Max(1, maxPending);
        _metadataPath = Path.Combine(DataDirectory, "creatures.json");
        _time = time;
        _logger = logger;

        Directory.CreateDirectory(ImagesDirectory);
        Load();
    }

    public string DataDirectory { get; }
    public string ImagesDirectory { get; }
    public int PopulationCap { get; }
    public int MaxPending { get; }

    public int PendingCount
    {
        get { lock (_gate) return _creatures.Count(c => c.Status == CreatureStatus.Pending); }
    }

    public StoreResult Submit(NewCreature input, byte[] png, bool autoApprove)
    {
        lock (_gate)
        {
            if (!autoApprove && _creatures.Count(c => c.Status == CreatureStatus.Pending) >= MaxPending)
            {
                return StoreResult.Fail(StoreOutcome.PendingQueueFull);
            }

            var id = Guid.NewGuid();
            var imageFile = $"images/{id}.png";
            File.WriteAllBytes(Path.Combine(DataDirectory, imageFile), png);

            var creature = new Creature(
                id, input.CreatureName, input.ArtistName, input.Habitat, imageFile,
                CreatureStatus.Pending, input.ClientId, _time.GetUtcNow(), DecidedAt: null);
            _creatures.Add(creature);

            if (autoApprove)
            {
                return ApproveLocked(creature);
            }

            Save();
            return new StoreResult(StoreOutcome.Ok, creature, []);
        }
    }

    public StoreResult Approve(Guid id)
    {
        lock (_gate)
        {
            var creature = Find(id);
            if (creature is null) return StoreResult.Fail(StoreOutcome.NotFound);
            if (creature.Status != CreatureStatus.Pending) return StoreResult.Fail(StoreOutcome.InvalidState, creature);
            return ApproveLocked(creature);
        }
    }

    public StoreResult Reject(Guid id) =>
        Transition(id, from: CreatureStatus.Pending, c => c with { Status = CreatureStatus.Rejected, DecidedAt = _time.GetUtcNow() });

    public StoreResult Remove(Guid id) =>
        Transition(id, from: CreatureStatus.Approved, c => c with { Status = CreatureStatus.Removed });

    public Creature? Get(Guid id)
    {
        lock (_gate) return Find(id);
    }

    /// <summary>Pending queue, oldest first.</summary>
    public IReadOnlyList<Creature> Pending()
    {
        lock (_gate) return _creatures.Where(c => c.Status == CreatureStatus.Pending).OrderBy(c => c.CreatedAt).ToList();
    }

    /// <summary>Creatures currently in the tank, in the order they were approved.</summary>
    public IReadOnlyList<Creature> InTank()
    {
        lock (_gate) return TankLocked();
    }

    /// <summary>Everything that was ever on screen and not removed by a moderator (approved + retired).</summary>
    public IReadOnlyList<Creature> Gallery()
    {
        lock (_gate)
        {
            return _creatures
                .Where(c => c.Status is CreatureStatus.Approved or CreatureStatus.Retired)
                .OrderBy(c => c.CreatedAt)
                .ToList();
        }
    }

    public StoreCounts Counts()
    {
        lock (_gate)
        {
            var today = LocalDate(_time.GetUtcNow());
            return new StoreCounts(
                SubmissionsTotal: _creatures.Count,
                SubmissionsToday: _creatures.Count(c => LocalDate(c.CreatedAt) == today),
                ApprovedTotal: _creatures.Count(c => c.Status is CreatureStatus.Approved or CreatureStatus.Retired or CreatureStatus.Removed),
                InTank: _creatures.Count(c => c.Status == CreatureStatus.Approved),
                Pending: _creatures.Count(c => c.Status == CreatureStatus.Pending));
        }
    }

    public string ImagePath(Creature creature) => Path.Combine(DataDirectory, creature.ImageFile);

    private StoreResult Transition(Guid id, CreatureStatus from, Func<Creature, Creature> change)
    {
        lock (_gate)
        {
            var creature = Find(id);
            if (creature is null) return StoreResult.Fail(StoreOutcome.NotFound);
            if (creature.Status != from) return StoreResult.Fail(StoreOutcome.InvalidState, creature);

            var updated = change(creature);
            Replace(updated);
            Save();
            return new StoreResult(StoreOutcome.Ok, updated, []);
        }
    }

    private StoreResult ApproveLocked(Creature creature)
    {
        var approved = creature with { Status = CreatureStatus.Approved, DecidedAt = _time.GetUtcNow() };
        Replace(approved);
        var retired = RetireOverflowLocked();
        Save();
        return new StoreResult(StoreOutcome.Ok, approved, retired);
    }

    /// <summary>When the tank is over the cap, the oldest arrivals swim off and become Retired.</summary>
    private List<Creature> RetireOverflowLocked()
    {
        var tank = TankLocked();
        var retired = new List<Creature>();
        for (var i = 0; i < tank.Count - PopulationCap; i++)
        {
            var r = tank[i] with { Status = CreatureStatus.Retired };
            Replace(r);
            retired.Add(r);
        }
        return retired;
    }

    private List<Creature> TankLocked() => _creatures
        .Where(c => c.Status == CreatureStatus.Approved)
        .OrderBy(c => c.DecidedAt)
        .ThenBy(c => c.CreatedAt)
        .ToList();

    private Creature? Find(Guid id) => _creatures.Find(c => c.Id == id);

    private void Replace(Creature updated)
    {
        var index = _creatures.FindIndex(c => c.Id == updated.Id);
        _creatures[index] = updated;
    }

    private DateOnly LocalDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, _time.LocalTimeZone).DateTime);

    private void Load()
    {
        if (!File.Exists(_metadataPath)) return;

        try
        {
            var saved = JsonSerializer.Deserialize<List<Creature>>(File.ReadAllText(_metadataPath), JsonOptions) ?? [];
            _creatures.AddRange(saved.OrderBy(c => c.CreatedAt));
        }
        catch (JsonException ex)
        {
            var backup = $"{_metadataPath}.corrupt-{_time.GetUtcNow():yyyyMMddHHmmss}";
            File.Move(_metadataPath, backup);
            _logger.LogError(ex, "creatures.json could not be read. Moved it to {Backup} and started with an empty tank.", backup);
            return;
        }

        // The cap may have been lowered since the last run.
        if (RetireOverflowLocked().Count > 0) Save();

        var counts = Counts();
        _logger.LogInformation(
            "Loaded {Total} creatures from {Path}: {InTank} in the tank, {Pending} pending.",
            counts.SubmissionsTotal, _metadataPath, counts.InTank, counts.Pending);
    }

    private void Save()
    {
        // Write to a temp file and swap it in, so a crash mid-write never leaves half a file.
        var temp = _metadataPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_creatures, JsonOptions));
        File.Move(temp, _metadataPath, overwrite: true);
    }
}
