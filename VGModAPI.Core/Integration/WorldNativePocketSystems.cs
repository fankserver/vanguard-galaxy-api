using System;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Core.Integration;

/// <summary>
/// Installed reflection-backed native seam for authored pocket systems. Pocket creation uses the game's
/// own SandboxWorld.AddSideContentSystemToSystem (no storyteller => nothing is generated inside), the
/// entrance gate is resolved structurally (GetEntranceJumpgate + GetTargetPOI), and both paired gates are
/// unhidden and opened/closed together. All faults fail open and report once per distinct cause.
/// </summary>
internal sealed class WorldNativePocketSystems : IPocketSystemNative
{
    private readonly GameAdapter _game;
    private readonly WorldMapIndex _index;
    private readonly Type _jumpGateType;
    private readonly PropertyInfo _map;
    private readonly FieldInfo _sector, _parent, _hidden, _jumpgateOpen;
    private readonly FieldInfo _sectorSystems, _galaxySectors, _playerCurrentSystem, _playerCurrentPoi, _playerWaypoints;
    private readonly FieldInfo _parentLevel, _pocketSystem, _systemPosition, _allFactions;
    private readonly MethodInfo _factionGet;
    private readonly PropertyInfo _systemName, _systemFaction;
    private readonly PropertyInfo _guid;
    private readonly MethodInfo _entrance, _target, _unlock, _lock, _removePoi;
    private readonly MethodInfo _galaxyRandomPosition, _galaxyAddSector, _sectorCreate, _sectorName, _emptyCreate, _gatePair;
    private readonly int _level;
    private readonly Action<Exception> _report;
    private bool _inPass;
    private Guid _passSession;
    private WorldMapIndex.Snapshot? _passSnapshot;

    internal WorldNativePocketSystems(GameAdapter game, Assembly assembly, int level = 10, Action<Exception>? report = null)
    {
        _game = game; _level = level; _report = report ?? (_ => { });
        _index = new WorldMapIndex(assembly);
        _jumpGateType = assembly.GetType(PocketSystemBindings.JumpGate, true)!;
        _map = assembly.GetType("Source.Player.GamePlayer", true)!.GetProperty("map", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("GamePlayer.map");
        _sector = Field(assembly.GetType(PocketSystemBindings.System, true)!, "sector");
        _systemPosition = Field(assembly.GetType(PocketSystemBindings.System, true)!, "position");
        _parent = Field(assembly.GetType(PocketSystemBindings.Element, true)!, "system");
        _sectorSystems = Field(assembly.GetType(PocketSystemBindings.Sector, true)!, "systems");
        _galaxySectors = Field(assembly.GetType(PocketSystemBindings.Galaxy, true)!, "sectors");
        _parentLevel = Field(assembly.GetType(PocketSystemBindings.Element, true)!, "level");
        _pocketSystem = Field(assembly.GetType(PocketSystemBindings.System, true)!, "pocketSystem");

        var factionType = assembly.GetType(PocketSystemBindings.Faction, true)!;
        _allFactions = factionType.GetField("allFactions", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(PocketSystemBindings.Faction, "allFactions");
        _factionGet = factionType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException(PocketSystemBindings.Faction, "Get(string)");
        var playerType = assembly.GetType(PocketSystemBindings.Player, true)!;
        _playerCurrentSystem = Field(playerType, "currentSystem");
        _playerCurrentPoi = Field(playerType, "currentPointOfInterest");
        _playerWaypoints = Field(playerType, "waypoints");
        _hidden = Field(assembly.GetType(PocketSystemBindings.Poi, true)!, "hidden");
        _jumpgateOpen = Field(_jumpGateType, "jumpgateOpen");
        _guid = assembly.GetType(PocketSystemBindings.Element, true)!.GetProperty("guid", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("MapElement.guid");
        var resolved = PocketSystemBindings.Validate(assembly);
        _entrance = resolved["authoredEntrance"];
        _target = resolved["gateTarget"]; _unlock = resolved["gateUnlock"]; _lock = resolved["gateLock"];
        _removePoi = resolved["systemRemovePoi"];
        _systemName = assembly.GetType(PocketSystemBindings.Element, true)!.GetProperty("name", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("MapElement.name");
        if (_systemName.SetMethod == null) throw new MissingMethodException("MapElement.name", "set_name");
        _systemFaction = assembly.GetType(PocketSystemBindings.Element, true)!.GetProperty("faction", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("MapElement.faction");
        _galaxyRandomPosition = resolved["galaxyRandomPosition"]; _galaxyAddSector = resolved["galaxyAddSector"];
        _sectorCreate = resolved["sectorCreate"]; _sectorName = resolved["sectorName"];
        _emptyCreate = resolved["emptyCreate"]; _gatePair = resolved["gatePair"];
    }
    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(type.FullName, name);
    private object? Map(bool observed, Guid session, out object? player)
    {
        player = null;
        object? observedPlayer = null, readyPlayer = null;
        bool ok;
        if (observed) ok = _game.TryGetObservedPlayer(session, out observedPlayer);
        else ok = _game.TryGetCurrentReadyPlayer(session, out readyPlayer);
        if (!ok) return null;
        player = observed ? observedPlayer : readyPlayer;
        if (player == null) return null;
        return _map.GetValue(player);
    }
    private string Id(object element) => (string)_guid.GetValue(element)!;
    private PocketSystemInfo? ResolveFromSystem(object system)
    {
        object? entrance;
        try { entrance = _entrance.Invoke(system, null); } catch (Exception e) { ReportInvoke(e); return null; }
        if (entrance == null || !_jumpGateType.IsInstanceOfType(entrance)) return null;
        object? peer;
        try { peer = _target.Invoke(entrance, null); } catch (Exception e) { ReportInvoke(e); return null; }
        if (peer == null || !_jumpGateType.IsInstanceOfType(peer)) return null;
        return new PocketSystemInfo(Id(system), Id(entrance), Id(peer));
    }
    private void ReportInvoke(Exception error)
    {
        if (error is TargetInvocationException tie && tie.InnerException != null) _report(tie.InnerException);
        else _report(error);
    }

    /// <summary>Bounds a reconciliation pass to a single observed snapshot for its read paths (ResolvePocket/AmbiguousCount/IsOpen).</summary>
    public void BeginPass(Guid session) { _inPass = true; _passSession = session; _passSnapshot = null; }
    public void EndPass() { _inPass = false; _passSnapshot = null; }
    private WorldMapIndex.Snapshot? Snapshot(bool observed, Guid session)
    {
        bool cache = observed && _inPass && session == _passSession;
        if (cache && _passSnapshot != null) return _passSnapshot;
        var map = Map(observed, session, out _);
        if (map == null) return null;
        var snapshot = _index.Read(map);
        if (cache) _passSnapshot = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Verifies a genuinely-successful pocket creation changed membership by EXACTLY one new system
    /// (the created pocket), its pocket-side gate POI, and the one parent-side entrance gate POI — and
    /// removed nothing and adopted nothing foreign. Never accepts a no-op or any extra membership growth.
    /// </summary>
    internal static bool VerifyPocketDelta(WorldMapIndex.Snapshot before, WorldMapIndex.Snapshot after,
        object created, object parent, Func<object, bool> isGate, Func<object, object?> poiParentOf)
    {
        var beforeSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Systems) beforeSystems.Add(pair.Value);
        var afterSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Systems) afterSystems.Add(pair.Value);
        if (afterSystems.Count != beforeSystems.Count + 1 || Has(beforeSystems, created) || !Has(afterSystems, created)) return false;
        foreach (var value in beforeSystems) if (!Has(afterSystems, value)) return false; // a prior system was removed
        var beforePoints = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Points) beforePoints.Add(pair.Value);
        var afterPoints = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Points) afterPoints.Add(pair.Value);
        if (afterPoints.Count != beforePoints.Count + 2) return false;
        foreach (var value in beforePoints) if (!Has(afterPoints, value)) return false; // a prior POI was removed
        int anchorGates = 0, pocketGates = 0;
        foreach (var value in afterPoints)
        {
            if (Has(beforePoints, value)) continue;
            if (!isGate(value)) return false; // the only new POIs must be jump gates
            var poiParent = poiParentOf(value);
            if (ReferenceEquals(poiParent, parent)) anchorGates++;
            else if (ReferenceEquals(poiParent, created)) pocketGates++;
            else return false; // a new POI parented somewhere foreign
        }
        return anchorGates == 1 && pocketGates == 1;
    }
    private static bool Has(System.Collections.Generic.HashSet<object> set, object value)
    {
        foreach (var item in set) if (ReferenceEquals(item, value)) return true;
        return false;
    }

    public PocketSystemInfo? CreatePocket(Guid session, string anchorSystemId, PocketSystemPlacement placement, string? factionId, string? name)
    {
        var map = Map(false, session, out var player);
        if (map == null || player == null) return null;
        WorldMapIndex.Snapshot before;
        try { before = _index.Read(map); }
        catch (Exception e) { _report(e); return null; }
        var parent = before.FindSystem(anchorSystemId);
        if (parent == null) return null;
        // Inherit the anchor's facade owner when the consumer declared none (or an unknown faction), so
        // the authored pocket's gates always have a non-null system.faction to draw from — a null-faction
        // pocket makes its JumpGateManager NRE on init (stuck gate). Refuse (rather than author a broken
        // gate) if even the anchor system has no faction we can inherit.
        object? owner = ResolveFaction(factionId) ?? facadeFactionOf(parent);
        if (owner == null) { _report(new InvalidOperationException("Pocket has no declared faction and its anchor system has no faction to inherit; refusing to author a broken gate.")); return null; }
        try
        {
            object? created = null;
            if (placement == PocketSystemPlacement.Visible)
            {
                // Visible: a distinct system in the parent's OWN sector (renders on the settled
                // belt/galaxy map), placed well away from the parent so its dot is clearly separate,
                // and gate-linked explicitly to the parent. Same contract as OffMap: empty, sealed,
                // +1 system / +2 gates.
                var neighborSector = _sector.GetValue(parent);
                if (neighborSector == null) return null;
                if (CreateVisiblePocket(neighborSector, parent, owner, out created) == null) return null;
            }
            else
            {
                // OffMap: allocate a distant, remote sector (seeded, matching the game's own placement) and
                // place the pocket system in it — a wormhole-only door, off the settled belt/galaxy map.
                CreateRemoteSector(map, parent, owner, out created);
            }
            if (created == null || !_system_IsInstance(created)) return null;
            if (name != null) _systemName.SetValue(created, name);
            if (Map(false, session, out var current) == null || !ReferenceEquals(current, player)) return null;
            WorldMapIndex.Snapshot after;
            try { after = _index.Read(map); } catch (Exception e) { _report(e); return null; }
            if (!VerifyPocketDelta(before, after, created, parent, o => _jumpGateType.IsInstanceOfType(o), o => _parent.GetValue(o)))
                return null;
            var info = ResolveFromSystem(created);
            if (info == null) return null;
            return info;
        }
        catch (Exception e) { ReportInvoke(e); return null; }
    }

    /// <summary>Allocates a remote, sparsely-populated sector and places the pocket system in it.</summary>
    private object? CreateRemoteSector(object map, object parent, object? owner, out object? created)
    {
        created = null;
        var vector = _galaxyRandomPosition.ReturnType;                     // UnityEngine.Vector2 (resolved, never by-name)
        var exclude = Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(vector))!;
        object? pos;
        try { pos = _galaxyRandomPosition.Invoke(null, new[] { exclude, 150f, 350f, 150f, 350f, 8f }); }
        catch (Exception e) { ReportInvoke(e); return null; }
        if (pos == null) return null;
        var name = (string?)_sectorName.Invoke(null, null) ?? "The Rift";
        object? sector;
        try { sector = _sectorCreate.Invoke(null, new[] { pos, name }); }
        catch (Exception e) { ReportInvoke(e); return null; }
        if (sector == null) return null;
        try { _galaxyAddSector.Invoke(map, new[] { sector }); }
        catch (Exception e) { ReportInvoke(e); return null; }
        int level = (int)_parentLevel.GetValue(parent)! + _level;
        object? pocket;
        try { pocket = _emptyCreate.Invoke(null, new[] { sector, level, owner, pos, false }); }
        catch (Exception e) { ReportInvoke(e); return null; }
        if (pocket == null) return null;
        _pocketSystem.SetValue(pocket, true);
        // Sealed identity+traversal scaffolding only (never unlocked): keeps ResolveFromSystem /
        // VerifyPocketDelta / DissolvePocket structurally intact. The wormhole is the only usable door.
        try { _gatePair.Invoke(null, new[] { parent, pocket, false, false }); }
        catch (Exception e) { ReportInvoke(e); return null; }
        SealGates(pocket);
        created = pocket;
        return sector;
    }

    /// <summary>
    /// Puts both paired gates into the closed+hidden state at creation, matching what SetEntranceOpen(false)
    /// applies. Without this a fresh pocket's gates are closed but still VISIBLE, so the map draws them as
    /// red jumpgate lines (a phantom gate the player cannot use); reconcile cannot correct it later because
    /// IsOpen reports false for closed-and-visible, which already equals the declared closed state.
    /// </summary>
    private void SealGates(object pocket)
    {
        try
        {
            var entrance = _entrance.Invoke(pocket, null);
            if (entrance == null) return;
            var peer = _target.Invoke(entrance, null);
            foreach (var gate in new[] { entrance, peer })
            {
                if (gate == null) continue;
                _hidden.SetValue(gate, true);
                _jumpgateOpen.SetValue(gate, false);
                _lock.Invoke(gate, null);
            }
        }
        catch (Exception e) { ReportInvoke(e); }
    }

    /// <summary>Creates a distinct visible pocket system in the ANCHOR's own sector, placed well away from
    /// the parent (maximizing distance to existing systems so its dot is clearly separate on the sector map),
    /// no storyteller, and gate-linked explicitly to the parent. Mirrors the OffMap contract: +1 system / +2 gates.</summary>
    private object? CreateVisiblePocket(object neighborSector, object parent, object? owner, out object? created)
    {
        created = null;
        var vector = _galaxyRandomPosition.ReturnType; // UnityEngine.Vector2 (resolved, never by-name)
        var vx = vector.GetField("x");
        var vy = vector.GetField("y");
        var systems = _sectorSystems.GetValue(neighborSector) as System.Collections.IEnumerable;
        // Collect existing system positions in this sector.
        var existing = new System.Collections.Generic.List<object>();
        if (systems != null)
            foreach (var sys in systems)
                if (sys != null) existing.Add(_systemPosition.GetValue(sys)!);
        // Pick the local grid cell (mirroring the game's side-content scan) farthest from every existing
        // system, so the pocket renders as a clearly separate dot on the belt map.
        float winX = 0f, winY = 0f;
        float best = -1f;
        for (float gx = -33f; gx <= 33f; gx += 2f)
        {
            for (float gy = -4f; gy <= 4f; gy += 2f)
            {
                float minDist = float.MaxValue;
                foreach (var e in existing)
                {
                    float dx = gx - (float)vx.GetValue(e);
                    float dy = gy - (float)vy.GetValue(e);
                    float d = dx * dx + dy * dy;
                    if (d < minDist) minDist = d;
                }
                if (minDist > best) { best = minDist; winX = gx; winY = gy; }
            }
        }
        object pos = Activator.CreateInstance(vector)!;
        vx.SetValue(pos, winX); vy.SetValue(pos, winY);
        int level = (int)_parentLevel.GetValue(parent)! + _level;
        object? pocket;
        try { pocket = _emptyCreate.Invoke(null, new[] { neighborSector, level, owner, pos, false }); }
        catch (Exception e) { ReportInvoke(e); return null; }
        if (pocket == null) return null;
        _pocketSystem.SetValue(pocket, true);
        // Sealed identity+traversal scaffolding only (never unlocked); explicitly gate-linked to the parent.
        try { _gatePair.Invoke(null, new[] { parent, pocket, false, false }); }
        catch (Exception e) { ReportInvoke(e); return null; }
        SealGates(pocket);
        created = pocket;
        return neighborSector;
    }

    /// <summary>Resolves a known owning faction to its native object, or null (unknown owner) when the
    /// id is null/empty or the game does not know it. Guards <see cref="Faction.Get"/> which would otherwise
    /// throw for an unknown id by constructing a missing type.</summary>
    private object? ResolveFaction(string? factionId)
    {
        if (string.IsNullOrEmpty(factionId)) return null;
        try
        {
            if (_allFactions.GetValue(null) is not System.Collections.IDictionary factions || !factions.Contains(factionId))
                return null;
            return _factionGet.Invoke(null, new object[] { factionId });
        }
        catch (Exception e) { _report(e); return null; }
    }
    /// <summary>Reads the anchor system's facade owner (never null for a live system) so a pocket with no
    /// declared faction inherits one and its gates keep working.</summary>
    private object? facadeFactionOf(object system)
    {
        try { return _systemFaction.GetValue(system); }
        catch { return null; }
    }
    private bool _system_IsInstance(object value) => value.GetType().FullName == PocketSystemBindings.System;

    /// <summary>
    /// Verifies a dissolution changed membership by EXACTLY the removed pocket system, the parent-side
    /// entrance gate and the POIs parented to the pocket — and removed nothing else and added nothing.
    /// </summary>
    internal static bool VerifyDissolveDelta(WorldMapIndex.Snapshot before, WorldMapIndex.Snapshot after,
        object removedSystem, object removedEntrance, Func<object, object?> parentOf)
    {
        var beforeSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Systems) beforeSystems.Add(pair.Value);
        var afterSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Systems) afterSystems.Add(pair.Value);
        if (afterSystems.Count != beforeSystems.Count - 1 || Has(afterSystems, removedSystem)) return false;
        foreach (var value in afterSystems) if (!Has(beforeSystems, value)) return false; // a system was added
        var afterPoints = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Points) afterPoints.Add(pair.Value);
        var beforePoints = new System.Collections.Generic.HashSet<object>();
        int expectedRemoved = 0;
        foreach (var pair in before.Points)
        {
            beforePoints.Add(pair.Value);
            bool shouldGo = ReferenceEquals(pair.Value, removedEntrance) || ReferenceEquals(parentOf(pair.Value), removedSystem);
            if (shouldGo) expectedRemoved++;
            if (shouldGo == Has(afterPoints, pair.Value)) return false; // survived a removal or vanished unexpectedly
        }
        if (afterPoints.Count != beforePoints.Count - expectedRemoved) return false;
        foreach (var value in afterPoints) if (!Has(beforePoints, value)) return false; // a POI was added
        return true;
    }

    public PocketDissolveOutcome DissolvePocket(Guid session, string systemId, string entranceGateId, string pocketGateId)
    {
        var map = Map(false, session, out var player);
        if (map == null || player == null) return PocketDissolveOutcome.Failed;
        WorldMapIndex.Snapshot before;
        try { before = _index.Read(map); }
        catch (Exception e) { _report(e); return PocketDissolveOutcome.Failed; }
        var system = before.FindSystem(systemId);
        var entrance = before.FindPoint(entranceGateId);
        var peer = before.FindPoint(pocketGateId);
        if (system == null || entrance == null || peer == null
            || !_jumpGateType.IsInstanceOfType(entrance) || !_jumpGateType.IsInstanceOfType(peer)) return PocketDissolveOutcome.Missing;
        try
        {
            var parent = _parent.GetValue(entrance);
            if (parent == null || ReferenceEquals(parent, system) || !ReferenceEquals(_parent.GetValue(peer), system))
                return PocketDissolveOutcome.Missing;
            // Refuse while the player is inside the pocket or routed into it; relocation is the consumer's move.
            if (ReferenceEquals(_playerCurrentSystem.GetValue(player), system)) return PocketDissolveOutcome.PlayerInside;
            var currentPoi = _playerCurrentPoi.GetValue(player);
            if (currentPoi != null && ReferenceEquals(_parent.GetValue(currentPoi), system)) return PocketDissolveOutcome.PlayerInside;
            if (_playerWaypoints.GetValue(player) is System.Collections.IEnumerable waypoints)
                foreach (var waypoint in waypoints)
                    if (waypoint != null && (ReferenceEquals(waypoint, entrance) || ReferenceEquals(_parent.GetValue(waypoint), system)))
                        return PocketDissolveOutcome.PlayerInside;
            var sector = _sector.GetValue(system);
            if (sector == null || _sectorSystems.GetValue(sector) is not System.Collections.IList systems)
                return PocketDissolveOutcome.Missing;
            int index = -1;
            for (int i = 0; i < systems.Count; i++) if (ReferenceEquals(systems[i], system)) { index = i; break; }
            if (index < 0) return PocketDissolveOutcome.Missing;
            _removePoi.Invoke(parent, new[] { entrance });
            systems.RemoveAt(index);
            // Reclaim the remote pocket sector once its only system is gone: it existed solely to give
            // the pocket an isolated, wormhole-only home, so a leftover empty sector would be a leak.
            if (systems.Count == 0 && _galaxySectors.GetValue(map) is System.Collections.IList galaxy)
                for (int i = 0; i < galaxy.Count; i++)
                    if (ReferenceEquals(galaxy[i], sector)) { galaxy.RemoveAt(i); break; }
            if (Map(false, session, out var current) == null || !ReferenceEquals(current, player)) return PocketDissolveOutcome.Failed;
            WorldMapIndex.Snapshot after;
            try { after = _index.Read(map); }
            catch (Exception e) { _report(e); return PocketDissolveOutcome.Failed; }
            return VerifyDissolveDelta(before, after, system, entrance, o => _parent.GetValue(o))
                ? PocketDissolveOutcome.Dissolved : PocketDissolveOutcome.Failed;
        }
        catch (Exception e) { ReportInvoke(e); return PocketDissolveOutcome.Failed; }
    }

    public PocketSystemInfo? ResolvePocket(Guid session, string systemId)
    {
        var snapshot = Snapshot(true, session);
        if (snapshot == null) return null;
        try
        {
            var system = snapshot.FindSystem(systemId);
            if (system == null) return null;
            return ResolveFromSystem(system);
        }
        catch (Exception e) { _report(e); return null; }
    }

    public int AmbiguousCount(Guid session, string systemId)
    {
        var snapshot = Snapshot(true, session);
        if (snapshot == null) return 0;
        int count = 0;
        try
        {
            foreach (var pair in snapshot.Systems) if (pair.Key == systemId) count++;
        }
        catch (Exception e) { _report(e); }
        return count;
    }

    public bool ApplyOpen(Guid session, string entranceGateId, string pocketGateId, bool open)
    {
        var map = Map(false, session, out var player);
        if (map == null || player == null) return false;
        WorldMapIndex.Snapshot before;
        try { before = _index.Read(map); }
        catch (Exception e) { _report(e); return false; }
        var entrance = before.FindPoint(entranceGateId);
        var peer = before.FindPoint(pocketGateId);
        if (entrance == null || peer == null || !_jumpGateType.IsInstanceOfType(entrance) || !_jumpGateType.IsInstanceOfType(peer)) return false;
        try
        {
            // Unhide + open/close both paired gates together.
            _hidden.SetValue(entrance, !open);
            _hidden.SetValue(peer, !open);
            _jumpgateOpen.SetValue(entrance, open);
            _jumpgateOpen.SetValue(peer, open);
            if (open) _unlock.Invoke(entrance, null);
            else { _lock.Invoke(entrance, null); _lock.Invoke(peer, null); }
            Map(false, session, out var currentOk);
            return currentOk != null && ReferenceEquals(currentOk, player) && before.SameMembership(_index.Read(map));
        }
        catch (Exception e) { ReportInvoke(e); return false; }
    }

    public bool IsOpen(Guid session, string entranceGateId, string pocketGateId)
    {
        var snapshot = Snapshot(true, session);
        if (snapshot == null) return false;
        try
        {
            var entrance = snapshot.FindPoint(entranceGateId);
            var peer = snapshot.FindPoint(pocketGateId);
            if (entrance == null || peer == null) return false;
            return (bool)_jumpgateOpen.GetValue(entrance)! && !(bool)_hidden.GetValue(entrance)! &&
                (bool)_jumpgateOpen.GetValue(peer)! && !(bool)_hidden.GetValue(peer)!;
        }
        catch (Exception e) { _report(e); return false; }
    }

    /// <summary>True when both paired gates are closed AND hidden (the sealed presentation). A closed but
    /// visible gate is not sealed, so reconcile repairs pockets authored before gates were hidden at create.</summary>
    public bool IsSealed(Guid session, string entranceGateId, string pocketGateId)
    {
        var snapshot = Snapshot(true, session);
        if (snapshot == null) return false;
        try
        {
            var entrance = snapshot.FindPoint(entranceGateId);
            var peer = snapshot.FindPoint(pocketGateId);
            if (entrance == null || peer == null) return false;
            return (bool)_hidden.GetValue(entrance)! && !(bool)_jumpgateOpen.GetValue(entrance)! &&
                (bool)_hidden.GetValue(peer)! && !(bool)_jumpgateOpen.GetValue(peer)!;
        }
        catch (Exception e) { _report(e); return false; }
    }
}
