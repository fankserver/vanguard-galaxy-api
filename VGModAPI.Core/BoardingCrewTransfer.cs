using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace VGModAPI.Core;

/// <summary>Batch debit without intermediate native crew-change notifications. Never refund a possibly dispatched transport.</summary>
internal static class BoardingCrewTransfer
{
    internal static BoardingCommandStatus Validate(IDictionary<string, int> available, BoardingCrewManifest manifest,
        Func<string, bool> allowed, int capacity)
    {
        if (manifest.Count > capacity) return BoardingCommandStatus.CapacityExceeded;
        foreach (var pair in manifest.Crew)
        {
            if (!allowed(pair.Key)) return BoardingCommandStatus.InvalidCrew;
            if (!available.TryGetValue(pair.Key, out var count) || count < pair.Value) return BoardingCommandStatus.InsufficientCrew;
        }
        return BoardingCommandStatus.Admitted;
    }
    internal static BoardingCommandStatus Transfer(IDictionary<string, int> available, BoardingCrewManifest manifest,
        Func<string, bool> allowed, int capacity, Action<Dictionary<string, int>> transport, Action notify)
    {
        var status = Validate(available, manifest, allowed, capacity);
        if (status != BoardingCommandStatus.Admitted) return status;
        var debit = new Dictionary<string, int>(manifest.Crew, StringComparer.Ordinal);
        var before = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in debit) before.Add(pair.Key, available[pair.Key]);
        try
        {
            // The inspected native roster is a Dictionary; setters perform no callbacks.
            foreach (var pair in debit) available[pair.Key] = before[pair.Key] - pair.Value;
        }
        catch
        {
            foreach (var pair in before) available[pair.Key] = pair.Value;
            throw;
        }
        Exception? failure = null;
        try { transport(debit); }
        catch (Exception error) { failure = error; }
        try { notify(); }
        catch (Exception notificationError)
        {
            if (failure != null) throw new AggregateException("Boarding transport and crew notification failed; crew may already be dispatched.", failure, notificationError);
            throw;
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return BoardingCommandStatus.Admitted;
    }
}
