using System;
using System.IO;

namespace VGModAPI.Core;

/// <summary>Read-only early-load metadata lookup. The adapter must separately bind these native bytes to the parsed load input.</summary>
internal sealed class WorldGenerationReader
{
    internal sealed class Result
    {
        internal SnapshotAssociation Association { get; }
        private readonly WorldSavedObject[] _rows;
        internal WorldSavedObject[] Rows => (WorldSavedObject[])_rows.Clone();
        internal Result(SnapshotAssociation association, WorldSavedObject[] rows)
        { Association = association; _rows = (WorldSavedObject[])rows.Clone(); }
    }
    private readonly GenerationStore _store;
    private readonly OwnerSchemaCodec _codec = new(WorldStateCodec.Owner, WorldStateCodec.SchemaVersion, Validate);
    internal WorldGenerationReader(GenerationStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    internal Result Read(string canonicalPath, byte[] nativeBytes) =>
        ReadCore(canonicalPath, nativeBytes, true)!;

    internal Result? ReadOptional(string canonicalPath, byte[] nativeBytes) =>
        ReadCore(canonicalPath, nativeBytes, false);

    private Result? ReadCore(string canonicalPath, byte[] nativeBytes, bool required)
    {
        if (string.IsNullOrWhiteSpace(canonicalPath) || !Path.IsPathRooted(canonicalPath) || Path.GetFullPath(canonicalPath) != canonicalPath)
            throw new InvalidDataException("Canonical absolute load path required.");
        if (nativeBytes == null || nativeBytes.Length == 0) throw new InvalidDataException("Native load bytes required.");
        string hash = GenerationStore.Hash(nativeBytes);
        var generation = _store.Load(canonicalPath, hash);
        if (generation == null)
        {
            if (!required) return null;
            throw new InvalidDataException("Owned world content has no committed generation.");
        }
        if (!generation.Identity.Matches(canonicalPath, hash)) throw new InvalidDataException("World generation association mismatch.");
        if (!generation.Owners.TryGetValue(WorldStateCodec.Owner, out var envelope))
        {
            if (!required) return null;
            throw new InvalidDataException("Owned world metadata is missing from the committed generation.");
        }
        var decoded = _codec.Decode(envelope);
        if (decoded.Status != SchemaReadStatus.Ready || decoded.Payload is not { } payload)
            throw new InvalidDataException("World metadata is protected: " + decoded.Status);
        return new Result(generation.Identity, WorldStateCodec.Decode(payload));
    }

    private static bool Validate(byte[] payload)
    {
        try { WorldStateCodec.Decode(payload); return true; }
        catch (InvalidDataException) { return false; }
    }
}
