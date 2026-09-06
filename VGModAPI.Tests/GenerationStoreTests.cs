using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class GenerationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vg-generation-" + Guid.NewGuid().ToString("N"));
    private static string H(char c) => new string(c, 64);
    private static Dictionary<string, byte[]> Owners(byte value) => new() { ["test.owner"] = new OwnerSchemaCodec("test.owner", 1, _ => true).Encode(new[] { value }) };
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void RotationSaveAsAndRollbackKeepIndependentGenerations()
    {
        var store = new GenerationStore(_root);
        var campaign = Guid.NewGuid();
        var old = store.Publish("slot-a", H('a'), campaign, Owners(1));
        store.Publish("slot-a", H('b'), campaign, Owners(2));
        store.Publish("slot-b", H('b'), campaign, Owners(2));
        Assert.Equal(old.Identity.Snapshot, store.Load("slot-a", H('a'))!.Identity.Snapshot);
        Assert.Throws<InvalidDataException>(() => store.Load("slot-b", H('a')));
        Assert.Equal(campaign, store.Load("slot-b", H('b'))!.Identity.Campaign);
    }

    [Fact]
    public void RetryIsIdempotentButConflictingProgressCannotOverwrite()
    {
        var store = new GenerationStore(_root);
        var campaign = Guid.NewGuid();
        var first = store.Publish("slot", H('a'), campaign, Owners(1));
        Assert.Equal(first.Identity.Snapshot, store.Publish("slot", H('a'), campaign, Owners(1)).Identity.Snapshot);
        Assert.Throws<InvalidDataException>(() => store.Publish("slot", H('a'), campaign, Owners(2)));
        Assert.Throws<InvalidDataException>(() => store.Publish("slot", H('a'), Guid.NewGuid(), Owners(1)));
        Assert.Equal(first.Identity.Snapshot, store.Load("slot", H('a'))!.Identity.Snapshot);
    }

    [Theory]
    [InlineData((int)PublishBoundary.FilesStaged, false)]
    [InlineData((int)PublishBoundary.BeforePublish, false)]
    [InlineData((int)PublishBoundary.Published, true)]
    public void InterruptedPublicationIsAbsentOrComplete(int boundary, bool published)
    {
        var campaign = Guid.NewGuid();
        var failing = new GenerationStore(_root, point => { if (point == (PublishBoundary)boundary) throw new IOException("simulated interruption"); });
        Assert.Throws<IOException>(() => failing.Publish("slot", H('a'), campaign, Owners(1)));
        var recovered = new GenerationStore(_root);
        if (published) Assert.NotNull(recovered.Load("slot", H('a')));
        else Assert.Throws<InvalidDataException>(() => recovered.Load("slot", H('a')));
        if (!published) Assert.NotEmpty(Directory.GetDirectories(_root, ".stage-*", SearchOption.AllDirectories));
        Assert.NotNull(recovered.Publish("slot", H('a'), campaign, Owners(1)));
    }

    [Fact]
    public void CorruptPublishedOwnerIsProtectedNotMissingOrOverwritten()
    {
        var store = new GenerationStore(_root);
        var campaign = Guid.NewGuid();
        store.Publish("slot", H('a'), campaign, Owners(1));
        var path = Directory.GetFiles(_root, "*.vgo", SearchOption.AllDirectories).Single(p => Path.GetFileName(p) != "manifest.vgo");
        File.WriteAllBytes(path, new byte[] { 9 });
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
        Assert.Throws<InvalidDataException>(() => store.Publish("slot", H('a'), campaign, Owners(2)));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void FutureManifestAndExtraFilesAreProtected()
    {
        var store = new GenerationStore(_root);
        store.Publish("slot", H('a'), Guid.NewGuid(), Owners(1));
        var manifest = Directory.GetFiles(_root, "manifest.vgo", SearchOption.AllDirectories).Single();
        var original = File.ReadAllBytes(manifest);
        var future = (byte[])original.Clone(); future[4] = 2; File.WriteAllBytes(manifest, future);
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
        File.WriteAllBytes(manifest, original);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "unexpected"), "retain");
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
    }

    [Fact]
    public void ValidDigestDoesNotBypassManifestStringBounds()
    {
        var store = new GenerationStore(_root);
        store.Publish("slot", H('a'), Guid.NewGuid(), Owners(1));
        var path = Directory.GetFiles(_root, "manifest.vgo", SearchOption.AllDirectories).Single();
        var codec = new OwnerSchemaCodec("vgmodapi.manifest", 1, _ => true);
        var payload = codec.Decode(File.ReadAllBytes(path)).Payload!;
        payload[0] = 255;
        File.WriteAllBytes(path, codec.Encode(payload));
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
    }

    [Fact]
    public void DeletedOwnerAndTruncatedManifestAreInvalidNotAbsent()
    {
        var store = new GenerationStore(_root);
        store.Publish("slot", H('a'), Guid.NewGuid(), Owners(1));
        var owner = Directory.GetFiles(_root, "*.vgo", SearchOption.AllDirectories).Single(p => Path.GetFileName(p) != "manifest.vgo");
        File.Delete(owner);
        var missing = Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
        Assert.IsType<FileNotFoundException>(missing.InnerException);
        var manifest = Directory.GetFiles(_root, "manifest.vgo", SearchOption.AllDirectories).Single();
        var codec = new OwnerSchemaCodec("vgmodapi.manifest", 1, _ => true);
        File.WriteAllBytes(manifest, codec.Encode(new byte[] { 64 }));
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
    }

    [Fact]
    public void CopiedManifestCannotCrossSlotIdentity()
    {
        var store = new GenerationStore(_root);
        var first = store.Publish("first", H('a'), Guid.NewGuid(), Owners(1));
        store.Publish("second", H('a'), first.Identity.Campaign, Owners(1));
        var firstPath = Path.Combine(_root, GenerationStore.Hash(System.Text.Encoding.UTF8.GetBytes("first")).Substring(0, 32), H('a'), "manifest.vgo");
        var secondPath = Path.Combine(_root, GenerationStore.Hash(System.Text.Encoding.UTF8.GetBytes("second")).Substring(0, 32), H('a'), "manifest.vgo");
        File.Copy(firstPath, secondPath, true);
        Assert.Throws<InvalidDataException>(() => store.Load("second", H('a')));
    }

    [Fact]
    public void PublishBoundsAndRegularFileTargetAreRefused()
    {
        var store = new GenerationStore(_root);
        var campaign = Guid.NewGuid();
        var many = Enumerable.Range(0, 33).ToDictionary(i => "owner" + i, _ => new byte[] { 1 });
        Assert.Throws<InvalidDataException>(() => store.Publish("slot", H('a'), campaign, many));
        Assert.Throws<InvalidDataException>(() => store.Publish("slot", H('a'), campaign,
            new Dictionary<string, byte[]> { ["owner"] = new byte[GenerationStore.MaxOwnerBytes + 1] }));
        Assert.Throws<InvalidDataException>(() => store.Publish("slot", H('a'), campaign,
            new Dictionary<string, byte[]> { ["owner"] = null! }));
        Assert.Throws<ArgumentException>(() => store.Publish("slot", H('a'), campaign,
            new Dictionary<string, byte[]> { ["../owner"] = new byte[] { 1 } }));
        Assert.Throws<ArgumentException>(() => store.Publish("slot", "bad", campaign, Owners(1)));
        var parent = Path.Combine(_root, GenerationStore.Hash(System.Text.Encoding.UTF8.GetBytes("slot")).Substring(0, 32));
        Directory.CreateDirectory(parent); File.WriteAllText(Path.Combine(parent, H('a')), "retain");
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
    }

    [Fact]
    public void ReturnedOwnersCannotMutateStoredGeneration()
    {
        var store = new GenerationStore(_root);
        var data = Owners(1);
        var result = store.Publish("slot", H('a'), Guid.NewGuid(), data);
        data["test.owner"][0] = 0;
        result.Owners["test.owner"][0] = 0;
        Assert.Equal((byte)'V', store.Load("slot", H('a'))!.Owners["test.owner"][0]);
    }

    [Fact]
    public void PortablePathBudgetIsEnforcedBeforeCreatingDirectories()
    {
        var tooLong = Path.Combine(_root, new string('x', 100));
        Assert.Throws<ArgumentException>(() => new GenerationStore(tooLong));
        Assert.False(Directory.Exists(tooLong));
        var store = new GenerationStore(_root);
        store.Publish("slot", H('a'), Guid.NewGuid(), Owners(1));
        Assert.All(Directory.GetFiles(_root, "*", SearchOption.AllDirectories), path => Assert.True(path.Length <= 259));
    }

    [Fact]
    public void LinkedRootIsRefusedWithoutWritingIntoTarget()
    {
        Directory.CreateDirectory(_root);
        var real = Path.Combine(_root, "real"); Directory.CreateDirectory(real);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, real);
        try
        {
            Assert.Throws<IOException>(() => new GenerationStore(link));
            Assert.Empty(Directory.GetFileSystemEntries(real));
        }
        finally { Directory.Delete(link); }
    }
}
