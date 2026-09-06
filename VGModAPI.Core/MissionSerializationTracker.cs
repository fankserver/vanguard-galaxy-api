using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core;

/// <summary>Associates identity bytes with a particular vanilla Player JSON object, never the latest live state.</summary>
internal sealed class MissionSerializationTracker
{
    internal sealed class Capture
    {
        internal readonly long Epoch, Revision;
        internal readonly object[] Missions;
        internal readonly Guid[] Ids;
        internal Capture(long epoch, long revision, object[] missions, Guid[] ids)
        { Epoch = epoch; Revision = revision; Missions = missions; Ids = ids; }
    }
    private sealed class Payload
    {
        internal readonly byte[] Bytes;
        internal readonly string[] Fingerprints;
        internal Payload(byte[] bytes, string[] fingerprints) { Bytes = bytes; Fingerprints = fingerprints; }
    }
    internal sealed class StoreScope : IDisposable
    {
        private readonly MissionSerializationTracker _owner;
        internal readonly long Epoch;
        internal readonly byte[]? Bytes;
        internal bool Closed;
        internal StoreScope(MissionSerializationTracker owner, long epoch, byte[]? bytes)
        { _owner = owner; Epoch = epoch; Bytes = bytes; }
        public void Dispose() => _owner.EndStore(this);
    }
    private ConditionalWeakTable<object, Payload> _snapshots = new();
    private readonly Stack<StoreScope> _stores = new();
    private long _epoch;
    internal void Reset()
    { _epoch++; _snapshots = new(); _stores.Clear(); }
    internal Capture Begin(IEnumerable<object> missions, Func<object, Guid> identity, long revision)
    {
        var values = missions.Take(MissionIdentitySnapshot.MaxEntries + 1).ToArray();
        if (values.Length > MissionIdentitySnapshot.MaxEntries || values.Any(m => m == null)) throw new InvalidDataException("Invalid mission snapshot membership.");
        return new Capture(_epoch, revision, values, values.Select(identity).ToArray());
    }
    internal bool Complete(Capture capture, object playerJson, IReadOnlyList<object> current, IReadOnlyList<string> serializedFingerprints, long revision)
    {
        if (capture.Epoch != _epoch) return false;
        // A reused JSON object must not retain an earlier, now-unverified association.
        _snapshots.Remove(playerJson);
        if (capture.Revision != revision || current.Count != capture.Missions.Length || serializedFingerprints.Count != current.Count ||
            !current.Zip(capture.Missions, ReferenceEquals).All(equal => equal)) return false;
        var rows = serializedFingerprints.Select((fingerprint, index) => new MissionIdentityRecord(fingerprint, capture.Ids[index]));
        // Ambiguous occurrences get session-local IDs after reload; never persist that churn against identical vanilla bytes.
        var unique = rows.GroupBy(row => row.Fingerprint, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Single());
        _snapshots.Add(playerJson, new Payload(MissionIdentitySnapshot.Encode(unique), serializedFingerprints.ToArray())); return true;
    }
    internal StoreScope BeginStore(object? playerJson, Func<string[]>? readFingerprints = null)
    {
        Payload? payload = null;
        if (playerJson != null) _snapshots.TryGetValue(playerJson, out payload);
        byte[]? bytes = payload?.Bytes;
        if (payload != null && readFingerprints != null && !payload.Fingerprints.OrderBy(f => f, StringComparer.Ordinal)
                .SequenceEqual(readFingerprints().OrderBy(f => f, StringComparer.Ordinal), StringComparer.Ordinal)) bytes = null;
        var scope = new StoreScope(this, _epoch, bytes); _stores.Push(scope); return scope;
    }
    internal byte[] CaptureForStore()
    {
        if (_stores.Count == 0 || _stores.Peek().Bytes is not { } bytes) throw new InvalidDataException("No verified mission identity association for this vanilla snapshot.");
        return (byte[])bytes.Clone();
    }
    private void EndStore(StoreScope scope)
    {
        if (scope.Closed) return;
        if (scope.Epoch != _epoch || !_stores.Contains(scope)) { scope.Closed = true; return; }
        while (_stores.Count > 0)
        {
            var closed = _stores.Pop(); closed.Closed = true;
            if (ReferenceEquals(closed, scope)) break;
        }
    }
}
