using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>Attributes observed native roster and persisted overflow effects to an exact return invocation.</summary>
internal sealed class DungeonReturnReceiptCollector
{
    private readonly List<Scope> _scopes = new();
    internal Scope Begin(object? recipient, object? origin)
    { var scope = new Scope(this, recipient, origin); _scopes.Add(scope); return scope; }
    internal void CrewAdded(object recipient, string crew, int requested, int overflow)
    {
        if (_scopes.Count == 0 || requested < 0 || overflow < 0 || overflow > requested) return;
        var scope = _scopes[_scopes.Count - 1];
        if (scope.Recipient == null || !ReferenceEquals(scope.Recipient, recipient)) return;
        Add(scope.Accepted, crew, requested - overflow);
    }
    internal void OverflowPersisted(object origin, string crew, int count, object persistedData)
    {
        if (_scopes.Count == 0 || count <= 0) return;
        var scope = _scopes[_scopes.Count - 1];
        if (scope.Recipient == null || scope.Origin == null || !ReferenceEquals(scope.Origin, origin) || !scope.Persistables.Add(persistedData)) return;
        Add(scope.Overflow, crew, count);
    }
    private static void Add(Dictionary<string, int> target, string crew, int count)
    {
        if (count == 0) return;
        if (string.IsNullOrWhiteSpace(crew) || count < 0 || count > 10000) throw new InvalidOperationException("Invalid observed return count.");
        target.TryGetValue(crew, out var previous); var next = checked(previous + count);
        if (next > 10000 || (!target.ContainsKey(crew) && target.Count >= 64)) throw new InvalidOperationException("Return receipt exceeds bounds.");
        target[crew] = next;
    }
    internal sealed class Scope : IDisposable
    {
        private readonly DungeonReturnReceiptCollector _owner;
        internal readonly object? Recipient, Origin;
        internal readonly Dictionary<string, int> Accepted = new(StringComparer.Ordinal), Overflow = new(StringComparer.Ordinal);
        internal readonly HashSet<object> Persistables = new(ReferenceComparer.Instance);
        internal DungeonPodDeliveryReceipt Receipt => new(Accepted, Overflow);
        internal Scope(DungeonReturnReceiptCollector owner, object? recipient, object? origin) { _owner = owner; Recipient = recipient; Origin = origin; }
        public void Dispose() => _owner._scopes.Remove(this);
    }
    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();
        public new bool Equals(object? first, object? second) => ReferenceEquals(first, second);
        public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
}
