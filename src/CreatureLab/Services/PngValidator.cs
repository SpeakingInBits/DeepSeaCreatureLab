using System.Buffers.Binary;
using System.IO.Compression;

namespace CreatureLab.Services;

public readonly record struct PngCheck(bool IsValid, string? Error, int Width, int Height)
{
    public static PngCheck Fail(string error) => new(false, error, 0, 0);
}

/// <summary>
/// Checks that an upload really is a PNG without any image library: signature,
/// chunk structure and CRCs, IHDR limits, then inflates the pixel data and checks
/// it is exactly the size the header promises (with valid row filter bytes).
/// </summary>
public static class PngValidator
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Adam7 interlacing: (xStart, yStart, xStep, yStep) for each of the 7 passes.
    private static readonly (int X, int Y, int DX, int DY)[] Adam7 =
        [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    private const string Corrupt = "That image file is damaged. Try releasing it again.";

    public static bool HasPngSignature(ReadOnlySpan<byte> data) =>
        data.Length >= Signature.Length && data[..Signature.Length].SequenceEqual(Signature);

    public static PngCheck Validate(byte[] data, int maxBytes, int maxDimension)
    {
        if (data.Length == 0) return PngCheck.Fail("The drawing was empty.");
        if (data.Length > maxBytes) return PngCheck.Fail("That drawing is too big to send. Try a simpler one?");
        if (!HasPngSignature(data)) return PngCheck.Fail("That isn't a PNG image.");

        var pos = Signature.Length;
        var sawHeader = false;
        var sawPalette = false;
        var sawEnd = false;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        using var compressed = new MemoryStream();

        while (pos < data.Length)
        {
            if (data.Length - pos < 12) return PngCheck.Fail(Corrupt);

            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (length > (uint)(data.Length - pos - 12)) return PngCheck.Fail(Corrupt);

            var len = (int)length;
            var type = data.AsSpan(pos + 4, 4);
            var body = data.AsSpan(pos + 8, len);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 8 + len));
            if (Crc32.Compute(data.AsSpan(pos + 4, 4 + len)) != crc) return PngCheck.Fail(Corrupt);

            if (!sawHeader)
            {
                if (!type.SequenceEqual("IHDR"u8) || len != 13) return PngCheck.Fail(Corrupt);

                var w = BinaryPrimitives.ReadUInt32BigEndian(body);
                var h = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                if (w == 0 || h == 0 || w > maxDimension || h > maxDimension)
                {
                    return PngCheck.Fail($"Drawings can be at most {maxDimension}×{maxDimension} pixels.");
                }

                width = (int)w;
                height = (int)h;
                bitDepth = body[8];
                colorType = body[9];
                interlace = body[12];
                if (BitsPerPixel(colorType, bitDepth) == 0 || body[10] != 0 || body[11] != 0 || interlace > 1)
                {
                    return PngCheck.Fail(Corrupt);
                }

                sawHeader = true;
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                return PngCheck.Fail(Corrupt);
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                sawPalette = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                sawEnd = true;
                pos += 12 + len;
                break;
            }

            pos += 12 + len;
        }

        // Nothing may follow IEND: that's where polyglot files hide extra content.
        if (!sawEnd || pos != data.Length || compressed.Length == 0) return PngCheck.Fail(Corrupt);
        if (colorType == 3 && !sawPalette) return PngCheck.Fail(Corrupt);

        var bpp = BitsPerPixel(colorType, bitDepth);
        var expected = ExpectedRawSize(width, height, bpp, interlace);
        compressed.Position = 0;
        if (!InflatesTo(compressed, expected, interlace == 0 ? 1L + RowBytes(width, bpp) : 0))
        {
            return PngCheck.Fail(Corrupt);
        }

        return new PngCheck(true, null, width, height);
    }

    /// <summary>Inflates the IDAT stream, stopping early if it is larger than expected (no zip bombs).</summary>
    private static bool InflatesTo(Stream compressed, long expected, long rowStride)
    {
        try
        {
            using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
            var buffer = new byte[16 * 1024];
            long total = 0;
            long nextFilterByte = 0;
            int read;
            while ((read = inflater.Read(buffer)) > 0)
            {
                if (total + read > expected) return false;

                // Every row starts with a filter type byte, 0 to 4.
                while (rowStride > 0 && nextFilterByte < total + read)
                {
                    if (buffer[nextFilterByte - total] > 4) return false;
                    nextFilterByte += rowStride;
                }

                total += read;
            }

            return total == expected;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static int BitsPerPixel(int colorType, int bitDepth) => (colorType, bitDepth) switch
    {
        (0, 1 or 2 or 4 or 8 or 16) => bitDepth,          // grayscale
        (2, 8 or 16) => bitDepth * 3,                     // RGB
        (3, 1 or 2 or 4 or 8) => bitDepth,                // palette
        (4, 8 or 16) => bitDepth * 2,                     // grayscale + alpha
        (6, 8 or 16) => bitDepth * 4,                     // RGBA
        _ => 0,
    };

    private static long RowBytes(int width, int bitsPerPixel) => ((long)width * bitsPerPixel + 7) / 8;

    private static long ExpectedRawSize(int width, int height, int bitsPerPixel, int interlace)
    {
        if (interlace == 0) return height * (1 + RowBytes(width, bitsPerPixel));

        long size = 0;
        foreach (var (x, y, dx, dy) in Adam7)
        {
            var passWidth = (width - x + dx - 1) / dx;
            var passHeight = (height - y + dy - 1) / dy;
            if (passWidth > 0 && passHeight > 0) size += passHeight * (1 + RowBytes(passWidth, bitsPerPixel));
        }
        return size;
    }
}

/// <summary>The CRC-32 used by PNG chunks (same polynomial as zip).</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
