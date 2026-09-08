using System;

namespace VGModAPI.Core;

internal sealed class DungeonMutationFence
{
    private object _generation = new();
    private int _depth;
    internal bool Uncertain { get; private set; }
    internal bool Busy => _depth != 0;
    internal void Fail() => Uncertain = true;
    internal void Reset() { _generation = new(); _depth = 0; Uncertain = false; }
    internal Lease Enter()
    {
        if (Uncertain) throw new InvalidOperationException("Dungeon effects require a reload after an uncertain mutation.");
        _depth++; return new(this, _generation);
    }
    internal void EnsureSettled()
    { if (Busy || Uncertain) throw new InvalidOperationException("Cannot save incomplete or uncertain dungeon effects."); }
    internal sealed class Lease : IDisposable
    {
        private readonly DungeonMutationFence _owner; private readonly object _generation; private bool _disposed;
        internal Lease(DungeonMutationFence owner, object generation) { _owner = owner; _generation = generation; }
        internal void Failed() { if (!_disposed && ReferenceEquals(_generation, _owner._generation)) _owner.Uncertain = true; }
        public void Dispose() { if (_disposed) return; _disposed = true; if (ReferenceEquals(_generation, _owner._generation)) _owner._depth--; }
    }
}
