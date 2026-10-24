using CreatureLab.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace CreatureLab.Services;

public sealed record StatsPayload(
    int ConnectedPhones,
    int SubmissionsTotal,
    int SubmissionsToday,
    int ApprovedTotal,
    int InTank,
    int Pending,
    int MessagesPerMinute);

/// <summary>Pushes the live counters to the TV and moderators every 2 seconds.</summary>
public sealed class StatsBroadcaster(
    IHubContext<TankHub> hub,
    CreatureStore store,
    ConnectionTracker connections,
    MessageCounter messages,
    ILogger<StatsBroadcaster> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    public StatsPayload Current()
    {
        var counts = store.Counts();
        return new StatsPayload(
            connections.ConnectedPhones,
            counts.SubmissionsTotal,
            counts.SubmissionsToday,
            counts.ApprovedTotal,
            counts.InTank,
            counts.Pending,
            messages.PerMinute());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await hub.Clients.Groups(TankHub.DisplayGroup, TankHub.AdminGroup)
                        .SendAsync("Stats", Current(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not broadcast stats.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
