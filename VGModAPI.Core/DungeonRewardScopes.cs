using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>Invocation-local reward identity; nested native work cannot borrow another loot entry's policy.</summary>
internal sealed class DungeonRewardScopes
{
    private readonly DungeonRewardService _rules;
    private readonly List<Scope> _stack = new();
    internal DungeonRewardScopes(DungeonRewardService rules) { _rules = rules; }
    internal IDisposable Begin(BoardingHandle operation, DungeonRewardKind kind, string outcome, bool missionToken, object? lootEntry = null)
    {
        var scope = new Scope(this, operation, kind, outcome, missionToken, lootEntry); _stack.Add(scope); return scope;
    }
    internal IDisposable Mask()
    {
        var scope = new Scope(this, null, default, "Unknown", true, null); _stack.Add(scope); return scope;
    }
    internal int LootAmount(object entry, int nativeAmount)
    {
        if (nativeAmount < 0 || _stack.Count == 0) return nativeAmount;
        var scope = _stack[_stack.Count - 1];
        if (scope.Operation == null || scope.Kind != DungeonRewardKind.LootAmount || !ReferenceEquals(entry, scope.LootEntry)) return nativeAmount;
        var amount = _rules.Apply(new(scope.Operation, scope.Kind, scope.Outcome, scope.MissionToken, nativeAmount));
        return amount <= int.MaxValue ? (int)Math.Floor(amount) : nativeAmount;
    }
    internal float Mastery(object recipient, float nativeAmount)
    {
        if (float.IsNaN(nativeAmount) || float.IsInfinity(nativeAmount) || nativeAmount < 0 || _stack.Count == 0) return nativeAmount;
        var scope = _stack[_stack.Count - 1];
        if (scope.Operation == null || scope.Kind != DungeonRewardKind.MasteryExperience || !ReferenceEquals(recipient, scope.LootEntry)) return nativeAmount;
        var amount = _rules.Apply(new(scope.Operation, scope.Kind, scope.Outcome, scope.MissionToken, nativeAmount));
        return amount <= float.MaxValue ? (float)amount : nativeAmount;
    }
    private sealed class Scope : IDisposable
    {
        private readonly DungeonRewardScopes _owner;
        internal readonly BoardingHandle? Operation;
        internal readonly DungeonRewardKind Kind;
        internal readonly string Outcome;
        internal readonly bool MissionToken;
        internal readonly object? LootEntry;
        internal Scope(DungeonRewardScopes owner, BoardingHandle? operation, DungeonRewardKind kind, string outcome, bool missionToken, object? lootEntry)
        { _owner = owner; Operation = operation; Kind = kind; Outcome = outcome; MissionToken = missionToken; LootEntry = lootEntry; }
        public void Dispose() => _owner._stack.Remove(this);
    }
}
