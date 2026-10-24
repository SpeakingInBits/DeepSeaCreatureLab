using CreatureLab.Models;
using CreatureLab.Services;
using static CreatureLab.Endpoints.RequestHelpers;

namespace CreatureLab.Endpoints;

/// <summary>Moderator endpoints. Every request needs the PIN in the X-Admin-Pin header.</summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").AddEndpointFilter(RequirePinAsync);

        admin.MapGet("/pending", (CreatureStore store) => store.Pending().Select(CreatureSummary.From));
        admin.MapPost("/creatures/{id:guid}/approve", ApproveAsync);
        admin.MapPost("/creatures/{id:guid}/reject", RejectAsync);
        admin.MapPost("/creatures/{id:guid}/remove", RemoveAsync);
        admin.MapGet("/settings", (RuntimeSettings settings) => settings.Current);
        admin.MapPost("/settings", UpdateSettingsAsync);
        admin.MapGet("/stats", (StatsBroadcaster stats) => stats.Current());
        admin.MapPost("/snapshot", async (TankNotifier notifier) =>
        {
            await notifier.TakeSnapshotAsync();
            return Results.NoContent();
        });
        admin.MapGet("/export", (GalleryExporter exporter) =>
            Results.File(exporter.CreateZip(), "application/zip", exporter.DownloadName()));

        return app;
    }

    private static async ValueTask<object?> RequirePinAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var pins = http.RequestServices.GetRequiredService<AdminPinGuard>();
        http.RequestServices.GetRequiredService<MessageCounter>().Record();

        var ip = http.Connection.RemoteIpAddress;
        if (pins.IsLockedOut(ip)) return Error(429, "Too many wrong PINs. Wait a few minutes and try again.");
        if (!pins.Verify(http.Request.Headers[PinHeader].ToString(), ip)) return Error(401, "Wrong PIN.");

        return await next(context);
    }

    private static async Task<IResult> ApproveAsync(Guid id, CreatureStore store, TankNotifier notifier)
    {
        var result = store.Approve(id);
        if (result.Outcome != StoreOutcome.Ok) return DecisionError(result);

        await notifier.CreatureApprovedAsync(result.Creature!, result.Retired);
        return Results.Ok(CreatureSummary.From(result.Creature!));
    }

    private static async Task<IResult> RejectAsync(Guid id, CreatureStore store, TankNotifier notifier)
    {
        var result = store.Reject(id);
        if (result.Outcome != StoreOutcome.Ok) return DecisionError(result);

        await notifier.CreatureRejectedAsync(result.Creature!);
        return Results.Ok(CreatureSummary.From(result.Creature!));
    }

    private static async Task<IResult> RemoveAsync(Guid id, CreatureStore store, TankNotifier notifier)
    {
        var result = store.Remove(id);
        if (result.Outcome != StoreOutcome.Ok) return DecisionError(result);

        await notifier.CreatureRemovedAsync(result.Creature!);
        return Results.Ok(CreatureSummary.From(result.Creature!));
    }

    private static async Task<IResult> UpdateSettingsAsync(SettingsUpdate update, RuntimeSettings settings, TankNotifier notifier)
    {
        if (!settings.TryUpdate(update, out var current, out var error)) return Error(400, error!);

        await notifier.SettingsChangedAsync(current);
        return Results.Ok(current);
    }

    private static IResult DecisionError(StoreResult result) => result.Outcome switch
    {
        StoreOutcome.NotFound => Error(404, "That creature doesn't exist."),
        _ => Error(409, $"That creature is already {result.Creature!.Status.ToString().ToLowerInvariant()}."),
    };
}
