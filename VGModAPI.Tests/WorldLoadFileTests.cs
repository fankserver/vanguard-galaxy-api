using System;
using System.IO;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldLoadFileTests
{
    [Fact]
    public void ReturnedBytesAreTheObservedReadAndChangedSourceIsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "vg-world-input-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = new byte[] { 1, 2, 3 }; File.WriteAllBytes(path, original);
            string hash = GenerationStore.Hash(original);
            var captured = WorldLoadFile.Capture(path, hash);
            File.WriteAllBytes(path, new byte[] { 4, 5, 6 });
            Assert.Equal(original, captured);
            Assert.Throws<InvalidDataException>(() => WorldLoadFile.Capture(path, hash));
            File.WriteAllBytes(path, Array.Empty<byte>());
            Assert.Throws<InvalidDataException>(() => WorldLoadFile.Capture(path, GenerationStore.Hash(Array.Empty<byte>())));
            Assert.Throws<InvalidDataException>(() => WorldLoadFile.Capture("relative.save", hash));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
