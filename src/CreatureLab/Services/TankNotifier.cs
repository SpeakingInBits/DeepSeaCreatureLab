using CreatureLab.Hubs;
using CreatureLab.Models;
using Microsoft.AspNetCore.SignalR;

namespace CreatureLab.Services;

/// <summary>Kinds of <c>Activity</c> events; the tank animates a dot along its diagram for each.</summary>
public static class ActivityKinds
{
    public const string Connect = "connect";   // a phone opened the draw page   (phone → server)
    public const string Submit = "submit";     // a drawing was uploaded         (phone → server)
    public const string Approve = "approve";   // creature sent to the tank      (server → screen, server → phone)
    public const string Reject = "reject";     // visitor told "not this time"   (server → phone)
    public const string Remove = "remove";     // moderator pulled a creature    (server → screen)
    public const string Retire = "retire";     // population cap pushed one out  (server → screen)
}

public sealed record ActivityEvent(string Kind, DateTimeOffset At);

/// <summary>Every server-to-client SignalR event goes through here (see DESIGN.md §8.2).</summary>
public sealed class TankNotifier(IHubContext<TankHub> hub, MessageCounter messages, TimeProvider time)
{
    public async Task CreatureSubmittedAsync(Creature creature)
    {
        await SendAsync(hub.Clients.Group(TankHub.AdminGroup), "CreatureSubmitted", CreatureSummary.From(creature));
        await ActivityAsync(ActivityKinds.Submit);
    }

    public async Task CreatureApprovedAsync(Creature creature, IReadOnlyList<Creature> retired)
    {
        var groups = new[] { TankHub.DisplayGroup, TankHub.AdminGroup, TankHub.VisitorGroup(creature.ClientId) };
        await SendAsync(hub.Clients.Groups(groups), "CreatureApproved", CreatureSummary.From(creature));
        await ActivityAsync(ActivityKinds.Approve);

        foreach (var r in retired)
        {
            await SendAsync(hub.Clients.Groups(TankHub.DisplayGroup, TankHub.AdminGroup), "CreatureRetired", new CreatureRef(r.Id));
            await ActivityAsync(ActivityKinds.Retire);
        }
    }

    public async Task CreatureRejectedAsync(Creature creature)
    {
        var groups = new[] { TankHub.AdminGroup, TankHub.VisitorGroup(creature.ClientId) };
        await SendAsync(hub.Clients.Groups(groups), "CreatureRejected", new CreatureRef(creature.Id));
        await ActivityAsync(ActivityKinds.Reject);
    }

    public async Task CreatureRemovedAsync(Creature creature)
    {
        await SendAsync(hub.Clients.Groups(TankHub.DisplayGroup, TankHub.AdminGroup), "CreatureRemoved", new CreatureRef(creature.Id));
        await ActivityAsync(ActivityKinds.Remove);
    }

    public Task SettingsChangedAsync(LabSettings settings) =>
        SendAsync(hub.Clients.Groups(TankHub.DisplayGroup, TankHub.AdminGroup), "SettingsChanged", settings);

    public Task TakeSnapshotAsync() =>
        SendAsync(hub.Clients.Group(TankHub.DisplayGroup), "TakeSnapshot", new { });

    public Task ActivityAsync(string kind) =>
        hub.Clients.Group(TankHub.DisplayGroup).SendAsync("Activity", new ActivityEvent(kind, time.GetUtcNow()));

    private Task SendAsync(IClientProxy target, string method, object payload)
    {
        messages.Record();
        return target.SendAsync(method, payload);
    }
}
