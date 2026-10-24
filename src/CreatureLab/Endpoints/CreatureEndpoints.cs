using System.Globalization;
using CreatureLab.Models;
using CreatureLab.Options;
using CreatureLab.Services;
using Microsoft.Extensions.Options;
using static CreatureLab.Endpoints.RequestHelpers;

namespace CreatureLab.Endpoints;

public sealed record SubmitResponse(Guid Id, CreatureStatus Status);

public sealed record StatusResponse(Guid Id, CreatureStatus Status);

public sealed record ConfigResponse(
    string PublicBaseUrl,
    int SnapshotIntervalMinutes,
    bool SnapshotIncludeOverlay,
    int PopulationCap,
    int RateLimitSeconds,
    int MaxNameLength,
    string CreditLine,
    IReadOnlyList<ProgramLink> Programs);

/// <summary>Public endpoints used by the draw page and the tank (DESIGN.md §8.2).</summary>
public static class CreatureEndpoints
{
    public static IEndpointRouteBuilder MapCreatureEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapPost("/creatures", SubmitAsync);
        api.MapGet("/creatures/{id:guid}", GetStatus);
        api.MapGet("/creatures/{id:guid}/image", GetImage);
        api.MapGet("/tank", (CreatureStore store) => store.InTank().Select(CreatureSummary.From));
        api.MapGet("/config", GetConfig);
        api.MapPost("/snapshots", UploadSnapshotAsync);
        return app;
    }

    private static async Task<IResult> SubmitAsync(
        HttpContext http,
        CreatureStore store,
        NameValidator names,
        SubmissionRateLimiter limiter,
        RuntimeSettings settings,
        TankNotifier notifier,
        MessageCounter messages,
        IOptions<LabOptions> options,
        CancellationToken cancellationToken)
    {
        messages.Record();
        var upload = options.Value.Upload;
        LimitRequestBody(http, upload.MaxBytes + 16 * 1024);

        if (!http.Request.HasFormContentType) return Error(400, "Expected a drawing upload.");

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Error(413, "That drawing is too big to send. Try a simpler one?");
        }
        catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException or IOException)
        {
            return Error(400, "The upload didn't come through. Try again?");
        }

        var clientId = form["clientId"].ToString();
        if (!ClientIds.IsValid(clientId)) return Error(400, "Missing device ID. Reload the page and try again.");

        if (!Enum.TryParse<Habitat>(form["habitat"].ToString(), ignoreCase: true, out var habitat) || !Enum.IsDefined(habitat))
        {
            return Error(400, "Pick where your creature lives.");
        }

        var creatureName = names.Check(form["creatureName"], required: true, "creature name");
        if (!creatureName.IsValid) return Error(400, creatureName.Error!);

        var artistName = names.Check(form["artistName"], required: false, "artist name");
        if (!artistName.IsValid) return Error(400, artistName.Error!);

        var file = form.Files.GetFile("image");
        if (file is null || file.Length == 0) return Error(400, "Draw something first!");
        if (file.Length > upload.MaxBytes) return Error(413, "That drawing is too big to send. Try a simpler one?");

        byte[] png;
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await file.CopyToAsync(buffer, cancellationToken);
            png = buffer.ToArray();
        }

        var check = PngValidator.Validate(png, upload.MaxBytes, upload.MaxDimension);
        if (!check.IsValid) return Error(400, check.Error!);

        // Validation passed; only now spend the visitor's rate-limit budget, so fixing a
        // rejected name doesn't make them wait 30 seconds.
        var autoApprove = settings.Current.AutoApprove;
        if (!autoApprove && store.PendingCount >= store.MaxPending) return QueueFull();

        var decision = limiter.TryAcquire(clientId, ClientIp(http));
        if (!decision.Allowed)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            var message = decision.Reason == RateLimitReason.Device
                ? $"You just released a creature! You can release another in {seconds} seconds."
                : $"Lots of creatures are coming from your network right now. Try again in {seconds} seconds.";
            return Results.Json(new ErrorBody(message, seconds), statusCode: StatusCodes.Status429TooManyRequests);
        }

        var result = store.Submit(new NewCreature(creatureName.Value!, artistName.Value, habitat, clientId), png, autoApprove);
        if (result.Outcome == StoreOutcome.PendingQueueFull) return QueueFull();

        var creature = result.Creature!;
        if (creature.Status == CreatureStatus.Approved)
        {
            await notifier.ActivityAsync(ActivityKinds.Submit);
            await notifier.CreatureApprovedAsync(creature, result.Retired);
        }
        else
        {
            await notifier.CreatureSubmittedAsync(creature);
        }

        return Results.Ok(new SubmitResponse(creature.Id, creature.Status));

        static IResult QueueFull() => Error(503, "The tank crew is swamped right now. Try again in a minute!");
    }

    /// <summary>Status polling, the fallback when a phone's SignalR connection drops.</summary>
    private static IResult GetStatus(Guid id, CreatureStore store, MessageCounter messages)
    {
        messages.Record();
        return store.Get(id) is { } creature
            ? Results.Ok(new StatusResponse(creature.Id, creature.Status))
            : Error(404, "We couldn't find that creature.");
    }

    private static IResult GetImage(Guid id, CreatureStore store, HttpContext http)
    {
        if (store.Get(id) is not { } creature) return Results.NotFound();

        var path = store.ImagePath(creature);
        if (!File.Exists(path)) return Results.NotFound();

        // Drawings never change once saved.
        http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.File(path, "image/png");
    }

    private static ConfigResponse GetConfig(RuntimeSettings settings, IOptions<LabOptions> options)
    {
        var o = options.Value;
        var s = settings.Current;
        return new ConfigResponse(
            s.PublicBaseUrl,
            s.SnapshotIntervalMinutes,
            o.Snapshots.IncludeOverlay,
            o.Tank.PopulationCap,
            o.RateLimit.SecondsPerDevice,
            o.Upload.MaxNameLength,
            o.Display.CreditLine,
            o.Programs);
    }

    /// <summary>Timed tank snapshots. Accepted from the booth laptop itself, or with the admin PIN.</summary>
    private static async Task<IResult> UploadSnapshotAsync(
        HttpContext http,
        AdminPinGuard pins,
        SnapshotStore snapshots,
        IOptions<LabOptions> options,
        CancellationToken cancellationToken)
    {
        var pin = http.Request.Headers[PinHeader].ToString();
        var allowed = IsLocalRequest(http) || (pin.Length > 0 && pins.Verify(pin, http.Connection.RemoteIpAddress));
        if (!allowed) return Error(401, "Snapshots are only accepted from the booth laptop.");

        LimitRequestBody(http, options.Value.Snapshots.MaxBytes);
        byte[] png;
        try
        {
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, cancellationToken);
            png = buffer.ToArray();
        }
        catch (BadHttpRequestException)
        {
            return Error(413, "Snapshot too large.");
        }

        if (!PngValidator.HasPngSignature(png)) return Error(400, "Snapshots must be PNG images.");

        var name = snapshots.Save(png);
        return Results.Ok(new { file = name });
    }
}
