using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core;

/// <summary>Freezes world metadata before serialization and binds it to the exact native snapshot, not live save-time state.</summary>
internal sealed class WorldSerializationAssociation
{
    internal const int MaxMetadataBytes = 256 * 1024;
    internal const int MaxObjects = 1024;
    internal sealed class Capture
    {
        internal readonly WorldSerializationAssociation Owner;
        internal readonly long Epoch, Revision;
        internal readonly byte[] Metadata;
        internal readonly object[] Objects;
        internal Capture(WorldSerializationAssociation owner, long epoch, long revision, byte[] metadata, object[] objects)
        { Owner = owner; Epoch = epoch; Revision = revision; Metadata = metadata; Objects = objects; }
    }
    private sealed class Binding
    {
        internal readonly byte[] Metadata;
        internal readonly string Digest;
        internal Binding(byte[] metadata, string digest) { Metadata = metadata; Digest = digest; }
    }
    private long _epoch;
    private ConditionalWeakTable<object, Binding> _bindings = new();

    internal Capture Begin(long revision, byte[] metadata, IReadOnlyList<object> objects)
    {
        long epoch = _epoch;
        if (metadata == null || metadata.Length == 0 || metadata.Length > MaxMetadataBytes)
            throw new InvalidDataException("Invalid world metadata size.");
        if (objects == null || objects.Count > MaxObjects) throw new InvalidDataException("Invalid world object count.");
        var copy = new object[objects.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = objects[i] ?? throw new InvalidDataException("Null world object.");
        if (epoch != _epoch) throw new InvalidDataException("World session changed during capture.");
        return new Capture(this, epoch, revision, (byte[])metadata.Clone(), copy);
    }

    internal bool Complete(Capture capture, long revision, IReadOnlyList<object> objects, object nativeSnapshot, string digest)
    {
        if (capture == null) throw new ArgumentNullException(nameof(capture));
        if (nativeSnapshot == null) throw new ArgumentNullException(nameof(nativeSnapshot));
        if (!ReferenceEquals(capture.Owner, this) || capture.Epoch != _epoch) return false;
        // A failed recapture of a reused JSON object must revoke its previous association.
        _bindings.Remove(nativeSnapshot);
        if (capture.Revision != revision || objects == null || objects.Count != capture.Objects.Length || !IsDigest(digest)) return false;
        for (int i = 0; i < objects.Count; i++)
            if (!ReferenceEquals(objects[i], capture.Objects[i])) return false;
        if (capture.Epoch != _epoch) return false;
        _bindings.Add(nativeSnapshot, new Binding((byte[])capture.Metadata.Clone(), digest));
        return true;
    }

    internal byte[] ForStore(object nativeSnapshot, string currentDigest)
    {
        if (nativeSnapshot == null || !_bindings.TryGetValue(nativeSnapshot, out var binding) ||
            !string.Equals(binding.Digest, currentDigest, StringComparison.Ordinal))
            throw new InvalidDataException("World metadata is not associated with this exact native snapshot.");
        return (byte[])binding.Metadata.Clone();
    }

    internal void Reset()
    {
        _epoch = checked(_epoch + 1);
        _bindings = new ConditionalWeakTable<object, Binding>();
    }

    private static bool IsDigest(string? value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
        return true;
    }
}
