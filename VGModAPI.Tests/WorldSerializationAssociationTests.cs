using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
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
        var recapture = tracker.Begin(1, new byte[] { 1 }, objects);
        Assert.False(tracker.Complete(recapture, 2, objects, json, Digest));
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

    private sealed class UnstableList : IReadOnlyList<object>
    {
        private readonly Func<int> _count;
        private readonly Func<int, object> _item;
        internal UnstableList(Func<int> count, Func<int, object> item) { _count = count; _item = item; }
        public int Count => _count();
        public object this[int index] => _item(index);
        public IEnumerator<object> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void ChangingCountCannotSkipMembershipValidation()
    {
        var tracker = new WorldSerializationAssociation();
        var capture = tracker.Begin(1, new byte[] { 1 }, new[] { new object() });
        int reads = 0, comparisons = 0;
        var unstable = new UnstableList(() => ++reads == 1 ? 1 : 0, _ => { comparisons++; return new object(); });
        Assert.False(tracker.Complete(capture, 1, unstable, new object(), Digest));
        Assert.Equal(1, comparisons);
    }

    [Fact]
    public void ReentrantResetCannotPublishCaptureOrBinding()
    {
        var tracker = new WorldSerializationAssociation();
        var item = new object();
        var unstable = new UnstableList(() => 1, _ => { tracker.Reset(); return item; });
        Assert.Throws<InvalidDataException>(() => tracker.Begin(1, new byte[] { 1 }, unstable));
        var capture = tracker.Begin(1, new byte[] { 1 }, new[] { item });
        var json = new object();
        Assert.False(tracker.Complete(capture, 1, unstable, json, Digest));
        Assert.Throws<InvalidDataException>(() => tracker.ForStore(json, Digest));
    }

    [Fact]
    public void OpaqueTokensCannotBeForgedOrReplayed()
    {
        var tracker = new WorldSerializationAssociation();
        var objects = new[] { new object() };
        var capture = tracker.Begin(1, new byte[] { 1 }, objects);
        Assert.Equal(typeof(object), capture.GetType());
        objects[0] = new object();
        Assert.False(tracker.Complete(capture, 1, objects, new object(), Digest));
        Assert.False(tracker.Complete(new object(), 1, objects, new object(), Digest));
        var valid = tracker.Begin(1, new byte[] { 2 }, objects);
        Assert.True(tracker.Complete(valid, 1, objects, new object(), Digest));
        Assert.False(tracker.Complete(valid, 1, objects, new object(), Digest));
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
