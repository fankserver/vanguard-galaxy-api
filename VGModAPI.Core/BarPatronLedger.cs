using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>Transactional storage; session authorization and persistence readiness belong to its service.</summary>
internal sealed class BarPatronLedger
{
    private Dictionary<BarPatronId, BarPatronState> _rows = new();
    internal IReadOnlyList<BarPatronState> Snapshot() => Array.AsReadOnly(_rows.Values
        .OrderBy(row => row.Id.Provider, StringComparer.Ordinal).ThenBy(row => row.Id.LocalId, StringComparer.Ordinal).ToArray());
    internal byte[] Capture() => BarPatronCodec.Encode(_rows.Values);
    internal void Reset() => _rows = new Dictionary<BarPatronId, BarPatronState>();

    internal void Restore(byte[] payload)
    {
        // Complete validation precedes replacement: malformed data cannot become fresh empty state.
        var decoded = BarPatronCodec.Decode(payload);
        _rows = decoded.ToDictionary(row => row.Id);
    }

    internal bool TryPut(string authenticatedProvider, BarPatronState state)
    {
        if (state == null || state.Id.Provider != authenticatedProvider) return false;
        var candidate = new Dictionary<BarPatronId, BarPatronState>(_rows) { [state.Id] = state };
        try { BarPatronCodec.Encode(candidate.Values); }
        catch (Exception error) when (error is InvalidDataException || error is ArgumentException || error is OverflowException) { return false; }
        _rows = candidate;
        return true;
    }

    internal bool TryRemove(string authenticatedProvider, BarPatronId id)
    {
        if (id.Provider != authenticatedProvider) return false;
        return _rows.Remove(id);
    }
}
