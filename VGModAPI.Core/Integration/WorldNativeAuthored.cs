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
internal sealed class WorldNativeAuthored : IAuthoredSystemNative
{
    private readonly GameAdapter _game;
    private readonly WorldMapIndex _index;
    private readonly Type _jumpGateType;
    private readonly PropertyInfo _map;
    private readonly FieldInfo _sector, _hidden, _jumpgateOpen;
    private readonly PropertyInfo _guid;
    private readonly MethodInfo _create, _entrance, _target, _unlock, _lock;
    private readonly int _level;
    private readonly Action<Exception> _report;

    internal WorldNativeAuthored(GameAdapter game, Assembly assembly, int level = 10, Action<Exception>? report = null)
    {
        _game = game; _level = level; _report = report ?? (_ => { });
        _index = new WorldMapIndex(assembly);
        _jumpGateType = assembly.GetType(AuthoredSystemBindings.JumpGate, true)!;
        _map = assembly.GetType("Source.Player.GamePlayer", true)!.GetProperty("map", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("GamePlayer.map");
        _sector = Field(assembly.GetType(AuthoredSystemBindings.System, true)!, "sector");
        _hidden = Field(assembly.GetType(AuthoredSystemBindings.Poi, true)!, "hidden");
        _jumpgateOpen = Field(_jumpGateType, "jumpgateOpen");
        _guid = assembly.GetType(AuthoredSystemBindings.Element, true)!.GetProperty("guid", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("MapElement.guid");
        var resolved = AuthoredSystemBindings.Validate(assembly);
        _create = resolved["authoredCreate"]; _entrance = resolved["authoredEntrance"];
        _target = resolved["gateTarget"]; _unlock = resolved["gateUnlock"]; _lock = resolved["gateLock"];
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
    private AuthoredSystemPocketInfo? ResolveFromSystem(object system)
    {
        object? entrance;
        try { entrance = _entrance.Invoke(system, null); } catch (Exception e) { ReportInvoke(e); return null; }
        if (entrance == null || !_jumpGateType.IsInstanceOfType(entrance)) return null;
        object? peer;
        try { peer = _target.Invoke(entrance, null); } catch (Exception e) { ReportInvoke(e); return null; }
        if (peer == null || !_jumpGateType.IsInstanceOfType(peer)) return null;
        return new AuthoredSystemPocketInfo(Id(system), Id(entrance), Id(peer));
    }
    private void ReportInvoke(Exception error)
    {
        if (error is TargetInvocationException tie && tie.InnerException != null) _report(tie.InnerException);
        else _report(error);
    }

    public AuthoredSystemPocketInfo? CreatePocket(Guid session, string anchorSystemId)
    {
        var map = Map(false, session, out var player);
        if (map == null || player == null) return null;
        WorldMapIndex.Snapshot before;
        try { before = _index.Read(map); }
        catch (Exception e) { _report(e); return null; }
        var anchor = before.FindSystem(anchorSystemId);
        if (anchor == null) return null;
        var sector = _sector.GetValue(anchor);
        if (sector == null) return null;
        try
        {
            var created = _create.Invoke(null, new[] { sector, anchor, _level });
            if (created == null || !_system_IsInstance(created)) return null;
            if (Map(false, session, out var current) == null || !ReferenceEquals(current, player)) return null;
            WorldMapIndex.Snapshot after;
            try { after = _index.Read(map); } catch (Exception e) { _report(e); return null; }
            if (!before.SameMembership(after)) return null;
            var info = ResolveFromSystem(created);
            if (info == null) return null;
            return info;
        }
        catch (Exception e) { ReportInvoke(e); return null; }
    }
    private bool _system_IsInstance(object value) => value.GetType().FullName == AuthoredSystemBindings.System;

    public AuthoredSystemPocketInfo? ResolvePocket(Guid session, string systemId)
    {
        var map = Map(true, session, out _);
        if (map == null) return null;
        try
        {
            var system = _index.Read(map).FindSystem(systemId);
            if (system == null) return null;
            return ResolveFromSystem(system);
        }
        catch (Exception e) { _report(e); return null; }
    }

    public int AmbiguousCount(Guid session, string systemId)
    {
        var map = Map(true, session, out _);
        if (map == null) return 0;
        int count = 0;
        try
        {
            foreach (var pair in _index.Read(map).Systems) if (pair.Key == systemId) count++;
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
        var map = Map(true, session, out _);
        if (map == null) return false;
        try
        {
            var snapshot = _index.Read(map);
            var entrance = snapshot.FindPoint(entranceGateId);
            var peer = snapshot.FindPoint(pocketGateId);
            if (entrance == null || peer == null) return false;
            return (bool)_jumpgateOpen.GetValue(entrance)! && !(bool)_hidden.GetValue(entrance)! &&
                (bool)_jumpgateOpen.GetValue(peer)! && !(bool)_hidden.GetValue(peer)!;
        }
        catch (Exception e) { _report(e); return false; }
    }
}
