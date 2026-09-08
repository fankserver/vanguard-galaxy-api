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
            if (previous.WalkReturn is { } walk)
            {
                var restored = next.WalkReturn;
                if (restored == null || restored.Crew.Count != walk.Crew.Count || walk.Crew.Any(pair => !restored.Crew.TryGetValue(pair.Key, out var count) || count != pair.Value) ||
                    (int)restored.Progress < (int)walk.Progress || (int)restored.Progress > (int)walk.Progress + 1)
                    throw new InvalidOperationException("Walk return obligations cannot be rewritten or retried.");
            }
            else if (next.WalkReturn != null && next.WalkReturn.Progress != DungeonWalkReturnProgress.Pending)
                throw new InvalidOperationException("New walk obligations cannot assert delivery.");
            var advance = (int)next.TerminalProgress - (int)previous.TerminalProgress;
            if (advance < 0 || advance > 1) throw new InvalidOperationException("Invalid terminal progress transition.");
            if (previous.TerminalProgress != DungeonTerminalProgress.NotStarted && (previous.MissionProtection != next.MissionProtection || previous.Outcome != next.Outcome))
                throw new InvalidOperationException("Attempted terminal effects retain their outcome and mission protection.");
        }
        else if (next.TerminalProgress != DungeonTerminalProgress.NotStarted || (next.WalkReturn != null && next.WalkReturn.Progress != DungeonWalkReturnProgress.Pending)) throw new InvalidOperationException("New operation cannot assert prior terminal delivery.");
        DungeonOperationResumeCodec.Encode(_operations.Values.Where(state => state.Id != next.Id).Concat(new[] { next }));
        _operations[next.Id] = next;
    }
}
