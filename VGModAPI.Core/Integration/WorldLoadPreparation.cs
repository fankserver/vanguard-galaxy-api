using System;
using System.IO;

namespace VGModAPI.Core.Integration;

/// <summary>Combines inspected input and committed metadata before native construction. Hooks must supply and revalidate the observed starting attempt.</summary>
internal sealed class WorldLoadPreparation
{
    private readonly WorldGenerationReader _generations;
    private readonly WorldJsonInspection _json;
    private readonly WorldConstructionGate _gate;
    internal WorldLoadPreparation(WorldGenerationReader generations, WorldJsonInspection json, WorldConstructionGate gate)
    {
        _generations = generations ?? throw new ArgumentNullException(nameof(generations));
        _json = json ?? throw new ArgumentNullException(nameof(json));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    internal object Read(Guid session, string canonicalPath, string expectedHash, Func<bool> stillStarting,
        Func<WorldSavedObject, bool> definitionAvailable, Func<long> providerRevision)
    {
        if (stillStarting == null || definitionAvailable == null || providerRevision == null) throw new ArgumentNullException("World load verification callbacks are required.");
        if (!stillStarting()) throw new InvalidDataException("World load attempt is no longer starting.");
        long revision = providerRevision();
        var bytes = WorldLoadFile.Capture(canonicalPath, expectedHash);
        // This parsed object is the returned load input. The native Recall body must not reread the file.
        var root = _json.ParseCaptured(bytes);
        string rootText = root.ToString() ?? throw new InvalidDataException("Missing native root representation.");
        var nodes = _json.Read(root);
        var generation = nodes.Length == 0 ? _generations.ReadOptional(canonicalPath, bytes) : _generations.Read(canonicalPath, bytes);
        if (generation == null)
        {
            if (!stillStarting() || providerRevision() != revision || !stillStarting()) throw new InvalidDataException("World load changed during inspection.");
            RequireUnchangedRoot();
            return root;
        }
        var rows = generation.Rows;
        var bindings = WorldJsonInspection.Bind(rows, nodes);
        var providers = new string[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            if (!definitionAvailable(rows[i])) throw new InvalidDataException("Required world definition/provider is unavailable.");
            providers[i] = rows[i].Identity.Owner;
        }
        if (!stillStarting() || providerRevision() != revision || !stillStarting()) throw new InvalidDataException("World load changed during definition admission.");
        // Reinspect after callbacks: they must not change the native input after the generation comparison.
        var current = WorldJsonInspection.Bind(rows, _json.Read(root));
        for (int i = 0; i < bindings.Length; i++)
            if (!ReferenceEquals(bindings[i].Json, current[i].Json)) throw new InvalidDataException("World JSON node replaced during admission.");
        RequireUnchangedRoot();
        _gate.Open(session, generation.Association, canonicalPath, expectedHash, revision, providers, current);
        return root;

        void RequireUnchangedRoot()
        {
            if (!string.Equals(rootText, root.ToString(), StringComparison.Ordinal))
                throw new InvalidDataException("Native load root changed after its byte association.");
        }
    }
}
