using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class DungeonOperationRecoveryLedger
{
    private Dictionary<Guid, DungeonOperationResumeState> _operations = new();
    internal DungeonOperationResumeState? Get(Guid id) => _operations.TryGetValue(id, out var state) ? state : null;
    internal void Restore(byte[]? payload) => _operations = (payload == null ? Array.Empty<DungeonOperationResumeState>() : DungeonOperationResumeCodec.Decode(payload)).ToDictionary(state => state.Id);
    internal byte[] Capture() => DungeonOperationResumeCodec.Encode(_operations.Values);
    internal void Track(DungeonOperationResumeState next)
    {
        if (_operations.TryGetValue(next.Id, out var previous))
        {
            if (previous.LocationId != next.LocationId || previous.ContentOccurrence != next.ContentOccurrence || previous.AttackerShipId != next.AttackerShipId || previous.DungeonType != next.DungeonType || previous.Autonomous != next.Autonomous)
                throw new InvalidOperationException("Persistent operation identity cannot change.");
            var advance = (int)next.TerminalProgress - (int)previous.TerminalProgress;
            if (advance < 0 || advance > 1) throw new InvalidOperationException("Invalid terminal progress transition.");
            if (previous.TerminalProgress != DungeonTerminalProgress.NotStarted && (previous.MissionProtection != next.MissionProtection || previous.Outcome != next.Outcome))
                throw new InvalidOperationException("Attempted terminal effects retain their outcome and mission protection.");
        }
        else if (next.TerminalProgress != DungeonTerminalProgress.NotStarted) throw new InvalidOperationException("New operation cannot assert prior terminal delivery.");
        DungeonOperationResumeCodec.Encode(_operations.Values.Where(state => state.Id != next.Id).Concat(new[] { next }));
        _operations[next.Id] = next;
    }
}
