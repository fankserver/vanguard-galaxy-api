using System;
using System.Collections;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Constructs without immediate crew debit or spawning fresh pods.</summary>
internal sealed class DungeonInitialOperationFactory
{
    private readonly ConstructorInfo _approach, _walkApproach, _activeShip, _activeLocation;
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly DungeonOperationOptionsAdapter _options;
    private readonly FieldInfo _operations;
    internal DungeonInitialOperationFactory(Assembly assembly, IBoardingTacticalNativeBindings native, DungeonOperationOptionsAdapter options)
    {
        _native = native; _options = options;
        var operation = assembly.GetType(BindingCatalog.BoardingOperation, true)!;
        var ship = assembly.GetType("Behaviour.Unit.SpaceShip", true)!; var boardable = assembly.GetType(BindingCatalog.Boardable, true)!;
        var location = assembly.GetType(BindingCatalog.BoardingLocation, true)!; var option = assembly.GetType(BindingCatalog.BoardingOptions, true)!;
        _approach = operation.GetConstructor(new[] { ship, boardable, option, typeof(bool), typeof(bool) }) ?? throw new MissingMethodException("Approach resume constructor unavailable.");
        _walkApproach = operation.GetConstructor(new[] { ship, location, option, typeof(bool) }) ?? throw new MissingMethodException("Walk approach constructor unavailable.");
        _activeShip = operation.GetConstructor(new[] { ship, boardable, typeof(bool) }) ?? throw new MissingMethodException("Ship resume constructor unavailable.");
        _activeLocation = operation.GetConstructor(new[] { ship, location, typeof(bool) }) ?? throw new MissingMethodException("Location resume constructor unavailable.");
        _operations = assembly.GetType(BindingCatalog.BoardingManager, true)!.GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (_operations == null || !NativeTypeName.Matches(_operations.FieldType, "System.Collections.Generic.List`1<Behaviour.Dungeon.DungeonOperation>")) throw new MissingMemberException("Manager operation registry unavailable.");
    }
    internal object Create(DungeonOperationResumeState saved, object recipient, object location, object? boardable, bool active)
    {
        if ((string?)_native.Get(_native.Get(recipient, "resumeShipData"), "resumeShipGuid") != saved.AttackerShipId || saved.TerminalProgress != DungeonTerminalProgress.NotStarted)
            throw new InvalidOperationException("Exact recipient and unprocessed terminal state required.");
        if (saved.NativePhase != (active ? "Active" : "Approach")) throw new InvalidOperationException("Saved phase requires a different recovery path.");
        if (active)
            return boardable == null ? _activeLocation.Invoke(new[] { recipient, location, (object)saved.Autonomous }) : _activeShip.Invoke(new[] { recipient, boardable, (object)saved.Autonomous });
        if (saved.Options == null) throw new InvalidOperationException("Approach recovery requires saved options.");
        if (boardable == null) return _walkApproach.Invoke(new[] { recipient, location, _options.Restore(saved.Options), (object)saved.Autonomous });
        return _approach.Invoke(new[] { recipient, boardable, _options.Restore(saved.Options), (object)saved.Autonomous, true });
    }
    internal void Register(object operation)
    {
        var manager = _native.Manager ?? throw new InvalidOperationException("Manager unavailable.");
        var operations = (IList)_operations.GetValue(manager)!;
        if (operations.Contains(operation)) throw new InvalidOperationException("Operation is already registered.");
        operations.Add(operation);
    }
}
