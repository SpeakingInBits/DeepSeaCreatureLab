using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CreatureLab.Services;
using Microsoft.Extensions.Logging;

namespace CreatureLab.Tests;

/// <summary>A clock tests can move by hand. Local time is UTC so "today" is deterministic.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Collects log messages so tests can check what was printed.</summary>
public sealed class ListLogger : ILogger
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}

/// <summary>A scratch folder that deletes itself.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"creature-lab-tests-{Guid.NewGuid():N}");

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file may still be open on Windows; it's in the temp folder anyway.
        }
    }
}

/// <summary>Builds small PNG files (valid or deliberately broken) without an image library.</summary>
public static class TestPng
{
    public static byte[] Create(int width = 32, int height = 24, byte colorType = 6, byte bitDepth = 8, byte interlace = 0,
        Action<List<(string Type, byte[] Data)>>? tamper = null)
    {
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var raw = interlace == 0
            ? RawRows(width, height, channels, bitDepth)
            : RawAdam7(width, height, channels, bitDepth);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = bitDepth;
        header[9] = colorType;
        header[12] = interlace;

        var chunks = new List<(string Type, byte[] Data)> { ("IHDR", header) };
        if (colorType == 3) chunks.Add(("PLTE", [255, 0, 0, 0, 255, 0, 0, 0, 255]));
        chunks.Add(("IDAT", Deflate(raw)));
        chunks.Add(("IEND", []));

        tamper?.Invoke(chunks);
        return Assemble(chunks);
    }

    public static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        return output.ToArray();
    }

    public static byte[] Assemble(IEnumerable<(string Type, byte[] Data)> chunks)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        foreach (var (type, data) in chunks)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            var buffer = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
            ms.Write(buffer);
            ms.Write(typeBytes);
            ms.Write(data);
            BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32.Compute([.. typeBytes, .. data]));
            ms.Write(buffer);
        }
        return ms.ToArray();
    }

    private static byte[] RawRows(int width, int height, int channels, int bitDepth)
    {
        var rowBytes = (width * channels * bitDepth + 7) / 8;
        var raw = new byte[height * (rowBytes + 1)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < rowBytes; x++) raw[y * (rowBytes + 1) + 1 + x] = (byte)(x * 7 + y * 3);
        }
        return raw;
    }

    private static byte[] RawAdam7(int width, int height, int channels, int bitDepth)
    {
        (int X, int Y, int DX, int DY)[] passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
        var parts = new List<byte>();
        foreach (var (x, y, dx, dy) in passes)
        {
            var w = (width - x + dx - 1) / dx;
            var h = (height - y + dy - 1) / dy;
            if (w > 0 && h > 0) parts.AddRange(RawRows(w, h, channels, bitDepth));
        }
        return [.. parts];
    }
}
