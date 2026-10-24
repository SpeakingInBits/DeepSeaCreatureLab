using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace CreatureLab.Tests;

/// <summary>End-to-end through the real HTTP pipeline, with data in a temp folder.</summary>
public sealed class ApiTests : IDisposable
{
    private const string Pin = "test-pin-2468";
    private readonly TempDirectory _dir = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Storage:DataPath", _dir.Path)
            .UseSetting("Admin:Pin", Pin)
            .UseSetting("Snapshots:IntervalMinutes", "0")
            .UseSetting("RateLimit:RequireConnection", "false")); // LiveConnectionTests covers it

        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _dir.Dispose();
    }

    private static MultipartFormDataContent Drawing(string name = "Gloop", string? artist = "Sam", string habitat = "swimmer",
        string? clientId = null, byte[]? image = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image ?? TestPng.Create(64, 48));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "image", "creature.png");
        form.Add(new StringContent(name), "creatureName");
        if (artist is not null) form.Add(new StringContent(artist), "artistName");
        form.Add(new StringContent(habitat), "habitat");
        form.Add(new StringContent(clientId ?? $"client-{Guid.NewGuid()}"), "clientId");
        return form;
    }

    private async Task<Guid> SubmitAsync(string name = "Gloop")
    {
        var res = await _client.PostAsync("/api/creatures", Drawing(name));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", body.GetProperty("status").GetString());
        return body.GetProperty("id").GetGuid();
    }

    private HttpRequestMessage Admin(HttpMethod method, string url, string pin = Pin)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Admin-Pin", pin);
        return request;
    }

    private static async Task<string> ErrorOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    [Fact]
    public async Task SubmitApproveAndShowInTank()
    {
        var id = await SubmitAsync("Gloop the Destroyer");

        var status = await _client.GetFromJsonAsync<JsonElement>($"/api/creatures/{id}");
        Assert.Equal("pending", status.GetProperty("status").GetString());
        Assert.Empty(await _client.GetFromJsonAsync<JsonElement[]>("/api/tank") ?? []);

        var approve = await _client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/creatures/{id}/approve"));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var tank = await _client.GetFromJsonAsync<JsonElement[]>("/api/tank");
        var creature = Assert.Single(tank!);
        Assert.Equal("Gloop the Destroyer", creature.GetProperty("creatureName").GetString());
        Assert.Equal("Sam", creature.GetProperty("artistDisplay").GetString());
        Assert.Equal("swimmer", creature.GetProperty("habitat").GetString());
        Assert.False(creature.TryGetProperty("clientId", out _)); // never sent to browsers

        var image = await _client.GetAsync(creature.GetProperty("imageUrl").GetString());
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TestPng.Create(64, 48), await image.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MissingArtistShowsAsMysteryDiver()
    {
        var res = await _client.PostAsync("/api/creatures", Drawing(artist: null));
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var pending = await (await _client.SendAsync(Admin(HttpMethod.Get, "/api/admin/pending"))).Content.ReadFromJsonAsync<JsonElement[]>();

        var creature = Assert.Single(pending!, c => c.GetProperty("id").GetGuid() == id);
        Assert.Equal("a mystery diver", creature.GetProperty("artistDisplay").GetString());
    }

    [Theory]
    [InlineData("GET", "/api/admin/pending")]
    [InlineData("POST", "/api/admin/creatures/00000000-0000-0000-0000-000000000001/approve")]
    [InlineData("GET", "/api/admin/settings")]
    [InlineData("GET", "/api/admin/export")]
    public async Task AdminEndpointsNeedThePin(string method, string url)
    {
        var noPin = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
        var wrongPin = await _client.SendAsync(Admin(new HttpMethod(method), url, pin: "0000"));

        Assert.Equal(HttpStatusCode.Unauthorized, noPin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPin.StatusCode);
    }

    [Fact]
    public async Task DecidingTwiceIsAConflict()
    {
        var id = await SubmitAsync();
        await _client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/creatures/{id}/reject"));

        var again = await _client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/creatures/{id}/approve"));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("rejected", (await _client.GetFromJsonAsync<JsonElement>($"/api/creatures/{id}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task RejectsUploadsThatArentPngs()
    {
        var res = await _client.PostAsync("/api/creatures", Drawing(image: "GIF89a not really"u8.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("That isn't a PNG image.", await ErrorOf(res));
    }

    [Fact]
    public async Task RejectsBlockedNamesWithAFriendlyMessage()
    {
        var res = await _client.PostAsync("/api/creatures", Drawing(name: "Fuuuck Fish"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("different creature name", await ErrorOf(res));
    }

    [Fact]
    public async Task RejectsUnknownHabitat()
    {
        var res = await _client.PostAsync("/api/creatures", Drawing(habitat: "flyer"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SameDeviceIsRateLimited()
    {
        var first = await _client.PostAsync("/api/creatures", Drawing(clientId: "same-phone-1234"));
        var second = await _client.PostAsync("/api/creatures", Drawing(clientId: "same-phone-1234"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(body.GetProperty("retryAfterSeconds").GetInt32(), 1, 30);
    }

    [Fact]
    public async Task InvalidSubmissionsDontCountAgainstTheRateLimit()
    {
        var bad = await _client.PostAsync("/api/creatures", Drawing(name: "", clientId: "phone-5678abcd"));
        var good = await _client.PostAsync("/api/creatures", Drawing(name: "Fixed it", clientId: "phone-5678abcd"));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Fact]
    public async Task SettingsUpdateNormalizesTheUrlAndShowsInConfig()
    {
        var update = Admin(HttpMethod.Post, "/api/admin/settings");
        update.Content = JsonContent.Create(new { publicBaseUrl = "https://abc.trycloudflare.com/draw/" });

        var res = await _client.SendAsync(update);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var config = await _client.GetFromJsonAsync<JsonElement>("/api/config");
        Assert.Equal("https://abc.trycloudflare.com", config.GetProperty("publicBaseUrl").GetString());
        Assert.Equal(2, config.GetProperty("programs").GetArrayLength());
    }

    [Fact]
    public async Task AutoApproveSendsCreaturesStraightToTheTank()
    {
        var update = Admin(HttpMethod.Post, "/api/admin/settings");
        update.Content = JsonContent.Create(new { autoApprove = true });
        await _client.SendAsync(update);

        var res = await _client.PostAsync("/api/creatures", Drawing());

        Assert.Equal("approved", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Single(await _client.GetFromJsonAsync<JsonElement[]>("/api/tank") ?? []);
    }

    [Fact]
    public async Task SnapshotsNeedThePinWhenNotFromTheBoothLaptop()
    {
        var png = TestPng.Create(100, 60);

        var anonymous = await _client.PostAsync("/api/snapshots", new ByteArrayContent(png));
        var withPin = Admin(HttpMethod.Post, "/api/snapshots");
        withPin.Content = new ByteArrayContent(png);
        var allowed = await _client.SendAsync(withPin);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir.Path, "snapshots"), "*.png"));
    }

    [Fact]
    public async Task ExportZipHasCreaturesSnapshotsMetadataAndGalleryPage()
    {
        var approvedId = await SubmitAsync("Jelly Belly");
        await _client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/creatures/{approvedId}/approve"));
        var rejectedId = await SubmitAsync("Not This One");
        await _client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/creatures/{rejectedId}/reject"));
        var snapshot = Admin(HttpMethod.Post, "/api/snapshots");
        snapshot.Content = new ByteArrayContent(TestPng.Create());
        await _client.SendAsync(snapshot);

        var res = await _client.SendAsync(Admin(HttpMethod.Get, "/api/admin/export"));

        Assert.Equal("application/zip", res.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(await res.Content.ReadAsStreamAsync());
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Single(names, n => n.StartsWith("creatures/") && n.EndsWith("-jelly-belly-by-sam.png"));
        Assert.DoesNotContain(names, n => n.Contains("not-this-one"));
        Assert.Single(names, n => n.StartsWith("snapshots/"));
        Assert.Contains("index.html", names);

        using var reader = new StreamReader(zip.GetEntry("creatures.json")!.Open());
        var metadata = await reader.ReadToEndAsync();
        Assert.Contains("Jelly Belly", metadata);
        Assert.DoesNotContain("client", metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/draw")]
    [InlineData("/draw/")]
    [InlineData("/tank")]
    [InlineData("/admin")]
    public async Task PagesAreServedWithoutRedirects(string path)
    {
        var res = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        Assert.True(res.Headers.Contains("Content-Security-Policy"));
    }

    internal static MultipartFormDataContent DrawingFor(string clientId) => Drawing(clientId: clientId);
}

/// <summary>Releases are only accepted from a phone connected to the hub, like the draw page.</summary>
public sealed class LiveConnectionTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public LiveConnectionTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Storage:DataPath", _dir.Path)
            .UseSetting("Admin:Pin", "test-pin-1357")
            .UseSetting("Snapshots:IntervalMinutes", "0"));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _dir.Dispose();
    }

    private async Task<HubConnection> ConnectAsVisitorAsync(string clientId)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/tank"), o =>
            {
                o.Transports = HttpTransportType.LongPolling; // the test server speaks plain HTTP
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();
        await hub.StartAsync();
        await hub.InvokeAsync("JoinVisitor", clientId);
        return hub;
    }

    [Fact]
    public async Task ReleaseWithoutAConnection_IsTurnedAway()
    {
        var res = await _client.PostAsync("/api/creatures", ApiTests.DrawingFor("script-without-hub"));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Still connecting", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ReleaseFromAConnectedPhone_IsAccepted()
    {
        await using var hub = await ConnectAsVisitorAsync("phone-connected-1");

        var res = await _client.PostAsync("/api/creatures", ApiTests.DrawingFor("phone-connected-1"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task APhoneThatJustDisconnected_CanStillRelease()
    {
        var hub = await ConnectAsVisitorAsync("phone-blip-1");
        await hub.DisposeAsync();

        var res = await _client.PostAsync("/api/creatures", ApiTests.DrawingFor("phone-blip-1"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task AnotherPhonesConnection_DoesNotCount()
    {
        await using var hub = await ConnectAsVisitorAsync("phone-connected-2");

        var res = await _client.PostAsync("/api/creatures", ApiTests.DrawingFor("someone-else-3"));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }
}
