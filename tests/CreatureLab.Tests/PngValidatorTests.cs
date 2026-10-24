using System.Buffers.Binary;
using CreatureLab.Services;

namespace CreatureLab.Tests;

public sealed class PngValidatorTests
{
    private const int MaxBytes = 256 * 1024;
    private const int MaxDimension = 512;

    private static PngCheck Validate(byte[] data) => PngValidator.Validate(data, MaxBytes, MaxDimension);

    [Theory]
    [InlineData(6, 8)]   // RGBA, what browsers export
    [InlineData(2, 8)]   // RGB
    [InlineData(0, 1)]   // 1-bit grayscale
    [InlineData(3, 4)]   // palette
    [InlineData(4, 16)]  // gray + alpha, 16-bit
    public void AcceptsValidPngs(byte colorType, byte bitDepth)
    {
        var result = Validate(TestPng.Create(37, 21, colorType, bitDepth));

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(37, result.Width);
        Assert.Equal(21, result.Height);
    }

    [Fact]
    public void AcceptsInterlacedPng()
    {
        Assert.True(Validate(TestPng.Create(13, 9, interlace: 1)).IsValid);
    }

    [Fact]
    public void AcceptsTheMaximumSize()
    {
        Assert.True(Validate(TestPng.Create(512, 512)).IsValid);
    }

    [Theory]
    [InlineData(513, 10)]
    [InlineData(10, 513)]
    public void RejectsOversizedDimensions(int width, int height)
    {
        var result = Validate(TestPng.Create(width, height));

        Assert.False(result.IsValid);
        Assert.Contains("512", result.Error);
    }

    [Fact]
    public void RejectsFilesOverTheByteLimit()
    {
        var result = PngValidator.Validate(TestPng.Create(), maxBytes: 50, MaxDimension);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RejectsOtherFileTypes()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];

        Assert.False(Validate(jpeg).IsValid);
        Assert.False(Validate("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray()).IsValid);
        Assert.False(Validate([]).IsValid);
    }

    [Fact]
    public void RejectsABadChunkCrc()
    {
        var png = TestPng.Create();
        png[^1] ^= 0xFF; // last byte of the IEND chunk's CRC

        Assert.False(Validate(png).IsValid);
    }

    [Fact]
    public void RejectsTruncatedFiles()
    {
        var png = TestPng.Create();

        Assert.False(Validate(png[..(png.Length - 20)]).IsValid);
    }

    [Fact]
    public void RejectsDataAfterTheEnd()
    {
        var png = TestPng.Create();
        byte[] withExtra = [.. png, .. "<script>alert(1)</script>"u8.ToArray()];

        Assert.False(Validate(withExtra).IsValid);
    }

    [Fact]
    public void RejectsPixelDataThatIsTooShort()
    {
        var png = TestPng.Create(20, 20, tamper: chunks =>
        {
            var i = chunks.FindIndex(c => c.Type == "IDAT");
            chunks[i] = ("IDAT", TestPng.Deflate(new byte[100]));
        });

        Assert.False(Validate(png).IsValid);
    }

    [Fact]
    public void RejectsPixelDataThatIsTooLong()
    {
        // A tiny header with a big payload: the shape of a decompression bomb.
        var png = TestPng.Create(4, 4, tamper: chunks =>
        {
            var i = chunks.FindIndex(c => c.Type == "IDAT");
            chunks[i] = ("IDAT", TestPng.Deflate(new byte[1_000_000]));
        });

        Assert.False(Validate(png).IsValid);
    }

    [Fact]
    public void RejectsInvalidRowFilterBytes()
    {
        var png = TestPng.Create(8, 8, tamper: chunks =>
        {
            var raw = new byte[8 * (1 + 8 * 4)];
            raw[0] = 9; // filter types only go up to 4
            var i = chunks.FindIndex(c => c.Type == "IDAT");
            chunks[i] = ("IDAT", TestPng.Deflate(raw));
        });

        Assert.False(Validate(png).IsValid);
    }

    [Fact]
    public void RejectsMissingIdat()
    {
        var png = TestPng.Create(tamper: chunks => chunks.RemoveAll(c => c.Type == "IDAT"));

        Assert.False(Validate(png).IsValid);
    }

    [Fact]
    public void RejectsPaletteImageWithoutPalette()
    {
        var png = TestPng.Create(colorType: 3, bitDepth: 8, tamper: chunks => chunks.RemoveAll(c => c.Type == "PLTE"));

        Assert.False(Validate(png).IsValid);
    }

    [Fact]
    public void RejectsInvalidBitDepthForColorType()
    {
        var png = TestPng.Create(tamper: chunks =>
        {
            var header = chunks[0].Data;
            header[8] = 4; // RGBA can't be 4-bit
        });

        Assert.False(Validate(png).IsValid);
    }

    [Fact]
    public void RejectsHugeChunkLength()
    {
        var png = TestPng.Create();
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 0xFFFFFFF0);

        Assert.False(Validate(png).IsValid);
    }
}
