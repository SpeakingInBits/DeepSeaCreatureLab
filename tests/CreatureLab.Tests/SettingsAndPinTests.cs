using System.Net;
using System.Text.RegularExpressions;
using CreatureLab.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatureLab.Tests;

public sealed class RuntimeSettingsTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private RuntimeSettings NewSettings(string url = "", bool autoApprove = false, int interval = 5) =>
        new(_dir.Path, new LabSettings(url, autoApprove, interval), NullLogger<RuntimeSettings>.Instance);

    [Theory]
    [InlineData("https://abc-5000.usw2.devtunnels.ms", "https://abc-5000.usw2.devtunnels.ms")]
    [InlineData("  https://abc-5000.usw2.devtunnels.ms/  ", "https://abc-5000.usw2.devtunnels.ms")]
    [InlineData("https://abc-5000.usw2.devtunnels.ms/draw", "https://abc-5000.usw2.devtunnels.ms")]
    [InlineData("https://abc-5000.usw2.devtunnels.ms/draw/", "https://abc-5000.usw2.devtunnels.ms")]
    [InlineData("https://abc-5000.usw2.devtunnels.ms/tank", "https://abc-5000.usw2.devtunnels.ms")]
    [InlineData("https://example.edu/creatures/draw", "https://example.edu/creatures")]
    [InlineData("", "")]
    public void NormalizeUrl_KeepsJustTheBase(string raw, string expected)
    {
        Assert.Equal(expected, RuntimeSettings.NormalizeUrl(raw, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("abc-5000.usw2.devtunnels.ms")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert(1)")]
    public void NormalizeUrl_RejectsNonWebUrls(string raw)
    {
        Assert.Null(RuntimeSettings.NormalizeUrl(raw, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Update_SavesUrlAndInterval_ButAutoApproveResetsOnRestart()
    {
        var settings = NewSettings();
        Assert.True(settings.TryUpdate(new SettingsUpdate("https://abc-5000.usw2.devtunnels.ms", true, 10), out _, out _));

        var restarted = NewSettings();

        Assert.Equal("https://abc-5000.usw2.devtunnels.ms", restarted.Current.PublicBaseUrl);
        Assert.Equal(10, restarted.Current.SnapshotIntervalMinutes);
        Assert.False(restarted.Current.AutoApprove);
    }

    [Fact]
    public void Update_LeavesNullFieldsAlone()
    {
        var settings = NewSettings(url: "https://first.example", autoApprove: true, interval: 7);

        settings.TryUpdate(new SettingsUpdate(null, null, 3), out var result, out _);

        Assert.Equal(new LabSettings("https://first.example", true, 3), result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(241)]
    public void Update_RejectsOutOfRangeInterval(int minutes)
    {
        var settings = NewSettings(interval: 5);

        Assert.False(settings.TryUpdate(new SettingsUpdate(null, null, minutes), out var result, out var error));
        Assert.Equal(5, result.SnapshotIntervalMinutes);
        Assert.NotNull(error);
    }
}

public sealed class AdminPinGuardTests
{
    private static readonly IPAddress Visitor = IPAddress.Parse("203.0.113.7");
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public void Verify_AcceptsOnlyTheRightPin()
    {
        var guard = new AdminPinGuard("4815", _time);

        Assert.True(guard.Verify("4815", Visitor));
        Assert.True(guard.Verify(" 4815 ", Visitor));
        Assert.False(guard.Verify("4816", Visitor));
        Assert.False(guard.Verify("", Visitor));
        Assert.False(guard.Verify(null, Visitor));
    }

    [Fact]
    public void TooManyWrongPins_LockOutThatIpForTheWindow()
    {
        var guard = new AdminPinGuard("4815", _time);
        for (var i = 0; i < AdminPinGuard.MaxFailures; i++) guard.Verify("0000", Visitor);

        Assert.True(guard.IsLockedOut(Visitor));
        Assert.False(guard.Verify("4815", Visitor)); // even the right PIN
        Assert.True(guard.Verify("4815", IPAddress.Parse("198.51.100.1")));

        _time.Advance(AdminPinGuard.Window + TimeSpan.FromSeconds(1));
        Assert.True(guard.Verify("4815", Visitor));
    }

    [Fact]
    public void TheBoothLaptopIsNeverLockedOut()
    {
        var guard = new AdminPinGuard("4815", _time);
        for (var i = 0; i < 20; i++) guard.Verify("0000", IPAddress.Loopback);

        Assert.False(guard.IsLockedOut(IPAddress.Loopback));
        Assert.True(guard.Verify("4815", IPAddress.IPv6Loopback));
    }

    [Fact]
    public void Create_GeneratesAPin_PrintsIt_AndKeepsItAcrossRestarts()
    {
        using var dir = new TempDirectory();
        var logger = new ListLogger();

        var guard = AdminPinGuard.Create("", dir.Path, _time, logger);
        var restarted = AdminPinGuard.Create(null, dir.Path, _time, NullLogger.Instance);

        var pin = Regex.Match(Assert.Single(logger.Messages), @": (\d{6})\. ").Groups[1].Value;
        Assert.Equal(6, pin.Length);
        Assert.True(guard.Verify(pin, Visitor));
        Assert.True(restarted.Verify(pin, Visitor));
    }

    [Fact]
    public void Create_PrefersTheConfiguredPin()
    {
        using var dir = new TempDirectory();

        var guard = AdminPinGuard.Create("9999", dir.Path, _time, NullLogger.Instance);

        Assert.True(guard.Verify("9999", Visitor));
        Assert.False(File.Exists(Path.Combine(dir.Path, "admin-pin.txt")));
    }
}
