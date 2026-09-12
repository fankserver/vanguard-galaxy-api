using System;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Core.Integration;

/// <summary>
/// Installed reflection-backed native seam for authored sites. Salvage sites use the game's own POI
/// setup, wreck salvage-data sizing, hazard-cloud, guaranteed derelict-station and asteroid-scatter
/// recipes; mining fields use the exact-count asteroid initialisation. Creation verifies the exact
/// membership delta (one new POI parented to the host system) and never adopts foreign natives.
/// </summary>
internal sealed class WorldNativeResourceSites : IResourceSiteNative
{
    private readonly GameAdapter _game;
    private readonly WorldMapIndex _index;
    private readonly Assembly _assembly;
    private readonly PropertyInfo _map;
    private readonly MethodInfo _setup, _shipExists, _worldPosition, _addPersistable, _addCargo, _addScrap, _addStructural,
        _createHazard, _hasDungeon, _buildDungeon, _rollAbandoned, _initAsteroids, _randomRange;
    private readonly PropertyInfo _oreData, _fieldDensity, _fieldWealth;
    private readonly FieldInfo _randomGlobal;
    private readonly PropertyInfo _elementFaction;
    private readonly FieldInfo _points, _hazardField, _asteroidsInitialized,
        _dataShip, _dataAngle, _dataPosition, _dataHazard, _hazardName, _hazardDamage, _hazardChance,
        _fieldSurface, _fieldCore, _vectorX, _vectorY;
    private readonly MethodInfo _getPersistables, _getOperation;
    private readonly PropertyInfo _dungeonManagerInstance;
    private readonly FieldInfo _locationDungeonData, _dungeonOperationActive, _dungeonSimulation,
        _playerCurrentPoi, _playerWaypoints;
    private readonly Type _dungeonLocationType;
    private readonly ConstructorInfo _salvagePoi, _miningPoi, _salvageData, _hazardFieldData, _asteroidField;
    private readonly Type _salvageType, _miningType, _vector, _factionType;
    private readonly MethodInfo _factionsGet;
    private readonly FieldInfo _allFactions;
    private readonly Action<Exception> _report;
    private readonly Type _hazardEnum, _damageEnum, _dungeonEnum;
    private bool _inPass;
    private Guid _passSession;
    private WorldMapIndex.Snapshot? _passSnapshot;

    internal WorldNativeResourceSites(GameAdapter game, Assembly assembly, Action<Exception>? report = null)
    {
        _game = game; _assembly = assembly; _report = report ?? (_ => { });
        _index = new WorldMapIndex(assembly);
        _map = assembly.GetType("Source.Player.GamePlayer", true)!.GetProperty("map", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("GamePlayer.map");
        var resolved = ResourceSiteBindings.Validate(assembly);
        _setup = resolved["siteSetup"]; _shipExists = resolved["siteShipExists"]; _worldPosition = resolved["siteWorldPosition"];
        _addPersistable = resolved["siteAddPersistable"]; _addCargo = resolved["siteAddCargo"];
        _addScrap = resolved["siteAddScrap"]; _addStructural = resolved["siteAddStructural"];
        _createHazard = resolved["siteCreateHazard"]; _hasDungeon = resolved["siteHasDungeon"];
        _buildDungeon = resolved["siteBuildDungeon"]; _rollAbandoned = resolved["siteRollAbandoned"];
        _initAsteroids = resolved["siteInitAsteroids"];
        Type Get(string name) => assembly.GetType(name, true)!;
        _salvageType = Get(ResourceSiteBindings.Salvage); _miningType = Get(ResourceSiteBindings.Mining);
        _salvagePoi = Ctor(_salvageType); _miningPoi = Ctor(_miningType);
        _salvageData = Ctor(Get(ResourceSiteBindings.SalvageData));
        _hazardFieldData = Ctor(Get(ResourceSiteBindings.HazardFieldData));
        _hazardEnum = Get(ResourceSiteBindings.HazardName); _damageEnum = Get(ResourceSiteBindings.DamageType);
        _dungeonEnum = Get(ResourceSiteBindings.DungeonType);
        var field = Get(ResourceSiteBindings.AsteroidField);
        _asteroidField = field.GetConstructor(new[] { typeof(int), typeof(float), typeof(float), Get(ResourceSiteBindings.OreSet), Get(ResourceSiteBindings.OreSet), typeof(float) })
            ?? throw new MissingMethodException(field.FullName, ".ctor");
        var system = Get(ResourceSiteBindings.System);
        _oreData = system.GetProperty("systemOreData", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException("systemOreData");
        _points = Field(system, "pointsOfInterest");
        _elementFaction = Get(ResourceSiteBindings.Element).GetProperty("faction", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("MapElement.faction");
        var poi = Get(ResourceSiteBindings.Poi);
        _hazardField = Field(poi, "hazardFieldData"); _asteroidsInitialized = Field(poi, "asteroidsInitialized");
        // The salvage site's derelict station, when present, is a DungeonLocationData persistable. Its
        // live operation and persisted simulation are read through the same native shapes the dungeon
        // aegis uses, so removing a site never strands a boarded or persisted interior.
        _getPersistables = poi.GetMethod("GetPersistables", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(poi.FullName, "GetPersistables");
        _dungeonLocationType = Get(ResourceSiteBindings.DungeonLocationData);
        _locationDungeonData = Field(_dungeonLocationType, "dungeonData");
        var dungeonData = Get("Source.Dungeon.DungeonData");
        _dungeonOperationActive = Field(dungeonData, "isOperationActive");
        _dungeonSimulation = Field(dungeonData, "simulation");
        var dungeonManager = Get("Behaviour.Managers.DungeonManager");
        _dungeonManagerInstance = dungeonManager.BaseType!.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMemberException(dungeonManager.BaseType.FullName, "Instance");
        _getOperation = dungeonManager.GetMethod("GetOperation", new[] { _dungeonLocationType })
            ?? throw new MissingMethodException(dungeonManager.FullName, "GetOperation");
        var playerType = Get("Source.Player.GamePlayer");
        _playerCurrentPoi = Field(playerType, "currentPointOfInterest");
        _playerWaypoints = Field(playerType, "waypoints");
        var data = Get(ResourceSiteBindings.SalvageData);
        _dataShip = Field(data, "shipTemplate"); _dataAngle = Field(data, "angle");
        _dataPosition = Field(data, "position"); _dataHazard = Field(data, "hazardData");
        var hazard = Get(ResourceSiteBindings.HazardFieldData);
        _hazardName = Field(hazard, "hazardName"); _hazardDamage = Field(hazard, "damageType"); _hazardChance = Field(hazard, "spawnChance");
        _fieldDensity = field.GetProperty("density", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException("density");
        _fieldWealth = field.GetProperty("wealth", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException("wealth");
        _fieldSurface = Field(field, "surfaceOres"); _fieldCore = Field(field, "coreOres");
        // Vector2 is defined in UnityEngine.CoreModule, not Assembly-CSharp, so a name lookup against the
        // game assembly throws at runtime while Cecil metadata tests pass. Resolve the real type from a
        // bound method's return value (GetWorldPosition returns UnityEngine.Vector2) instead.
        _vector = _worldPosition.ReturnType;
        _vectorX = Field(_vector, "x"); _vectorY = Field(_vector, "y");
        var random = Get(ResourceSiteBindings.SeededRandom);
        _randomGlobal = random.GetField("Global", BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMemberException("SeededRandom.Global");
        _randomRange = random.GetMethod("RandomRange", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null)
            ?? throw new MissingMethodException("SeededRandom.RandomRange");
        _factionType = Get(ResourceSiteBindings.Faction);
        _factionsGet = _factionType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException("Faction.Get");
        _allFactions = _factionType.GetField("allFactions", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException("Faction.allFactions");
    }
    private static FieldInfo Field(Type type, string name)
        => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(type.FullName, name);
    private static ConstructorInfo Ctor(Type type)
        => type.GetConstructor(Type.EmptyTypes) ?? throw new MissingMethodException(type.FullName, ".ctor");
    private object? Map(Guid session)
    {
        if (!_game.TryGetObservedPlayer(session, out var player) || player == null) return null;
        return _map.GetValue(player);
    }
    private WorldMapIndex.Snapshot? Snapshot(Guid session)
    {
        bool cache = _inPass && session == _passSession;
        if (cache && _passSnapshot != null) return _passSnapshot;
        var map = Map(session);
        if (map == null) return null;
        var snapshot = _index.Read(map);
        if (cache) _passSnapshot = snapshot;
        return snapshot;
    }
    public void BeginPass(Guid session) { _inPass = true; _passSession = session; _passSnapshot = null; }
    public void EndPass() { _inPass = false; _passSnapshot = null; }

    public string? CreateSite(Guid session, string systemId, float x, float y, ResourceSiteDeclaration declaration)
    {
        try
        {
            var map = Map(session);
            if (map == null) return null;
            var before = _index.Read(map);
            var host = before.FindSystem(systemId);
            if (host == null) return null;
            object? faction = null;
            if (declaration.Kind == ResourceSiteKind.SalvageSite)
            {
                if (_shipExists.Invoke(null, new object[] { declaration.WreckShipId! }) is not true) return null;
                // Existing factions only: Faction.Get would silently create an unknown identity.
                if (_allFactions.GetValue(null) is not System.Collections.IDictionary factions
                    || !factions.Contains(declaration.FactionId!)) return null;
                faction = _factionsGet.Invoke(null, new object[] { declaration.FactionId! });
                if (faction == null) return null;
            }
            // An exact-count field with no ore data would silently violate the count contract.
            if (declaration.Kind == ResourceSiteKind.MiningField && _oreData.GetValue(host) == null) return null;
            var poi = declaration.Kind == ResourceSiteKind.SalvageSite ? _salvagePoi.Invoke(null) : _miningPoi.Invoke(null);
            var position = Activator.CreateInstance(_vector)!;
            _vectorX.SetValue(position, x); _vectorY.SetValue(position, y);
            var placed = _setup.Invoke(host, new[] { poi, position, faction, declaration.Level });
            if (!ReferenceEquals(placed, poi)) return null;
            var members = (System.Collections.IList)_points.GetValue(host)!;
            members.Add(poi);
            try
            {
                // Exact membership delta: exactly this one new POI, parented to the host, nothing removed.
                var after = _index.Read(map);
                if (!VerifySiteDelta(before, after, poi, host)) { members.Remove(poi); return null; }
                if (declaration.Kind == ResourceSiteKind.SalvageSite) PopulateSalvage(host, poi, declaration);
                else PopulateMining(host, poi, declaration);
                foreach (var pair in after.Points)
                    if (ReferenceEquals(pair.Value, poi)) return pair.Key;
                members.Remove(poi);
                return null;
            }
            catch
            {
                // A refused creation never leaves a partially populated native in the system.
                try { members.Remove(poi); } catch { }
                throw;
            }
        }
        catch (Exception error) { Report(error); return null; }
    }

    /// <summary>Exactly one new POI, it is the created occurrence, it is parented to the host system, and nothing was removed.</summary>
    internal static bool VerifySiteDelta(WorldMapIndex.Snapshot before, WorldMapIndex.Snapshot after, object created, object host)
    {
        var beforePoints = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Points) beforePoints.Add(pair.Value);
        var afterPoints = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Points) afterPoints.Add(pair.Value);
        if (afterPoints.Count != beforePoints.Count + 1) return false;
        foreach (var value in beforePoints) if (!Contains(afterPoints, value)) return false;
        object? added = null;
        foreach (var value in afterPoints) if (!Contains(beforePoints, value)) { added = value; break; }
        if (!ReferenceEquals(added, created)) return false;
        var beforeSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Systems) beforeSystems.Add(pair.Value);
        var afterSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Systems) afterSystems.Add(pair.Value);
        if (afterSystems.Count != beforeSystems.Count) return false;
        bool hostPresent = false;
        foreach (var value in afterSystems) { if (!Contains(beforeSystems, value)) return false; if (ReferenceEquals(value, host)) hostPresent = true; }
        return hostPresent;
        static bool Contains(System.Collections.Generic.HashSet<object> set, object value)
        { foreach (var item in set) if (ReferenceEquals(item, value)) return true; return false; }
    }

    private void PopulateSalvage(object host, object poi, ResourceSiteDeclaration declaration)
    {
        object? hazardName = null, damageType = null;
        if (declaration.Hazard.HasValue)
        {
            hazardName = Enum.Parse(_hazardEnum, "DamageInRadius");
            damageType = Enum.Parse(_damageEnum, "Radiation");
            var fieldData = _hazardFieldData.Invoke(null);
            _hazardName.SetValue(fieldData, hazardName); _hazardDamage.SetValue(fieldData, damageType);
            _hazardChance.SetValue(fieldData, 1f);
            _hazardField.SetValue(poi, fieldData);
        }
        var data = _salvageData.Invoke(null);
        var world = _worldPosition.Invoke(poi, null)!;
        var offset = Activator.CreateInstance(_vector)!;
        _vectorX.SetValue(offset, (float)_vectorX.GetValue(world)! + 8f);
        _vectorY.SetValue(offset, (float)_vectorY.GetValue(world)! + 2f);
        _dataPosition.SetValue(data, offset);
        _dataAngle.SetValue(data, 25f);
        _dataShip.SetValue(data, declaration.WreckShipId);
        _addScrap.Invoke(data, new object?[] { declaration.Level, 1f, 3, null });
        _addStructural.Invoke(data, new object?[] { declaration.Level, 3, 1.5f, null });
        if (declaration.Hazard.HasValue)
            _dataHazard.SetValue(data, _createHazard.Invoke(poi, new[] { hazardName!, damageType! }));
        _addPersistable.Invoke(poi, new[] { data });
        var cargoSize = Activator.CreateInstance(_vector)!;
        _vectorX.SetValue(cargoSize, 20f); _vectorY.SetValue(cargoSize, 16f);
        _addCargo.Invoke(poi, new object[] { cargoSize, 1, 0.2f });
        if (declaration.WithStation && _hasDungeon.Invoke(poi, null) is false)
        {
            // Deterministic contract: no 25% probability roll and no consumer retry loop. Type and
            // crewing flavor keep the native distribution; existence is guaranteed.
            var random = _randomGlobal.GetValue(null)!;
            int pick = (int)_randomRange.Invoke(random, new object[] { 0, 3 })!;
            var type = Enum.Parse(_dungeonEnum, pick == 0 ? "ResearchStation" : pick == 1 ? "RelayStation" : "IndustrialFacility");
            bool abandoned = _rollAbandoned.Invoke(null, new[] { type, random }) is true;
            var dungeonFaction = abandoned ? null : _elementFaction.GetValue(poi) ?? _elementFaction.GetValue(host);
            _addPersistable.Invoke(poi, new[] { _buildDungeon.Invoke(poi, new[] { type, dungeonFaction, random, abandoned })! });
        }
        if (declaration.ScatterAsteroids) Scatter(host, poi, 12, keepMiddleClear: true);
    }

    private void PopulateMining(object host, object poi, ResourceSiteDeclaration declaration)
        => Scatter(host, poi, declaration.AsteroidCount, keepMiddleClear: true);

    private void Scatter(object host, object poi, int amount, bool keepMiddleClear)
    {
        var ore = _oreData.GetValue(host);
        if (ore == null) return;
        // An exact-count field drawn from the host system's own ore data; the helper bypasses the
        // probabilistic mining-dungeon roll that the POI-level initializer would add.
        var field = _asteroidField.Invoke(new[]
        {
            amount, _fieldDensity.GetValue(ore)!, _fieldWealth.GetValue(ore)!,
            _fieldSurface.GetValue(ore), _fieldCore.GetValue(ore), -1f
        });
        _initAsteroids.Invoke(null, new[] { poi, field, false, keepMiddleClear });
        _asteroidsInitialized.SetValue(poi, true);
    }

    public string? ResolveSite(Guid session, string systemId, string poiId, ResourceSiteKind kind)
    {
        var snapshot = Snapshot(session);
        var poi = snapshot?.FindPoint(poiId);
        if (snapshot == null || poi == null) return null;
        var host = snapshot.FindSystem(systemId);
        if (host == null) return null;
        var expected = kind == ResourceSiteKind.SalvageSite ? _salvageType : _miningType;
        if (!expected.IsInstanceOfType(poi)) return null;
        // Structural containment: the POI must currently be a member of the host system.
        if (!((System.Collections.IList)_points.GetValue(host)!).Contains(poi)) return null;
        return poiId;
    }

    public int AmbiguousCount(Guid session, string poiId)
    {
        // The snapshot indexer already rejects duplicate POI identities while reading; a readable
        // snapshot therefore proves at most one bearer. An unreadable map counts as zero bearers.
        return Snapshot(session)?.FindPoint(poiId) != null ? 1 : 0;
    }

    /// <summary>
    /// Removes the owned site POI from its host system. Ownership is structural (exact kind, membership
    /// in the recorded host), the player must not be at or routed to it, a salvage site's station must
    /// not have a live operation or persisted interior, and the post-removal membership delta must be
    /// exactly this one POI with nothing else changed; otherwise the POI is restored.
    /// </summary>
    public ResourceSiteRemoveOutcome RemoveSite(Guid session, string systemId, string poiId, ResourceSiteKind kind)
    {
        var map = Map(session);
        if (map == null) return ResourceSiteRemoveOutcome.Failed;
        // A duplicated native GUID cannot be attributed safely: refuse rather than remove an arbitrary copy.
        if (AmbiguousCount(session, poiId) > 1) return ResourceSiteRemoveOutcome.Missing;
        var before = _index.Read(map);
        var poi = before.FindPoint(poiId);
        var host = before.FindSystem(systemId);
        if (poi == null || host == null) return ResourceSiteRemoveOutcome.Missing;
        var expected = kind == ResourceSiteKind.SalvageSite ? _salvageType : _miningType;
        if (!expected.IsInstanceOfType(poi)) return ResourceSiteRemoveOutcome.Missing;
        var members = (System.Collections.IList)_points.GetValue(host)!;
        if (!members.Contains(poi)) return ResourceSiteRemoveOutcome.Missing;
        if (!_game.TryGetObservedPlayer(session, out var player) || player == null) return ResourceSiteRemoveOutcome.Failed;
        var currentPoi = _playerCurrentPoi.GetValue(player);
        if (currentPoi != null && ReferenceEquals(currentPoi, poi)) return ResourceSiteRemoveOutcome.PlayerInside;
        if (_playerWaypoints.GetValue(player) is System.Collections.IEnumerable waypoints)
            foreach (var waypoint in waypoints)
                if (waypoint != null && ReferenceEquals(waypoint, poi)) return ResourceSiteRemoveOutcome.PlayerInside;
        var boarding = StationInUse(poi);
        if (boarding != ResourceSiteRemoveOutcome.Removed) return boarding;
        // Remove by reference, never by equality: a native POI that overrode Equals must not let the
        // removal pick a different-but-equal member while the owned POI survives.
        int removalIndex = IndexOf(members, poi);
        if (removalIndex < 0) return ResourceSiteRemoveOutcome.Missing;
        try
        {
            members.RemoveAt(removalIndex);
            if (!_game.TryGetObservedPlayer(session, out var current) || !ReferenceEquals(current, player) ||
                !ReferenceEquals(_map.GetValue(current), map))
            { Rollback(members, poi, removalIndex); return ResourceSiteRemoveOutcome.Failed; }
            var after = _index.Read(map);
            if (!VerifyRemoveDelta(before, after, poi, host)) { Rollback(members, poi, removalIndex); return ResourceSiteRemoveOutcome.Failed; }
            return ResourceSiteRemoveOutcome.Removed;
        }
        catch (Exception error)
        {
            try { Rollback(members, poi, removalIndex); } catch { /* rollback is best-effort only */ }
            Report(error); return ResourceSiteRemoveOutcome.Failed;
        }
    }

    /// <summary>The salvage site's derelict station is in use when a boarding operation is live or an
    /// interior simulation is persisted. Other site kinds carry no boarding state.</summary>
    private ResourceSiteRemoveOutcome StationInUse(object poi)
    {
        object? location = null;
        if (_getPersistables.Invoke(poi, null) is System.Collections.IEnumerable persistables)
            foreach (var persistable in persistables)
                if (persistable != null && _dungeonLocationType.IsInstanceOfType(persistable)) { location = persistable; break; }
        if (location == null) return ResourceSiteRemoveOutcome.Removed;
        var data = _locationDungeonData.GetValue(location);
        if (data != null)
        {
            if (_dungeonOperationActive.GetValue(data) is true) return ResourceSiteRemoveOutcome.BoardingActive;
            if (_dungeonSimulation.GetValue(data) != null) return ResourceSiteRemoveOutcome.InteriorPersisted;
        }
        try
        {
            var manager = _dungeonManagerInstance.GetValue(null);
            if (manager != null && _getOperation.Invoke(manager, new[] { location }) != null)
                return ResourceSiteRemoveOutcome.BoardingActive;
        }
        catch { /* a missing dungeon manager fails open; the persisted-state checks above still apply */ }
        return ResourceSiteRemoveOutcome.Removed;
    }

    /// <summary>Best-effort restoration of the site POI at its original list position after a failed removal.</summary>
    private static void Rollback(System.Collections.IList members, object poi, int index)
    {
        try { if (IndexOf(members, poi) < 0) members.Insert(index < members.Count ? index : members.Count, poi); } catch { /* best-effort only */ }
    }

    private static int IndexOf(System.Collections.IList members, object value)
    { for (int i = 0; i < members.Count; i++) if (ReferenceEquals(members[i], value)) return i; return -1; }

    /// <summary>Exactly the given site POI was removed from the host and nothing else changed.</summary>
    internal static bool VerifyRemoveDelta(WorldMapIndex.Snapshot before, WorldMapIndex.Snapshot after, object removed, object host)
    {
        var beforePoints = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Points) beforePoints.Add(pair.Value);
        var afterPoints = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Points) afterPoints.Add(pair.Value);
        if (afterPoints.Count != beforePoints.Count - 1) return false;
        bool removedPresent = false;
        foreach (var value in beforePoints)
        {
            if (ReferenceEquals(value, removed)) { removedPresent = true; continue; }
            if (!DeltaContains(afterPoints, value)) return false; // an unrelated POI was removed
        }
        if (!removedPresent) return false; // the owned site was not present
        foreach (var value in afterPoints) if (!DeltaContains(beforePoints, value)) return false; // a POI was added
        var beforeSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Systems) beforeSystems.Add(pair.Value);
        var afterSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Systems) afterSystems.Add(pair.Value);
        if (afterSystems.Count != beforeSystems.Count) return false;
        bool hostPresent = false;
        foreach (var value in afterSystems)
        { if (!DeltaContains(beforeSystems, value)) return false; if (ReferenceEquals(value, host)) hostPresent = true; }
        return hostPresent;
        static bool DeltaContains(System.Collections.Generic.HashSet<object> set, object value)
        { foreach (var item in set) if (ReferenceEquals(item, value)) return true; return false; }
    }

    private void Report(Exception error)
    {
        if (error is TargetInvocationException tie && tie.InnerException != null) _report(tie.InnerException);
        else _report(error);
    }
}
