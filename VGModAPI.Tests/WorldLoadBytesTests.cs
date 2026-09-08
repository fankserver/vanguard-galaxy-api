using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldLoadBytesTests
{
    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }
    [Fact]
    public void RawAndCompressedUnicodeDecodeWithinExplicitBounds()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"name\":\"世界-é\"}");
        Assert.Equal(Encoding.UTF8.GetString(bytes), WorldLoadBytes.Decode(bytes));
        Assert.Equal(WorldLoadBytes.Decode(bytes), WorldLoadBytes.Decode(Compress(bytes)));
        Assert.Throws<InvalidDataException>(() => WorldLoadBytes.Decode(bytes, 4));
        Assert.Throws<InvalidDataException>(() => WorldLoadBytes.Decode(Compress(bytes), 4));
        Assert.Throws<InvalidDataException>(() => WorldLoadBytes.Decode(new byte[] { 0xff }));
        Assert.Throws<InvalidDataException>(() => WorldLoadBytes.Decode(Compress(new byte[] { 0xff })));
    }
    [Fact]
    public void NativeInputVerificationUsesTheCapturedBytesAndReturnsAnIndependentCopy()
    {
        var inspection = new WorldJsonInspection(typeof(JsonObject).Assembly);
        var bytes = Encoding.UTF8.GetBytes("{\"owned\":true}");
        var root = new JsonObject { Text = Encoding.UTF8.GetString(bytes) };
        var verified = inspection.VerifyInput(bytes, root);
        Assert.Equal(bytes, verified); Assert.NotSame(bytes, verified);
        bytes[0] = 0;
        Assert.Equal((byte)'{', verified[0]);
        Assert.Throws<InvalidDataException>(() => inspection.VerifyInput(Encoding.UTF8.GetBytes("different"), root));
    }
}
