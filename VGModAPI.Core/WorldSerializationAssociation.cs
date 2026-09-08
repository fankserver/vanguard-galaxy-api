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
    private sealed class CaptureData
    {
        internal readonly long Revision;
        internal readonly byte[] Metadata;
        internal readonly object[] Objects;
        internal CaptureData(long revision, byte[] metadata, object[] objects)
        { Revision = revision; Metadata = metadata; Objects = objects; }
    }
    private sealed class Binding
    {
        internal readonly byte[] Metadata;
        internal readonly string Digest;
        internal Binding(byte[] metadata, string digest) { Metadata = metadata; Digest = digest; }
    }
    private long _operation;
    private ConditionalWeakTable<object, CaptureData> _captures = new();
    private ConditionalWeakTable<object, Binding> _bindings = new();

    internal object Begin(long revision, byte[] metadata, IReadOnlyList<object> objects)
    {
        long operation = NextOperation();
        if (metadata == null || metadata.Length == 0 || metadata.Length > MaxMetadataBytes)
            throw new InvalidDataException("Invalid world metadata size.");
        var frozen = (byte[])metadata.Clone();
        if (objects == null) throw new InvalidDataException("Missing world objects.");
        int count = objects.Count;
        if (count < 0 || count > MaxObjects) throw new InvalidDataException("Invalid world object count.");
        var copy = new object[count];
        for (int i = 0; i < count; i++)
            copy[i] = objects[i] ?? throw new InvalidDataException("Null world object.");
        if (objects.Count != count || operation != _operation)
            throw new InvalidDataException("World capture changed during observation.");
        var token = new object();
        _captures.Add(token, new CaptureData(revision, frozen, copy));
        return token;
    }

    internal bool Complete(object token, long revision, IReadOnlyList<object> objects, object nativeSnapshot, string digest)
    {
        if (token == null) throw new ArgumentNullException(nameof(token));
        if (nativeSnapshot == null) throw new ArgumentNullException(nameof(nativeSnapshot));
        long operation = NextOperation();
        if (!_captures.TryGetValue(token, out var capture)) return false;
        _captures.Remove(token);
        // A failed recapture of a reused JSON object must revoke its previous association.
        _bindings.Remove(nativeSnapshot);
        int count = capture.Objects.Length;
        if (capture.Revision != revision || objects == null || objects.Count != count || !IsDigest(digest)) return false;
        for (int i = 0; i < count; i++)
            if (!ReferenceEquals(objects[i], capture.Objects[i])) return false;
        if (objects.Count != count || operation != _operation) return false;
        _bindings.Add(nativeSnapshot, new Binding(capture.Metadata, digest));
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
        NextOperation();
        _captures = new ConditionalWeakTable<object, CaptureData>();
        _bindings = new ConditionalWeakTable<object, Binding>();
    }

    private long NextOperation() => _operation = checked(_operation + 1);

    private static bool IsDigest(string? value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
        return true;
    }
}
