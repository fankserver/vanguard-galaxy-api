using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonContentAdapter : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly BoardingObserver _observer;
    private readonly DungeonStateStore _state;
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly DungeonLayoutBuilder _builder;
    private readonly HashSet<string> _factions;
    private readonly Type _loot;
    private readonly DungeonAttachmentIndex _index = new();
    private ConditionalWeakTable<object, DungeonOccurrence> _simulations = new();
    private ConditionalWeakTable<object, object> _placedDefenders = new();
    private readonly IDisposable _lifetime;
    private bool _disposed;
    internal DungeonContentAdapter(LifecycleHub hub, GameBindings game, BoardingObserver observer, DungeonStateStore state)
        : this(hub, observer, state, new BoardingCommandNativeBindings(game, DungeonNativeSchema.Methods, DungeonNativeSchema.Members),
            game.Assembly.GetType(DungeonNativeSchema.Room, true)!, game.Assembly.GetType(DungeonNativeSchema.Crew, true)!,
            game.Assembly.GetType(DungeonNativeSchema.Loot, true)!, NativeFactions(game)) { }
    private static IEnumerable<string> NativeFactions(GameBindings game)
    {
        var faction = game.Assembly.GetType("Source.Galaxy.Faction", true)!;
        return game.Assembly.GetTypes().Where(t => t.Namespace == "Source.Galaxy.Factions" && !t.IsAbstract && faction.IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null).Select(t => t.Name);
    }
    internal DungeonContentAdapter(LifecycleHub hub, BoardingObserver observer, DungeonStateStore state,
        IBoardingTacticalNativeBindings native, Type room, Type crew, Type loot, IEnumerable<string> factions)
    {
        _hub = hub; _observer = observer; _state = state; _native = native;
        _builder = new(native, room, crew); _loot = loot;
        if (_loot.GetConstructor(Type.EmptyTypes) == null) throw new MissingMethodException(_loot.FullName, ".ctor");
        _factions = new(factions, StringComparer.Ordinal);
        _lifetime = hub.Subscribe("vgmodapi.dungeon-attachments", message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            { _index.Clear(); _simulations = new(); _placedDefenders = new(); }
        });
    }
    internal void BeginSerialization() => _state.BeginSerialization();
    internal void EndSerialization() => _state.EndSerialization();
    internal DungeonContentBindings Bindings() => new(ValidateAttachment, Bind, ValidateChoice, ApplyChoice, _observer.HandleForInstallation);
    private object? ResolveSimulation(DungeonOccurrence occurrence)
    {
        var location = _index.Resolve(occurrence.Id); var handle = _observer.CommandHandleForLocation(location);
        if (handle == null || !_observer.TryResolveCommandTarget(handle, out _, out _, out var operation)) return null;
        return _native.Get(operation, "simulation");
    }
    private static int RoomIndex(DungeonOccurrence occurrence, DungeonEventDefinition item) => Array.FindIndex(
        occurrence.Definition.Layout.Compartments.OrderBy(c => c.Type == CompartmentType.Airlock ? 0 : 1).ToArray(), c => c.Id == item.CompartmentId);
    internal DungeonContentStatus ValidateChoice(DungeonOccurrence occurrence, DungeonEventDefinition item, DungeonChoiceDefinition choice)
    {
        _hub.CheckThread(); if (_disposed || !_state.MutationAllowed) return DungeonContentStatus.PersistenceUnavailable;
        var simulation = ResolveSimulation(occurrence);
        if (simulation == null || _native.Get(simulation, "isComplete") is true) return DungeonContentStatus.WrongPhase;
        var index = RoomIndex(occurrence, item); var rooms = (System.Collections.IList)_native.Get(simulation, "compartments")!;
        if (index < 0 || index >= rooms.Count) return DungeonContentStatus.WrongPhase;
        var state = _native.Get(rooms[index], "state")?.ToString();
        if (state is not ("Friendly" or "Investigating")) return DungeonContentStatus.WrongPhase;
        var crew = ((System.Collections.IList)_native.Get(simulation, "friendlyUnits")!).Cast<object>();
        if (!crew.Any(unit => (int)_native.Get(unit, "compartmentIndex")! == index && _native.Call("dungeonCrewAlive", unit) is true && _native.Get(unit, "authoredTransit") is false && _native.Get(unit, "authoredIncapacitated") is false &&
            (choice.RequiredCrewId == null || (string)_native.Get(unit, "authoredCrewType")! == choice.RequiredCrewId))) return DungeonContentStatus.WrongPhase;
        return DungeonContentStatus.ChoiceApplied;
    }
    internal void ApplyChoice(DungeonOccurrence occurrence, DungeonEventDefinition item, DungeonChoiceDefinition choice)
    {
        _hub.CheckThread(); var simulation = ResolveSimulation(occurrence) ?? throw new InvalidOperationException("Dungeon simulation unavailable.");
        var index = RoomIndex(occurrence, item); var room = ((System.Collections.IList)_native.Get(simulation, "compartments")!)[index]!;
        var prepared = new List<object>();
        foreach (var reward in choice.Loot)
        {
            var loot = Activator.CreateInstance(_loot)!;
            _native.Set(loot, "authoredLootId", reward.ItemId); _native.Set(loot, "authoredLootAmount", reward.Amount);
            _native.Set(loot, "authoredLootCollected", true); _native.Set(loot, "authoredLootLevel", 1); _native.Set(loot, "authoredLootRoom", index); prepared.Add(loot);
        }
        // Use vanilla's collected-loot queues, not an immediate inventory bypass of extraction and settlement.
        var simulationLoot = (System.Collections.IList)_native.Get(simulation, "authoredCollected")!;
        var roomLoot = (System.Collections.IList)_native.Get(room, "authoredCollected")!;
        foreach (var loot in prepared) { simulationLoot.Add(loot); roomLoot.Add(loot); }
    }
    internal DungeonDefinitionRegistry Catalogs() => new(_native.ValidCrew,
        id => _native.Call("dungeonItem", null, id, null!) is true,
        id => _factions.Contains(id));
    internal DungeonContentStatus ValidateAttachment(BoardingHandle target, DungeonDefinition definition)
    {
        _hub.CheckThread(); if (_disposed) return DungeonContentStatus.Unavailable;
        if (!_state.MutationAllowed) return DungeonContentStatus.PersistenceUnavailable;
        if (!_observer.TryResolveCommandTarget(target, out var location, out _, out var operation) || location == null) return DungeonContentStatus.StaleTarget;
        if (operation != null || _index.SavedMarker(location).HasValue || _native.Get(_native.Get(location, "authoredLocationData"), "authoredSavedSimulation") != null) return DungeonContentStatus.TargetInUse;
        try { _builder.Rooms(definition.Layout, Convert.ToInt32(_native.Get(location, "authoredLocationSize"))); }
        catch (ArgumentException) { return DungeonContentStatus.InvalidDefinition; }
        return DungeonContentStatus.Attached;
    }
    internal void Bind(BoardingHandle target, DungeonOccurrence occurrence)
    {
        _hub.CheckThread();
        if (_disposed || !_observer.TryResolveCommandTarget(target, out var location, out _, out _) || location == null || !_index.Bind(location, occurrence.Id))
            throw new InvalidOperationException("Dungeon target changed before attachment.");
    }
    internal bool RestoreMarker(object location, Guid id)
    { _hub.CheckThread(); return !_disposed && _index.Bind(location, id); }
    internal Guid? Marker(object location)
    { _hub.CheckThread(); return _disposed ? null : _index.SavedMarker(location); }
    internal bool ReplaceLayout(object simulation, object location)
    {
        _hub.CheckThread(); var id = _index.Find(location); if (_disposed || !id.HasValue) return false;
        var occurrence = _state.Get(id.Value) ?? throw new InvalidOperationException("Resource dungeon state is unavailable; creation cannot use a replacement vanilla layout.");
        var rooms = _builder.Rooms(occurrence.Definition.Layout, Convert.ToInt32(_native.Get(location, "authoredLocationSize")));
        object? profile = null;
        if (occurrence.Definition.FactionId != null)
        {
            if (!_factions.Contains(occurrence.Definition.FactionId)) throw new InvalidOperationException("Retained dungeon faction is unavailable.");
            profile = _native.Call("dungeonProfile", null, occurrence.Definition.FactionId)!;
            if (_native.Get(simulation, "authoredNoScuttle") is true) profile = _native.Call("dungeonNoScuttleProfile", null, profile)!;
        }
        _native.Set(simulation, "compartments", rooms);
        if (profile != null)
        {
            _native.Set(simulation, "authoredFaction", occurrence.Definition.FactionId);
            _native.Set(simulation, "authoredProfile", profile);
        }
        _simulations.Add(simulation, occurrence); return true;
    }
    internal void ReplaceWalkLayout(object operation, object simulation)
    {
        _hub.CheckThread(); var location = _native.Get(operation, "location"); if (location == null) return;
        var maximum = (float)_native.Get(simulation, "maxStructureIntegrity")!;
        var fraction = maximum > 0 ? (float)_native.Get(simulation, "structureIntegrity")! / maximum : 1f;
        if (!ReplaceLayout(simulation, location)) return;
        if (_simulations.TryGetValue(simulation, out var authored) && authored.Definition.Layout.Compartments.Any(c => c.Defenders.Count > 0))
            _native.Call("dungeonCombatMode", simulation);
        var rooms = (System.Collections.IList)_native.Get(simulation, "compartments")!;
        _native.Set(simulation, "maxStructureIntegrity", rooms.Count * 3000f);
        _native.Set(simulation, "structureIntegrity", rooms.Count * 3000f * fraction);
    }
    internal bool GuardOperation(object? operation, bool refreshProfile = false)
    {
        _hub.CheckThread(); if (_disposed || operation == null) return true;
        var location = _native.Get(operation, "location"); if (location == null) return true;
        var id = _index.SavedMarker(location); if (!id.HasValue) return true;
        var occurrence = _state.Get(id.Value);
        if (occurrence == null || !ReferenceEquals(_index.Resolve(id.Value), location)) return false;
        var simulation = _native.Get(operation, "simulation"); if (simulation == null) return true;
        _simulations.Remove(simulation); _simulations.Add(simulation, occurrence);
        var faction = occurrence.Definition.FactionId;
        if (faction != null && (refreshProfile || !Equals(_native.Get(simulation, "authoredFaction"), faction)))
        {
            if (!_factions.Contains(faction)) return false;
            var profile = _native.Call("dungeonProfile", null, faction)!;
            if (_native.Get(simulation, "authoredNoScuttle") is true) profile = _native.Call("dungeonNoScuttleProfile", null, profile)!;
            _native.Set(simulation, "authoredFaction", faction); _native.Set(simulation, "authoredProfile", profile);
        }
        return true;
    }
    internal bool AllowEffect(object simulation, bool hazard)
    {
        _hub.CheckThread(); if (_disposed) return true;
        if (!_simulations.TryGetValue(simulation, out var occurrence))
        {
            var marker = _index.FindSavedMarker(location =>
                ReferenceEquals(_native.Get(_native.Get(location, "authoredLocationData"), "authoredSavedSimulation"), simulation));
            if (!marker.HasValue) return true;
            if (_index.Resolve(marker.Value) == null) return false;
            occurrence = _state.Get(marker.Value);
            if (occurrence == null) return false;
            _simulations.Add(simulation, occurrence);
        }
        if (_state.Get(occurrence.Id) == null || _index.Resolve(occurrence.Id) == null) return false;
        return hazard ? occurrence.Definition.AllowHazards : occurrence.Definition.AllowScheduledReinforcements;
    }
    internal void CompleteWalkCreation(object dungeonData)
    {
        _hub.CheckThread(); var simulation = _native.Get(dungeonData, "authoredSavedSimulation");
        if (simulation != null && !_placedDefenders.TryGetValue(simulation, out _)) ReplaceDefenders(simulation);
    }
    internal bool ReplaceDefenders(object simulation)
    {
        _hub.CheckThread(); if (_disposed || !_simulations.TryGetValue(simulation, out var occurrence)) return false;
        var units = _builder.Defenders(occurrence.Definition.Layout, (float)_native.Get(simulation, "authoredHealth")!);
        _native.Set(simulation, "hostileUnits", units); _native.Call("dungeonOccupants", simulation);
        _placedDefenders.GetValue(simulation, _ => new object()); return true;
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return; _disposed = true;
        _lifetime.Dispose(); _index.Clear(); _simulations = new(); _placedDefenders = new();
    }
}
