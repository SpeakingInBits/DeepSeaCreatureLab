using CreatureLab.Models;
using CreatureLab.Services;
using Microsoft.AspNetCore.SignalR;

namespace CreatureLab.Hubs;

/// <summary>
/// One hub for everyone. Each browser joins a group for its role: the TV joins
/// "display", moderators join "admin" (with the PIN), and each phone joins its own
/// "visitor:{clientId}" group so it can hear when its creature is approved.
/// Server-to-client events are sent through <see cref="TankNotifier"/>.
/// </summary>
public sealed class TankHub(
    AdminPinGuard pins,
    ConnectionTracker connections,
    TankNotifier notifier,
    MessageCounter messages) : Hub
{
    public const string DisplayGroup = "display";
    public const string AdminGroup = "admin";

    public static string VisitorGroup(string clientId) => $"visitor:{clientId}";

    public async Task JoinDisplay()
    {
        messages.Record();
        await Groups.AddToGroupAsync(Context.ConnectionId, DisplayGroup);
    }

    /// <returns>False when the PIN is wrong (or this IP is locked out).</returns>
    public async Task<bool> JoinAdmin(string pin)
    {
        messages.Record();
        var ip = Context.GetHttpContext()?.Connection.RemoteIpAddress;
        if (!pins.Verify(pin, ip)) return false;

        await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);
        return true;
    }

    public async Task JoinVisitor(string clientId)
    {
        messages.Record();
        if (!ClientIds.IsValid(clientId)) throw new HubException("Invalid client id.");

        await Groups.AddToGroupAsync(Context.ConnectionId, VisitorGroup(clientId));
        connections.AddVisitor(Context.ConnectionId, clientId);
        await notifier.ActivityAsync(ActivityKinds.Connect);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
