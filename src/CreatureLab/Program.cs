using System.Text.Json;
using System.Text.Json.Serialization;
using CreatureLab.Endpoints;
using CreatureLab.Hubs;
using CreatureLab.Options;
using CreatureLab.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Optional, gitignored file for event-day secrets such as the admin PIN.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddOptions<LabOptions>().Bind(builder.Configuration);

static LabOptions Lab(IServiceProvider sp) => sp.GetRequiredService<IOptions<LabOptions>>().Value;
static string ContentPath(IServiceProvider sp, string relative) =>
    Path.GetFullPath(Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, relative));
static string DataPath(IServiceProvider sp) => ContentPath(sp, Lab(sp).Storage.DataPath);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new CreatureStore(
    DataPath(sp), Lab(sp).Tank.PopulationCap, Lab(sp).Moderation.MaxPending,
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CreatureStore>>()));
builder.Services.AddSingleton(sp => new RuntimeSettings(
    DataPath(sp),
    new LabSettings(Lab(sp).PublicBaseUrl, Lab(sp).Moderation.AutoApprove, Lab(sp).Snapshots.IntervalMinutes),
    sp.GetRequiredService<ILogger<RuntimeSettings>>()));
builder.Services.AddSingleton(sp => NameValidator.FromFile(
    ContentPath(sp, Lab(sp).Moderation.ProfanityListPath),
    Lab(sp).Upload.MaxNameLength,
    sp.GetRequiredService<ILogger<NameValidator>>()));
builder.Services.AddSingleton(sp => new SubmissionRateLimiter(
    TimeSpan.FromSeconds(Lab(sp).RateLimit.SecondsPerDevice), Lab(sp).RateLimit.MaxPerIpPerMinute, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => AdminPinGuard.Create(
    Lab(sp).Admin.Pin, DataPath(sp), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<AdminPinGuard>>()));
builder.Services.AddSingleton(sp => new SnapshotStore(DataPath(sp), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ConnectionTracker>();
builder.Services.AddSingleton<MessageCounter>();
builder.Services.AddSingleton<TankNotifier>();
builder.Services.AddSingleton<GalleryExporter>();
builder.Services.AddSingleton<StatsBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StatsBroadcaster>());

// Enums travel as camelCase strings ("bottomCrawler", "pending") in both REST and SignalR.
var enumConverter = new JsonStringEnumConverter(JsonNamingPolicy.CamelCase);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(enumConverter);
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(enumConverter));

// Behind cloudflared or Dev Tunnels, the real client IP and https scheme arrive in
// X-Forwarded-* headers from the tunnel client on this machine (loopback, trusted by default).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

app.UseForwardedHeaders();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers.XFrameOptions = "DENY";
    headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
        "connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

// /draw, /tank and /admin serve their index.html without a redirect (one less round trip on cellular).
app.UseRewriter(new RewriteOptions().AddRewrite("^(draw|tank|admin)$", "$1/index.html", skipRemainingRules: true));
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Pages revalidate on every load, so a fix deployed mid-event shows up on refresh.
        if (!ctx.Context.Request.Path.StartsWithSegments("/lib"))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    },
});

app.MapGet("/", () => Results.Redirect("/draw"));
app.MapCreatureEndpoints();
app.MapAdminEndpoints();
app.MapHub<TankHub>("/hubs/tank");

app.Lifetime.ApplicationStarted.Register(() =>
{
    // Create these now (rather than on first request) so their startup messages, including
    // a generated admin PIN, appear in the console right away.
    var store = app.Services.GetRequiredService<CreatureStore>();
    app.Services.GetRequiredService<NameValidator>();
    app.Services.GetRequiredService<AdminPinGuard>();
    var settings = app.Services.GetRequiredService<RuntimeSettings>().Current;

    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Data folder: {DataPath}", store.DataDirectory);
    logger.LogInformation("Tank (open on the TV): http://localhost:5000/tank");
    logger.LogInformation("Admin: http://localhost:5000/admin   Draw: http://localhost:5000/draw");
    logger.LogInformation("Public URL for the QR code: {Url}",
        settings.PublicBaseUrl.Length > 0 ? settings.PublicBaseUrl : "not set (paste the tunnel URL on the admin page)");
});

app.Run();

public partial class Program { }
