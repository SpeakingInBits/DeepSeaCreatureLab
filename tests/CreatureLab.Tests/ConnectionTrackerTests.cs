using CreatureLab.Services;

namespace CreatureLab.Tests;

public sealed class ConnectionTrackerTests
{
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public void ConnectedPhone_CanRelease()
    {
        var tracker = new ConnectionTracker(_time);

        tracker.AddVisitor("conn-1", "phone-a");

        Assert.True(tracker.ConnectedRecently("phone-a"));
        Assert.False(tracker.ConnectedRecently("phone-b"));
    }

    [Fact]
    public void BriefDrop_StillCounts_UntilTheWindowPasses()
    {
        var tracker = new ConnectionTracker(_time);
        tracker.AddVisitor("conn-1", "phone-a");
        tracker.Remove("conn-1");

        _time.Advance(ConnectionTracker.RecentWindow);
        Assert.True(tracker.ConnectedRecently("phone-a"));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.ConnectedRecently("phone-a"));
    }

    [Fact]
    public void Reconnecting_UsesTheNewConnection()
    {
        var tracker = new ConnectionTracker(_time);
        tracker.AddVisitor("conn-1", "phone-a");
        tracker.Remove("conn-1");
        _time.Advance(TimeSpan.FromMinutes(10));

        tracker.AddVisitor("conn-2", "phone-a");

        Assert.True(tracker.ConnectedRecently("phone-a"));
    }

    [Fact]
    public void TwoTabs_CountAsOnePhone_AndClosingOneKeepsItConnected()
    {
        var tracker = new ConnectionTracker(_time);
        tracker.AddVisitor("conn-1", "phone-a");
        tracker.AddVisitor("conn-2", "phone-a");
        tracker.AddVisitor("conn-3", "phone-b");

        Assert.Equal(2, tracker.ConnectedPhones);

        tracker.Remove("conn-1");
        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(tracker.ConnectedRecently("phone-a"));
        Assert.Equal(2, tracker.ConnectedPhones);
    }

    [Fact]
    public void RemovingAnUnknownConnection_IsHarmless()
    {
        var tracker = new ConnectionTracker(_time);

        tracker.Remove("never-seen");

        Assert.Equal(0, tracker.ConnectedPhones);
    }
}
