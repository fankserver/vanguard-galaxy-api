using System;
using System.IO;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>Read-only early-load metadata lookup. The adapter must separately bind these native bytes to the parsed load input.</summary>
internal sealed class WorldGenerationReader
{
    internal sealed class Result
    {
        internal SnapshotAssociation Association { get; }
        private readonly WorldSavedObject[] _rows;
        internal WorldSavedObject[] Rows => (WorldSavedObject[])_rows.Clone();
        private readonly Dictionary<(string, string), WorldSavedDefinition> _definitions = new();
        internal WorldSavedDefinition DefinitionFor(WorldSavedObject row) => _definitions[(row.Identity.Owner, row.Identity.LocalId)];
        private readonly byte[] _statePayload;
        private readonly byte[]? _definitionPayload;
        internal byte[]? PayloadFor(string owner) => owner == WorldStateCodec.Owner ? (byte[])_statePayload.Clone() :
            owner == WorldDefinitionCodec.Owner ? (_definitionPayload == null ? null : (byte[])_definitionPayload.Clone()) : throw new ArgumentException("Unknown world owner.", nameof(owner));
        internal Result(SnapshotAssociation association, WorldSavedObject[] rows, WorldSavedDefinition[] definitions, bool definitionsPresent)
        {
            Association = association; _rows = (WorldSavedObject[])rows.Clone();
            _statePayload = WorldStateCodec.Encode(rows);
            _definitionPayload = definitionsPresent ? WorldDefinitionCodec.Encode(definitions) : null;
            foreach (var definition in definitions) _definitions.Add((definition.Owner, definition.Definition.LocalId), definition);
            foreach (var row in rows)
                if (!_definitions.TryGetValue((row.Identity.Owner, row.Identity.LocalId), out var definition) || definition.Definition.Revision != row.DefinitionRevision)
                    throw new InvalidDataException("World instance has no matching retained declaration.");
        }
    }
    private readonly GenerationStore _store;
    private readonly OwnerSchemaCodec _codec = new(WorldStateCodec.Owner, WorldStateCodec.SchemaVersion, Validate);
    private readonly OwnerSchemaCodec _definitions = new(WorldDefinitionCodec.Owner, WorldDefinitionCodec.SchemaVersion, ValidateDefinitions);
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
            if (generation.Owners.ContainsKey(WorldDefinitionCodec.Owner))
                throw new InvalidDataException("Retained world declarations have no paired instance inventory.");
            if (!required) return null;
            throw new InvalidDataException("Owned world metadata is missing from the committed generation.");
        }
        var decoded = _codec.Decode(envelope);
        if (decoded.Status != SchemaReadStatus.Ready || decoded.Payload is not { } payload)
            throw new InvalidDataException("World metadata is protected: " + decoded.Status);
        var rows = WorldStateCodec.Decode(payload);
        var definitions = Array.Empty<WorldSavedDefinition>();
        if (generation.Owners.TryGetValue(WorldDefinitionCodec.Owner, out var definitionEnvelope))
        {
            var result = _definitions.Decode(definitionEnvelope);
            if (result.Status != SchemaReadStatus.Ready || result.Payload is not { } definitionPayload)
                throw new InvalidDataException("Retained world declarations are protected: " + result.Status);
            definitions = WorldDefinitionCodec.Decode(definitionPayload);
        }
        return new Result(generation.Identity, rows, definitions, generation.Owners.ContainsKey(WorldDefinitionCodec.Owner));
    }

    private static bool ValidateDefinitions(byte[] payload)
    {
        try { WorldDefinitionCodec.Decode(payload); return true; }
        catch (InvalidDataException) { return false; }
    }

    private static bool Validate(byte[] payload)
    {
        try { WorldStateCodec.Decode(payload); return true; }
        catch (InvalidDataException) { return false; }
    }
}
