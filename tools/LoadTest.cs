#:package Microsoft.AspNetCore.SignalR.Client@10.0.13
#:property TargetFramework=net10.0
#:property PublishAot=false

// Simulated phones for the load and soak tests (DESIGN.md §11). Each phone connects to the
// hub the way the draw page does, submits a randomly generated creature, and waits to hear
// whether it was approved. With --pin, the script also plays moderator and decides each one.
//
//   dotnet run tools/LoadTest.cs -- --url https://xyz.trycloudflare.com --phones 50 --minutes 3 --pin 123456
//
// The server allows 10 submissions per IP per minute, and every simulated phone shares your
// IP, so start the server with a higher limit first:  $env:RateLimit__MaxPerIpPerMinute = "1000"

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

var options = Options.Parse(args);
Console.WriteLine($"Simulating {options.Phones} phones over {options.Minutes} min against {options.Url}" +
                  (options.Pin is null ? " (no --pin: drawings stay pending)" : $" (approving {options.ApprovePercent}%)"));

using var http = new HttpClient { BaseAddress = new Uri(options.Url), Timeout = TimeSpan.FromSeconds(30) };
var results = new Results();
var stopwatch = Stopwatch.StartNew();

var phones = Enumerable.Range(0, options.Phones).Select(async i =>
{
    // Spread arrivals across the window, the way visitors trickle in.
    await Task.Delay(TimeSpan.FromSeconds(Random.Shared.NextDouble() * options.Minutes * 60));
    await SimulatePhoneAsync(i);
});

using (var progress = new Timer(_ => Console.WriteLine($"  {stopwatch.Elapsed:mm\\:ss}  {results.Summary()}"), null, 10_000, 10_000))
{
    await Task.WhenAll(phones);
}

Console.WriteLine();
Console.WriteLine($"Done in {stopwatch.Elapsed:mm\\:ss}. {results.Summary()}");
Console.WriteLine($"Submit latency: median {results.Percentile(50)} ms, p95 {results.Percentile(95)} ms, max {results.Percentile(100)} ms");
if (results.RateLimited > 0)
{
    Console.WriteLine("Some submissions were rate limited. For load tests, start the server with a higher RateLimit:MaxPerIpPerMinute.");
}

async Task SimulatePhoneAsync(int index)
{
    var clientId = $"loadtest-{Guid.NewGuid()}";
    var decided = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    Guid? myId = null;

    await using var hub = new HubConnectionBuilder()
        .WithUrl(new Uri(new Uri(options.Url), "/hubs/tank"))
        .WithAutomaticReconnect()
        .Build();
    hub.On<JsonElement>("CreatureApproved", c => { if (c.GetProperty("id").GetGuid() == myId) decided.TrySetResult("approved"); });
    hub.On<JsonElement>("CreatureRejected", c => { if (c.GetProperty("id").GetGuid() == myId) decided.TrySetResult("rejected"); });

    try
    {
        await hub.StartAsync();
        await hub.InvokeAsync("JoinVisitor", clientId);
        Interlocked.Increment(ref results.Connected);
    }
    catch (Exception ex)
    {
        Interlocked.Increment(ref results.HubFailures);
        Console.WriteLine($"  phone {index}: hub connection failed: {ex.Message}");
    }

    // Phones spend a while drawing before they submit.
    await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(5, 30)));

    using var form = new MultipartFormDataContent();
    var image = new ByteArrayContent(Drawing.Create());
    image.Headers.ContentType = new("image/png");
    form.Add(image, "image", "creature.png");
    form.Add(new StringContent(Drawing.RandomName()), "creatureName");
    form.Add(new StringContent(Random.Shared.Next(3) == 0 ? "" : $"Tester {index}"), "artistName");
    form.Add(new StringContent(new[] { "swimmer", "swimmer", "bottomCrawler", "floater" }[Random.Shared.Next(4)]), "habitat");
    form.Add(new StringContent(clientId), "clientId");

    var timer = Stopwatch.StartNew();
    HttpResponseMessage response;
    try
    {
        response = await http.PostAsync("/api/creatures", form);
    }
    catch (Exception ex)
    {
        Interlocked.Increment(ref results.Errors);
        Console.WriteLine($"  phone {index}: submit failed: {ex.Message}");
        return;
    }
    results.AddLatency(timer.ElapsedMilliseconds);

    if (response.StatusCode == HttpStatusCode.TooManyRequests)
    {
        Interlocked.Increment(ref results.RateLimited);
        return;
    }
    if (!response.IsSuccessStatusCode)
    {
        Interlocked.Increment(ref results.Errors);
        Console.WriteLine($"  phone {index}: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return;
    }

    Interlocked.Increment(ref results.Submitted);
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    myId = body.GetProperty("id").GetGuid();

    if (options.Pin is not null)
    {
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(1, 6)));
        var action = Random.Shared.Next(100) < options.ApprovePercent ? "approve" : "reject";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/creatures/{myId}/{action}");
        request.Headers.Add("X-Admin-Pin", options.Pin);
        var decision = await http.SendAsync(request);
        if (!decision.IsSuccessStatusCode) Console.WriteLine($"  phone {index}: {action} returned HTTP {(int)decision.StatusCode}");

        if (await Task.WhenAny(decided.Task, Task.Delay(TimeSpan.FromSeconds(15))) == decided.Task)
        {
            Interlocked.Increment(ref results.HeardBack);
        }
        else
        {
            Interlocked.Increment(ref results.NeverHeardBack);
        }
    }

    // Stay "connected" for a bit, like a visitor watching the TV.
    await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(10, 40)));
}

sealed class Results
{
    public int Connected, HubFailures, Submitted, RateLimited, Errors, HeardBack, NeverHeardBack;
    private readonly List<long> _latencies = [];

    public void AddLatency(long ms)
    {
        lock (_latencies) _latencies.Add(ms);
    }

    public long Percentile(int p)
    {
        lock (_latencies)
        {
            if (_latencies.Count == 0) return 0;
            var sorted = _latencies.Order().ToList();
            return sorted[Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Count) - 1, 0, sorted.Count - 1)];
        }
    }

    public string Summary() =>
        $"connected {Connected}, submitted {Submitted}, rate-limited {RateLimited}, errors {Errors + HubFailures}, " +
        $"status pushed {HeardBack}, status missed {NeverHeardBack}";
}

sealed record Options(string Url, int Phones, double Minutes, string? Pin, int ApprovePercent)
{
    public static Options Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        return new Options(
            Get("--url", "http://localhost:5000").TrimEnd('/'),
            int.Parse(Get("--phones", "50")),
            double.Parse(Get("--minutes", "3"), System.Globalization.CultureInfo.InvariantCulture),
            Get("--pin", "") is { Length: > 0 } pin ? pin : null,
            int.Parse(Get("--approve-percent", "85")));
    }
}

/// <summary>Random fish-ish drawings, encoded as PNG by hand (no image library needed).</summary>
static class Drawing
{
    private static readonly string[] First = ["Gloop", "Captain", "Sir", "Lady", "Mega", "Tiny", "Neon", "Spiky", "Wobbly", "Glow"];
    private static readonly string[] Second = ["Fins", "Noodle", "Bubbles", "Chomper", "Wiggles", "Jelly", "Pinchy", "Zap", "Squish", "Tuna"];

    public static string RandomName() => $"{First[System.Random.Shared.Next(First.Length)]} {Second[System.Random.Shared.Next(Second.Length)]}";

    public static byte[] Create()
    {
        var rnd = System.Random.Shared;
        int w = rnd.Next(220, 420), h = rnd.Next(160, 300);
        var rgba = new byte[w * h * 4];
        var body = (R: (byte)rnd.Next(80, 256), G: (byte)rnd.Next(80, 256), B: (byte)rnd.Next(80, 256));
        var stripe = (R: (byte)(255 - body.R), G: (byte)(255 - body.G), B: (byte)(255 - body.B));
        double cx = w * 0.58, cy = h / 2.0, rx = w * 0.38, ry = h * 0.4;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var dx = (x - cx) / rx;
                var dy = (y - cy) / ry;
                var inBody = dx * dx + dy * dy <= 1;
                var tailX = (cx - rx - x) / (w * 0.2);
                var inTail = tailX is > -0.3 and < 1 && Math.Abs(y - cy) < (tailX + 0.3) * h * 0.35;
                var inEye = Math.Pow(x - (cx + rx * 0.55), 2) + Math.Pow(y - (cy - ry * 0.25), 2) < Math.Pow(h * 0.05, 2);
                if (!inBody && !inTail) continue;

                var color = inEye ? (R: (byte)17, G: (byte)17, B: (byte)17) : (x / 18) % 3 == 0 && inBody ? stripe : body;
                var p = (y * w + x) * 4;
                rgba[p] = color.R;
                rgba[p + 1] = color.G;
                rgba[p + 2] = color.B;
                rgba[p + 3] = 255;
            }
        }
        return EncodePng(w, h, rgba);
    }

    private static byte[] EncodePng(int w, int h, byte[] rgba)
    {
        var raw = new byte[h * (w * 4 + 1)];
        for (var y = 0; y < h; y++) Buffer.BlockCopy(rgba, y * w * 4, raw, y * (w * 4 + 1) + 1, w * 4);

        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)h);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", idat.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
        s.Write(buffer);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc(typeAndData));
        s.Write(buffer);
    }

    private static uint Crc(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
