using System;
using System.IO;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldSerializationAssociationTests
{
    private static readonly string Digest = new('a', 64);

    [Fact]
    public void FreezesMetadataAndReturnsCopiesForTheExactSnapshot()
    {
        var tracker = new WorldSerializationAssociation();
        var metadata = new byte[] { 1, 2 };
        var objects = new[] { new object() };
        var capture = tracker.Begin(1, metadata, objects);
        metadata[0] = 9;
        var json = new object();
        Assert.True(tracker.Complete(capture, 1, objects, json, Digest));
        var first = tracker.ForStore(json, Digest);
        Assert.Equal(new byte[] { 1, 2 }, first);
        first[0] = 8;
        Assert.Equal(new byte[] { 1, 2 }, tracker.ForStore(json, Digest));
        Assert.Throws<InvalidDataException>(() => tracker.ForStore(new object(), Digest));
        Assert.Throws<InvalidDataException>(() => tracker.ForStore(json, new string('b', 64)));
    }

    [Fact]
    public void FailedRecaptureRevokesOldAssociation()
    {
        var tracker = new WorldSerializationAssociation();
        var objects = new[] { new object() };
        var capture = tracker.Begin(1, new byte[] { 1 }, objects);
        var json = new object();
        Assert.True(tracker.Complete(capture, 1, objects, json, Digest));
        Assert.False(tracker.Complete(capture, 2, objects, json, Digest));
        Assert.Throws<InvalidDataException>(() => tracker.ForStore(json, Digest));
    }

    [Fact]
    public void ReplacementAndReorderingCannotReuseMembershipEvidence()
    {
        var tracker = new WorldSerializationAssociation();
        var a = new object(); var b = new object();
        var capture = tracker.Begin(1, new byte[] { 1 }, new[] { a, b });
        Assert.False(tracker.Complete(capture, 1, new[] { b, a }, new object(), Digest));
        Assert.False(tracker.Complete(capture, 1, new[] { a, new object() }, new object(), Digest));
        Assert.False(tracker.Complete(capture, 1, new[] { a }, new object(), Digest));
        Assert.False(tracker.Complete(capture, 1, new[] { a, b }, new object(), "unverified"));
    }

    [Fact]
    public void SessionResetRejectsOldCapturesWithoutRevokingNewBindings()
    {
        var tracker = new WorldSerializationAssociation();
        var objects = Array.Empty<object>();
        var old = tracker.Begin(1, new byte[] { 1 }, objects);
        tracker.Reset();
        var current = tracker.Begin(1, new byte[] { 2 }, objects);
        var json = new object();
        Assert.True(tracker.Complete(current, 1, objects, json, Digest));
        Assert.False(tracker.Complete(old, 1, objects, json, Digest));
        Assert.Equal(new byte[] { 2 }, tracker.ForStore(json, Digest));
        tracker.Reset();
        Assert.Throws<InvalidDataException>(() => tracker.ForStore(json, Digest));
    }

    [Fact]
    public void ForeignCapturesAreNotAuthorityAndUnboundedMetadataIsRejected()
    {
        var tracker = new WorldSerializationAssociation();
        var other = new WorldSerializationAssociation();
        var capture = other.Begin(1, new byte[] { 1 }, Array.Empty<object>());
        Assert.False(tracker.Complete(capture, 1, Array.Empty<object>(), new object(), Digest));
        Assert.Throws<InvalidDataException>(() => tracker.Begin(1, Array.Empty<byte>(), Array.Empty<object>()));
        Assert.Throws<InvalidDataException>(() => tracker.Begin(1, new byte[WorldSerializationAssociation.MaxMetadataBytes + 1], Array.Empty<object>()));
    }
}
