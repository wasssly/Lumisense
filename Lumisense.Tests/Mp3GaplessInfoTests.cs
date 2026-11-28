using Lumisense;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace Lumisense.Tests;

public sealed class Mp3GaplessInfoTests
{
    // MPEG-1 Layer III, 44,1 кГц, стерео, без CRC: Xing/Info начинается через 4 + 32 байта.
    private static byte[] BuildFrame(string tag = "Info", int frames = 1000, int delay = 576, int padding = 1500, bool lame = true)
    {
        var bytes = new List<byte> { 0xFF, 0xFB, 0x90, 0x00 };
        bytes.AddRange(new byte[32]);
        bytes.AddRange(Encoding.ASCII.GetBytes(tag));
        bytes.AddRange(new byte[] { 0, 0, 0, 1 });
        bytes.AddRange(new[] { (byte)(frames >> 24), (byte)(frames >> 16), (byte)(frames >> 8), (byte)frames });
        bytes.AddRange(Encoding.ASCII.GetBytes(lame ? "LAME3.99r" : "XXXX3.99r"));
        bytes.AddRange(new byte[12]);
        bytes.Add((byte)(delay >> 4));
        bytes.Add((byte)(((delay & 0xF) << 4) | (padding >> 8)));
        bytes.Add((byte)padding);
        return bytes.ToArray();
    }

    [Fact]
    public void LameHeader_IsParsed()
    {
        Mp3GaplessInfo? info = Mp3GaplessInfo.Parse(BuildFrame());

        Assert.Equal(new Mp3GaplessInfo(1152, 1000, 576, 1500), info);
    }

    [Fact]
    public void Id3Prefix_IsSkipped()
    {
        byte[] id3 = [(byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 5, 1, 2, 3, 4, 5];

        Mp3GaplessInfo? info = Mp3GaplessInfo.Parse(id3.Concat(BuildFrame("Xing")).ToArray());

        Assert.Equal(576, info?.EncoderDelay);
        Assert.Equal(1500, info?.EndPadding);
    }

    [Fact]
    public void MissingLameExtension_ReturnsNull() =>
        Assert.Null(Mp3GaplessInfo.Parse(BuildFrame(lame: false)));

    [Fact]
    public void RandomData_ReturnsNull() =>
        Assert.Null(Mp3GaplessInfo.Parse(new byte[2000]));
}
