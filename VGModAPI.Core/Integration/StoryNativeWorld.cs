using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>
/// The thin native adapter behind <see cref="IStoryWorld"/>. It performs vanilla operations and
/// VERIFIES their result; it holds no policy, decides no outcome and never records anything. A native
/// failure is reported as a refusal so the story module can decide, and so no acceptance is ever
/// recorded that the world did not actually make.
///
/// Ownership is tracked by the definition object this adapter installed. Vanilla's own registration
/// replaces a duplicate identifier, so an identifier the adapter did not install is left untouched,
/// and an entry that someone else has since replaced is never removed.
/// </summary>
internal sealed class StoryNativeWorld : IStoryWorld, IStoryObjectiveWorld, IDisposable
{
    private readonly StoryNativeBindings _bindings;
    private readonly Action _checkThread;
    private readonly Action<Exception>? _fault;
    private readonly Dictionary<string, object> _owned = new(StringComparer.Ordinal);
    /// <summary>The exact mission object each acceptance produced, so a rollback removes THAT object.</summary>
    private readonly Dictionary<string, object> _accepted = new(StringComparer.Ordinal);
    private bool _disposed;

    internal StoryNativeWorld(StoryNativeBindings bindings, Action checkThread, Action<Exception>? fault = null)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _checkThread = checkThread ?? throw new ArgumentNullException(nameof(checkThread));
        _fault = fault;
    }

    public StoryWorldResult MigrateScripted(string identifier, StoryMissionDefinition definition, StoryObjectiveLayout source,
        StoryObjectiveLayout destination, Func<bool> stillValid)
    {
        _checkThread();
        try
        {
            var player = _bindings.CurrentPlayer;
            if (_disposed || player == null || !_owned.TryGetValue(identifier, out var catalog))
                return new StoryWorldResult(StoryWorldStatus.Unavailable, "No current owned occurrence.");
            var held = _bindings.Held(player);
            if (held.Missions > StoryQuarantine.MaxScannedMissions || held.Objectives > StoryQuarantine.MaxScannedObjectives)
                return new StoryWorldResult(StoryWorldStatus.Unavailable, "Migration scan exceeds its bound.");
            if (held.Objectives - source.Slots.Count + destination.Slots.Count > StoryQuarantine.MaxScannedObjectives)
                return new StoryWorldResult(StoryWorldStatus.Refused, "Migrated objectives would exceed the live scan budget.");
            var mission = _bindings.ActiveStory(player, identifier);
            bool Stable()
            {
                if (!stillValid() || _disposed || !ReferenceEquals(_bindings.CurrentPlayer, player)
                    || !ReferenceEquals(_bindings.Catalog[identifier], catalog)
                    || !ReferenceEquals(_bindings.ActiveStory(player, identifier), mission)) return false;
                var currentCounts = _bindings.Held(player);
                return currentCounts.Missions <= StoryQuarantine.MaxScannedMissions
                    && currentCounts.Objectives <= StoryQuarantine.MaxScannedObjectives
                    && currentCounts.Objectives - source.Slots.Count + destination.Slots.Count <= StoryQuarantine.MaxScannedObjectives
                    && _bindings.ActiveStoryIdentifiers(player).Count(value => value == identifier) == 1;
            }
            if (mission == null || !_bindings.MigrateScripted(mission, player, identifier, definition, source, destination, Stable))
                return new StoryWorldResult(StoryWorldStatus.Refused, "The current scripted occurrence could not be safely migrated.");
            return StoryWorldResult.Ok;
        }
        catch (Exception error) { Report(error); return new StoryWorldResult(StoryWorldStatus.Unavailable, "Scripted migration could not be verified."); }
    }

    public StoryWorldResult SetScriptedProgress(string identifier, StoryObjectiveLayout.Slot slot, int progress, Func<bool>? stillValid = null)
    {
        _checkThread();
        if (_disposed) return new StoryWorldResult(StoryWorldStatus.Unavailable, "The story adapter is disposed.");
        try
        {
            if (!_owned.TryGetValue(identifier, out var definition) || !ReferenceEquals(_bindings.Catalog[identifier], definition))
                return new StoryWorldResult(StoryWorldStatus.Refused, "The occurrence catalog entry is no longer owned.");
            var player = _bindings.CurrentPlayer;
            if (player == null) return new StoryWorldResult(StoryWorldStatus.Unavailable, "No current player.");
            var held = _bindings.Held(player);
            if (held.Missions > StoryQuarantine.MaxScannedMissions || held.Objectives > StoryQuarantine.MaxScannedObjectives)
                return new StoryWorldResult(StoryWorldStatus.Unavailable, "The live objective scan exceeds its bounds.");
            if (_bindings.ActiveStoryIdentifiers(player).Count(value => value == identifier) != 1)
                return new StoryWorldResult(StoryWorldStatus.Refused, "The current occurrence is missing or ambiguous.");
            var mission = _bindings.ActiveStory(player, identifier);
            bool Stable() => (stillValid?.Invoke() ?? true) && !_disposed
                && ReferenceEquals(_bindings.CurrentPlayer, player)
                && _owned.TryGetValue(identifier, out var owned) && ReferenceEquals(owned, definition)
                && ReferenceEquals(_bindings.Catalog[identifier], definition)
                && _bindings.ActiveStoryIdentifiers(player).Count(value => value == identifier) == 1
                && ReferenceEquals(_bindings.ActiveStory(player, identifier), mission);
            if (mission == null || !_bindings.SetScriptedProgress(mission, slot, progress, Stable))
                return new StoryWorldResult(StoryWorldStatus.Refused, "The current objective is inactive or differs from retained state.");
            return StoryWorldResult.Ok;
        }
        catch (Exception error) { Report(error); return new StoryWorldResult(StoryWorldStatus.Unavailable, "The current objective could not be verified."); }
    }

    public bool KnowsFaction(string factionId)
    {
        _checkThread();
        if (factionId == null) throw new ArgumentNullException(nameof(factionId));
        if (_disposed) return false;
        try { return _bindings.KnowsFaction(factionId); }
        catch (Exception error) { Report(error); return false; }
    }

    public bool? KnowsPointOfInterest(string guid)
    {
        _checkThread();
        if (guid == null) throw new ArgumentNullException(nameof(guid));
        if (_disposed) return null;
        try { return _bindings.KnowsPointOfInterest(guid); }
        catch (Exception error) { Report(error); return null; }
    }

    public IReadOnlyCollection<string> InstalledIdentifiers()
    {
        _checkThread();
        if (_disposed) return Array.Empty<string>();
        try { return _bindings.Catalog.Keys.Cast<object>().Select(key => (string)key).ToArray(); }
        catch (Exception error) { Report(error); return Array.Empty<string>(); }
    }

    public StoryWorldResult Install(string identifier, StoryMissionDefinition definition)
    {
        _checkThread();
        if (identifier == null) throw new ArgumentNullException(nameof(identifier));
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (_disposed) return new StoryWorldResult(StoryWorldStatus.Unavailable, "The story world adapter is disposed.");
        try
        {
            var catalog = _bindings.Catalog;
            if (catalog.Contains(identifier))
            {
                // Vanilla's Add would REPLACE it. Ours or not, nothing is overwritten here.
                var existing = catalog[identifier]!;
                if (_owned.TryGetValue(identifier, out var mine) && ReferenceEquals(mine, existing)) return StoryWorldResult.Ok;
                return new StoryWorldResult(StoryWorldStatus.AlreadyPresent, "Identifier '" + identifier + "' already exists in the world catalog.");
            }
            // The generator receives the player the game is building the mission for, which is where
            // the source location comes from; nothing else about the caller is captured.
            var native = _bindings.CreateDefinition(identifier,
                player => _bindings.CreateMission(definition, identifier, player), definition.Title);
            _bindings.AddDefinition(native);
            var installed = catalog.Contains(identifier) ? catalog[identifier] : null;
            if (installed == null || !ReferenceEquals(installed, native))
                return new StoryWorldResult(StoryWorldStatus.Refused, "The world did not hold the installed definition afterwards.");
            _owned[identifier] = native;
            return StoryWorldResult.Ok;
        }
        catch (Exception error)
        {
            Report(error);
            return new StoryWorldResult(StoryWorldStatus.Refused, "Native installation failed: " + error.GetType().Name + ".");
        }
    }

    public bool Uninstall(string identifier)
    {
        _checkThread();
        if (identifier == null) throw new ArgumentNullException(nameof(identifier));
        if (_disposed || !_owned.TryGetValue(identifier, out var mine)) return false;
        try
        {
            var catalog = _bindings.Catalog;
            // Only while the catalog still holds OUR entry: a replacement belongs to whoever made it.
            if (catalog.Contains(identifier) && ReferenceEquals(catalog[identifier], mine)) catalog.Remove(identifier);
            _owned.Remove(identifier);
            return true;
        }
        catch (Exception error) { Report(error); return false; }
    }

    public StoryWorldResult Accept(string identifier)
    {
        _checkThread();
        if (identifier == null) throw new ArgumentNullException(nameof(identifier));
        if (_disposed) return new StoryWorldResult(StoryWorldStatus.Unavailable, "The story world adapter is disposed.");
        if (!_owned.ContainsKey(identifier))
            return new StoryWorldResult(StoryWorldStatus.Refused, "Identifier '" + identifier + "' is not installed by this API.");
        try
        {
            var player = _bindings.CurrentPlayer;
            if (player == null) return new StoryWorldResult(StoryWorldStatus.Unavailable, "No current player.");
            // Vanilla refuses a story identifier that is already active or archived; asking first keeps
            // the refusal a diagnosis instead of a silently skipped call.
            if (_bindings.HasStory(player, identifier))
                return new StoryWorldResult(StoryWorldStatus.AlreadyPresent, "The world already holds story '" + identifier + "'.");
            // The game's own capacity check, which AddMissionWithLog does not make. Asked BEFORE the
            // mission is handed over, so a refusal leaves the world exactly as it was.
            if (_bindings.MissionsLimitExceeded(player))
                return new StoryWorldResult(StoryWorldStatus.Refused,
                    "The player already holds the game's limit of " + _bindings.MissionLimit + " missions.");
            var mission = _bindings.BuildMission(player, identifier);
            // The guards can only protect what they can still scan. Accepting a mission that pushes the
            // world past that bound would create content nobody could then decide about, so the
            // capacity is checked against what the player holds — vanilla missions included.
            var held = _bindings.Held(player);
            if (held.Missions + 1 > StoryQuarantine.MaxScannedMissions)
                return new StoryWorldResult(StoryWorldStatus.Refused,
                    "Accepting this mission would leave more missions than the protection guard can scan.");
            if (held.Objectives + _bindings.ObjectiveCount(mission) > StoryQuarantine.MaxScannedObjectives)
                return new StoryWorldResult(StoryWorldStatus.Refused,
                    "Accepting this mission would leave more objectives than the protection guard can scan.");
            _bindings.Accept(player, mission);
            // Verified, never assumed: AddMissionWithLog returns void and can skip.
            var active = _bindings.ActiveStory(player, identifier);
            if (active == null || !ReferenceEquals(active, mission))
                return new StoryWorldResult(StoryWorldStatus.Refused, "The world did not hold the accepted mission afterwards.");
            _accepted[identifier] = mission;
            return StoryWorldResult.Ok;
        }
        catch (Exception error)
        {
            Report(error);
            return new StoryWorldResult(StoryWorldStatus.Refused, "Native acceptance failed: " + error.GetType().Name + ".");
        }
    }

    public StoryWorldResult Release(string identifier, StoryOutcome outcome)
    {
        _checkThread();
        if (identifier == null) throw new ArgumentNullException(nameof(identifier));
        if (!Enum.IsDefined(typeof(StoryOutcome), outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        if (_disposed) return new StoryWorldResult(StoryWorldStatus.Unavailable, "The story world adapter is disposed.");
        if (!_owned.ContainsKey(identifier))
            return new StoryWorldResult(StoryWorldStatus.Refused, "Identifier '" + identifier + "' is not installed by this API.");
        try
        {
            var player = _bindings.CurrentPlayer;
            if (player == null) return new StoryWorldResult(StoryWorldStatus.Unavailable, "No current player.");
            var mission = _bindings.ActiveStory(player, identifier);
            if (mission == null) { _accepted.Remove(identifier); return StoryWorldResult.Ok; }   // The world already ended it.
            if (outcome == StoryOutcome.Completed)
                // A completion is the world's to make, with the world's rewards. The API never fabricates it.
                return new StoryWorldResult(StoryWorldStatus.Refused, "The world still holds this mission; a completion is not the API's to declare.");
            _bindings.Abandon(player, mission);
            if (_bindings.ActiveStory(player, identifier) != null)
                return new StoryWorldResult(StoryWorldStatus.Refused, "The world still held the mission after abandonment.");
            _accepted.Remove(identifier);
            return StoryWorldResult.Ok;
        }
        catch (Exception error)
        {
            Report(error);
            return new StoryWorldResult(StoryWorldStatus.Refused, "Native release failed: " + error.GetType().Name + ".");
        }
    }

    public StoryWorldResult RollbackAccept(string identifier)
    {
        _checkThread();
        if (identifier == null) throw new ArgumentNullException(nameof(identifier));
        if (_disposed) return new StoryWorldResult(StoryWorldStatus.Unavailable, "The story world adapter is disposed.");
        if (!_accepted.TryGetValue(identifier, out var mission))
            return new StoryWorldResult(StoryWorldStatus.Refused, "No acceptance of '" + identifier + "' is known to this adapter.");
        try
        {
            var player = _bindings.CurrentPlayer;
            if (player == null) return new StoryWorldResult(StoryWorldStatus.Unavailable, "No current player.");
            var held = _bindings.ActiveStory(player, identifier);
            if (held == null) { _accepted.Remove(identifier); return StoryWorldResult.Ok; }
            // Only the exact object this adapter's acceptance produced is ever removed, and it is
            // removed WITHOUT archiving, so the undone acceptance leaves no completed story behind.
            if (!ReferenceEquals(held, mission))
                return new StoryWorldResult(StoryWorldStatus.Refused, "The world holds a different mission for '" + identifier + "'.");
            _bindings.Abandon(player, mission);
            if (_bindings.ActiveStory(player, identifier) != null)
                return new StoryWorldResult(StoryWorldStatus.Refused, "The world still held the mission after the rollback.");
            _accepted.Remove(identifier);
            return StoryWorldResult.Ok;
        }
        catch (Exception error)
        {
            Report(error);
            return new StoryWorldResult(StoryWorldStatus.Refused, "Native rollback failed: " + error.GetType().Name + ".");
        }
    }

    public StoryWorldSnapshot? Snapshot()
    {
        _checkThread();
        if (_disposed) return null;
        try
        {
            var player = _bindings.CurrentPlayer;
            if (player == null) return null;
            var active = _bindings.ActiveStoryIdentifiers(player).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).ToArray();
            return new StoryWorldSnapshot(InstalledIdentifiers(), active, _bindings.ArchivedIdentifiers(player));
        }
        catch (Exception error) { Report(error); return null; }
    }

    public void Dispose()
    {
        _checkThread();
        if (_disposed) return;
        foreach (var identifier in _owned.Keys.ToArray()) Uninstall(identifier);
        _owned.Clear();
        _disposed = true;
    }

    private void Report(Exception error) => _fault?.Invoke(error);
}
