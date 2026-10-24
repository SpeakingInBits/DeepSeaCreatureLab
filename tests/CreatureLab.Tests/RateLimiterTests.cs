using CreatureLab.Services;

namespace CreatureLab.Tests;

public sealed class RateLimiterTests
{
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    private SubmissionRateLimiter NewLimiter(int perIpPerMinute = 10) =>
        new(TimeSpan.FromSeconds(30), perIpPerMinute, _time);

    [Fact]
    public void SameDevice_MustWait30Seconds()
    {
        var limiter = NewLimiter();
        Assert.True(limiter.TryAcquire("phone-a", "1.1.1.1").Allowed);

        _time.Advance(TimeSpan.FromSeconds(12));
        var blocked = limiter.TryAcquire("phone-a", "1.1.1.1");

        Assert.False(blocked.Allowed);
        Assert.Equal(RateLimitReason.Device, blocked.Reason);
        Assert.Equal(TimeSpan.FromSeconds(18), blocked.RetryAfter);

        _time.Advance(TimeSpan.FromSeconds(18));
        Assert.True(limiter.TryAcquire("phone-a", "1.1.1.1").Allowed);
    }

    [Fact]
    public void DifferentDevices_DoNotBlockEachOther()
    {
        var limiter = NewLimiter();

        Assert.True(limiter.TryAcquire("phone-a", "1.1.1.1").Allowed);
        Assert.True(limiter.TryAcquire("phone-b", "1.1.1.1").Allowed);
        Assert.True(limiter.TryAcquire("phone-c", "2.2.2.2").Allowed);
    }

    [Fact]
    public void SharedNetwork_IsCappedPerMinute()
    {
        var limiter = NewLimiter(perIpPerMinute: 3);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire($"phone-{i}", "10.0.0.1").Allowed);
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        var blocked = limiter.TryAcquire("phone-new", "10.0.0.1");

        Assert.False(blocked.Allowed);
        Assert.Equal(RateLimitReason.Network, blocked.Reason);
        Assert.Equal(TimeSpan.FromSeconds(45), blocked.RetryAfter);
        Assert.True(limiter.TryAcquire("phone-elsewhere", "10.0.0.2").Allowed);

        _time.Advance(TimeSpan.FromSeconds(45));
        Assert.True(limiter.TryAcquire("phone-new", "10.0.0.1").Allowed);
    }

    [Fact]
    public void BlockedAttempts_DoNotUseUpTheBudget()
    {
        var limiter = NewLimiter(perIpPerMinute: 2);
        Assert.True(limiter.TryAcquire("phone-a", "10.0.0.1").Allowed);
        Assert.False(limiter.TryAcquire("phone-a", "10.0.0.1").Allowed); // device limit

        Assert.True(limiter.TryAcquire("phone-b", "10.0.0.1").Allowed);
    }

    [Fact]
    public void ZeroDisablesThePerIpLimit()
    {
        var limiter = NewLimiter(perIpPerMinute: 0);

        for (var i = 0; i < 50; i++) Assert.True(limiter.TryAcquire($"phone-{i}", "10.0.0.1").Allowed);
    }
}
