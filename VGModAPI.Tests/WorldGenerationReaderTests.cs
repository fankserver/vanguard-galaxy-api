using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldGenerationReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vg-world-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _native = { 1, 2, 3, 4 };
    private string Slot => Path.Combine(_root, "native.save");
    private GenerationStore Store => new(Path.Combine(_root, "generations"));
    private static byte[] Envelope(int version = 1) => new OwnerSchemaCodec(WorldStateCodec.Owner, version, _ => true)
        .Encode(WorldStateCodec.Encode(Array.Empty<WorldSavedObject>()));

    [Fact]
    public void ReadsExactCommittedGenerationWithoutWritingOrPublishing()
    {
        var store = Store;
        var published = store.Publish(Slot, GenerationStore.Hash(_native), Guid.NewGuid(), new Dictionary<string, byte[]> { [WorldStateCodec.Owner] = Envelope() });
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, path => GenerationStore.Hash(File.ReadAllBytes(path)));
        var result = new WorldGenerationReader(store).Read(Slot, _native);
        Assert.Equal(published.Identity.Snapshot, result.Association.Snapshot);
        Assert.Equal(published.Identity.StateHash, result.Association.StateHash);
        Assert.Empty(result.Rows);
        var after = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, path => GenerationStore.Hash(File.ReadAllBytes(path)));
        Assert.Equal(before.OrderBy(pair => pair.Key), after.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void UncommittedChangedOrCrossSlotBytesAreNotFreshEmptyWorlds()
    {
        var store = Store; var reader = new WorldGenerationReader(store);
        Assert.Throws<InvalidDataException>(() => reader.Read(Slot, _native));
        store.Publish(Slot, GenerationStore.Hash(_native), Guid.NewGuid(), new Dictionary<string, byte[]> { [WorldStateCodec.Owner] = Envelope() });
        Assert.Throws<InvalidDataException>(() => reader.Read(Slot, new byte[] { 9 }));
        Assert.Throws<InvalidDataException>(() => reader.Read(Path.Combine(_root, "other.save"), _native));
        Assert.Throws<InvalidDataException>(() => reader.Read("relative.save", _native));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrNewerOwnerEnvelopeRefusesConstruction(bool newer)
    {
        var store = Store;
        var owners = new Dictionary<string, byte[]>();
        if (newer) owners.Add(WorldStateCodec.Owner, Envelope(2));
        store.Publish(Slot, GenerationStore.Hash(_native), Guid.NewGuid(), owners);
        Assert.Throws<InvalidDataException>(() => new WorldGenerationReader(store).Read(Slot, _native));
    }

    [Fact]
    public void CorruptPublishedMetadataDoesNotFallBackToEmpty()
    {
        var store = Store;
        store.Publish(Slot, GenerationStore.Hash(_native), Guid.NewGuid(), new Dictionary<string, byte[]> { [WorldStateCodec.Owner] = Envelope() });
        var ownerFile = Directory.GetFiles(_root, "*.vgo", SearchOption.AllDirectories).Single(path => Path.GetFileName(path) != "manifest.vgo");
        File.WriteAllBytes(ownerFile, new byte[] { 0 });
        Assert.Throws<InvalidDataException>(() => new WorldGenerationReader(store).Read(Slot, _native));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
