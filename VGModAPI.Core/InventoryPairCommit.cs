using System;

namespace VGModAPI.Core;

internal enum InventoryCommitStatus { Committed, Unchanged, RecoveryRequired }

// The production writer assigns inspected Inventory.allItems fields, not virtual Add/Remove:
// no callbacks run between the two assignments. All allocation and validation precede publication.
internal sealed class InventoryPairCommit
{
    private readonly Func<object> _readSource, _readDestination;
    private readonly Action<object> _writeSource, _writeDestination;
    private readonly object _sourceBefore, _destinationBefore, _sourceAfter, _destinationAfter;
    private bool _started, _committed;
    internal InventoryPairCommit(Func<object> readSource, Action<object> writeSource,
        Func<object> readDestination, Action<object> writeDestination,
        object sourceBefore, object destinationBefore, object sourceAfter, object destinationAfter)
    {
        _readSource = readSource; _writeSource = writeSource;
        _readDestination = readDestination; _writeDestination = writeDestination;
        _sourceBefore = sourceBefore; _destinationBefore = destinationBefore;
        _sourceAfter = sourceAfter; _destinationAfter = destinationAfter;
        if (ReferenceEquals(sourceBefore, destinationBefore) || ReferenceEquals(sourceBefore, sourceAfter) ||
            ReferenceEquals(destinationBefore, destinationAfter) || ReferenceEquals(sourceAfter, destinationAfter))
            throw new ArgumentException("Distinct prepared inventory arrays required.");
    }
    internal InventoryCommitStatus Commit()
    {
        if (_committed) return InventoryCommitStatus.Committed;
        if (_started) return Recover();
        if (!ReferenceEquals(_readSource(), _sourceBefore) || !ReferenceEquals(_readDestination(), _destinationBefore))
            return InventoryCommitStatus.Unchanged;
        _started = true;
        try
        {
            _writeSource(_sourceAfter);
            if (!ReferenceEquals(_readSource(), _sourceAfter)) return Recover();
            _writeDestination(_destinationAfter);
            if (!ReferenceEquals(_readSource(), _sourceAfter) || !ReferenceEquals(_readDestination(), _destinationAfter)) return Recover();
            _committed = true;
            return InventoryCommitStatus.Committed;
        }
        catch { return Recover(); }
    }
    internal InventoryCommitStatus Recover()
    {
        if (_committed) return InventoryCommitStatus.Committed;
        // Never overwrite an unrelated replacement. Retain this transaction for explicit recovery.
        Restore(_readDestination, _writeDestination, _destinationBefore, _destinationAfter);
        Restore(_readSource, _writeSource, _sourceBefore, _sourceAfter);
        try
        {
            return ReferenceEquals(_readSource(), _sourceBefore) && ReferenceEquals(_readDestination(), _destinationBefore)
                ? InventoryCommitStatus.Unchanged : InventoryCommitStatus.RecoveryRequired;
        }
        catch { return InventoryCommitStatus.RecoveryRequired; }
    }
    private static void Restore(Func<object> read, Action<object> write, object before, object after)
    {
        try { if (ReferenceEquals(read(), after)) write(before); }
        catch { /* A writer can assign then throw. Final reads, not return values, determine recovery. */ }
    }
}
