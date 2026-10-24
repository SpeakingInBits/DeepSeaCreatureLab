using CreatureLab.Models;
using CreatureLab.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatureLab.Tests;

public sealed class CreatureStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.Zero));
    private readonly byte[] _png = TestPng.Create();

    public void Dispose() => _dir.Dispose();

    private CreatureStore NewStore(int cap = 30, int maxPending = 100) =>
        new(_dir.Path, cap, maxPending, _time, NullLogger<CreatureStore>.Instance);

    private Creature Submit(CreatureStore store, string name = "Gloop", bool autoApprove = false)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        var result = store.Submit(new NewCreature(name, "Sam", Habitat.Swimmer, "client-1234"), _png, autoApprove);
        Assert.Equal(StoreOutcome.Ok, result.Outcome);
        return result.Creature!;
    }

    [Fact]
    public void Submit_CreatesPendingCreature_AndSavesTheImage()
    {
        var store = NewStore();

        var creature = Submit(store);

        Assert.Equal(CreatureStatus.Pending, creature.Status);
        Assert.Null(creature.DecidedAt);
        Assert.Equal(_png, File.ReadAllBytes(store.ImagePath(creature)));
        Assert.Single(store.Pending());
        Assert.Empty(store.InTank());
    }

    [Fact]
    public void Approve_MovesCreatureFromQueueToTank()
    {
        var store = NewStore();
        var creature = Submit(store);

        var result = store.Approve(creature.Id);

        Assert.Equal(StoreOutcome.Ok, result.Outcome);
        Assert.Equal(CreatureStatus.Approved, result.Creature!.Status);
        Assert.Equal(_time.Now, result.Creature.DecidedAt);
        Assert.Empty(store.Pending());
        Assert.Equal(creature.Id, Assert.Single(store.InTank()).Id);
    }

    [Fact]
    public void Reject_OnlyWorksOnPendingCreatures()
    {
        var store = NewStore();
        var creature = Submit(store);
        store.Approve(creature.Id);

        var result = store.Reject(creature.Id);

        Assert.Equal(StoreOutcome.InvalidState, result.Outcome);
        Assert.Equal(CreatureStatus.Approved, store.Get(creature.Id)!.Status);
    }

    [Fact]
    public void Reject_KeepsCreatureOutOfTankAndGallery()
    {
        var store = NewStore();
        var creature = Submit(store);

        Assert.Equal(StoreOutcome.Ok, store.Reject(creature.Id).Outcome);

        Assert.Equal(CreatureStatus.Rejected, store.Get(creature.Id)!.Status);
        Assert.Empty(store.Pending());
        Assert.Empty(store.InTank());
        Assert.Empty(store.Gallery());
    }

    [Fact]
    public void Remove_OnlyWorksOnCreaturesInTheTank()
    {
        var store = NewStore();
        var creature = Submit(store);

        Assert.Equal(StoreOutcome.InvalidState, store.Remove(creature.Id).Outcome);

        store.Approve(creature.Id);
        Assert.Equal(StoreOutcome.Ok, store.Remove(creature.Id).Outcome);
        Assert.Empty(store.InTank());
        Assert.Empty(store.Gallery());
    }

    [Fact]
    public void UnknownId_IsNotFound()
    {
        var store = NewStore();

        Assert.Equal(StoreOutcome.NotFound, store.Approve(Guid.NewGuid()).Outcome);
        Assert.Equal(StoreOutcome.NotFound, store.Reject(Guid.NewGuid()).Outcome);
        Assert.Equal(StoreOutcome.NotFound, store.Remove(Guid.NewGuid()).Outcome);
    }

    [Fact]
    public void PopulationCap_RetiresTheOldestArrival()
    {
        var store = NewStore(cap: 3);
        var creatures = Enumerable.Range(1, 4).Select(i => Submit(store, $"C{i}")).ToList();

        StoreResult last = null!;
        foreach (var c in creatures)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            last = store.Approve(c.Id);
        }

        var retired = Assert.Single(last.Retired);
        Assert.Equal(creatures[0].Id, retired.Id);
        Assert.Equal(CreatureStatus.Retired, store.Get(creatures[0].Id)!.Status);
        Assert.Equal(creatures.Skip(1).Select(c => c.Id), store.InTank().Select(c => c.Id));
        Assert.Equal(4, store.Gallery().Count); // retired creatures stay in the gallery
    }

    [Fact]
    public void AutoApprove_SkipsTheQueue()
    {
        var store = NewStore();

        var creature = Submit(store, autoApprove: true);

        Assert.Equal(CreatureStatus.Approved, creature.Status);
        Assert.Empty(store.Pending());
        Assert.Single(store.InTank());
    }

    [Fact]
    public void Submit_RefusesWhenPendingQueueIsFull()
    {
        var store = NewStore(maxPending: 2);
        Submit(store, "A");
        Submit(store, "B");

        var result = store.Submit(new NewCreature("C", null, Habitat.Floater, "client-1234"), _png, autoApprove: false);

        Assert.Equal(StoreOutcome.PendingQueueFull, result.Outcome);
        Assert.Equal(2, store.Pending().Count);
    }

    [Fact]
    public void Persistence_RoundTripsEverything()
    {
        var store = NewStore();
        var pending = Submit(store, "Pending one");
        var approved = Submit(store, "Approved one");
        var rejected = Submit(store, "Rejected one");
        store.Approve(approved.Id);
        store.Reject(rejected.Id);

        var reloaded = NewStore();

        Assert.Equal(CreatureStatus.Pending, reloaded.Get(pending.Id)!.Status);
        Assert.Equal(CreatureStatus.Rejected, reloaded.Get(rejected.Id)!.Status);
        var inTank = Assert.Single(reloaded.InTank());
        Assert.Equal(approved.Id, inTank.Id);
        Assert.Equal("Approved one", inTank.CreatureName);
        Assert.Equal("Sam", inTank.ArtistName);
        Assert.Equal(Habitat.Swimmer, inTank.Habitat);
        Assert.Equal("client-1234", inTank.ClientId);
        Assert.True(File.Exists(reloaded.ImagePath(inTank)));
    }

    [Fact]
    public void Load_RetiresOverflowWhenTheCapWasLowered()
    {
        var store = NewStore(cap: 5);
        foreach (var i in Enumerable.Range(1, 5))
        {
            var c = Submit(store, $"C{i}");
            store.Approve(c.Id);
        }

        var reloaded = NewStore(cap: 2);

        Assert.Equal(new[] { "C4", "C5" }, reloaded.InTank().Select(c => c.CreatureName));
        Assert.Equal(5, reloaded.Gallery().Count);
    }

    [Fact]
    public void Load_SetsAsideACorruptFileAndStartsEmpty()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "creatures.json"), "{ not json");

        var store = NewStore();

        Assert.Empty(store.InTank());
        Assert.Single(Directory.GetFiles(_dir.Path, "creatures.json.corrupt-*"));
    }

    [Fact]
    public void Counts_OnlyCountTodaysSubmissionsAsToday()
    {
        var store = NewStore();
        Submit(store, "Yesterday");
        _time.Advance(TimeSpan.FromDays(1));
        var today = Submit(store, "Today");
        store.Approve(today.Id);

        var counts = store.Counts();

        Assert.Equal(2, counts.SubmissionsTotal);
        Assert.Equal(1, counts.SubmissionsToday);
        Assert.Equal(1, counts.ApprovedTotal);
        Assert.Equal(1, counts.InTank);
        Assert.Equal(1, counts.Pending);
    }
}
