using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Installed-assembly evidence for every native member the travel/station qualification probe
/// reflects, plus the native call structure the phase relies on: the probe must never depend on a
/// member or a native drive path that the inspected build does not actually have.
/// </summary>
[Trait("Category", "InstalledGame")]
public sealed class InstalledTravelStationProbeTests
{
    private static string AssemblyPath => Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
        ?? throw new InvalidOperationException("Run make check-bindings or set VG_GAME_ASSEMBLY to the original installed Assembly-CSharp.dll.");

    [Fact]
    public void ProbeReflectedMembersHaveInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        TypeDefinition Type(string name) => module.GetType(name) ?? throw new InvalidOperationException("Missing type: " + name);
        void Field(string owner, string name, string type, bool isStatic)
        {
            var field = Assert.Single(Type(owner).Fields, candidate => candidate.Name == name);
            Assert.Equal(type, field.FieldType.FullName);
            Assert.Equal(isStatic, field.IsStatic);
        }
        void Property(string owner, string name, string type, bool isStatic = false)
        {
            var property = Assert.Single(Type(owner).Properties, candidate => candidate.Name == name);
            Assert.Equal(type, property.PropertyType.FullName);
            Assert.NotNull(property.GetMethod);
            Assert.Equal(isStatic, property.GetMethod.IsStatic);
            Assert.Empty(property.Parameters);
        }
        MethodDefinition Method(string owner, string name, string returnType, params string[] parameters)
        {
            var method = Assert.Single(Type(owner).Methods, candidate => candidate.Name == name
                && candidate.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters));
            Assert.False(method.IsStatic);
            Assert.Equal(returnType, method.ReturnType.FullName);
            Assert.True(method.HasBody && method.Body.Instructions.Count > 2, "Non-original body: " + owner + "." + name);
            return method;
        }
        const string travel = "Behaviour.Managers.TravelManager";
        const string poi = "Source.Galaxy.MapPointOfInterest";
        const string exterior = "SpacestationExteriorManager";
        const string interior = "Behaviour.UI.Spacestation.SpaceStationInterior";
        const string dockingOption = "Behaviour.Spacestation.Docking.DockingOption";
        const string ship = "Behaviour.Unit.SpaceShip";
        const string player = "Source.Player.GamePlayer";

        // Travel entry points the probe drives, bound by exact signature (not by bare name).
        Method(travel, "TryInitiateTravel", "System.Boolean", poi);
        Method(travel, "CanWeTravel", "System.Boolean", poi);
        Method(travel, "CancelTravel", "System.Boolean", "System.Nullable`1<UnityEngine.Vector2>");
        Method(travel, "TravelActive", "System.Boolean");
        Method(travel, "IsLocalPoiReady", "System.Boolean");
        Method(travel, "SetRouteToPOI", "System.Boolean", poi);
        Method(travel, "TravelToNextWaypoint", "System.Void");
        Assert.Equal("Behaviour.Util.Singleton`1<Behaviour.Managers.TravelManager>", Type(travel).BaseType.FullName);
        Assert.Contains(module.GetType("Behaviour.Util.Singleton`1").Properties, p => p.Name == "Instance" && p.GetMethod.IsStatic);

        // Station entry points and the state the probe reads to confirm physical dock/undock.
        Property(exterior, "Instance", exterior, isStatic: true);
        Property(exterior, "undockingRoutine", "UnityEngine.Coroutine");
        Method(exterior, "StartUndocking", "System.Void");
        Method(exterior, "GetDockingOption", dockingOption, ship);
        Method(exterior, "CheckForDocking", "System.Void");
        Field(interior, "instance", interior, isStatic: true);
        Method(interior, "ExitSpacestation", "System.Void");
        Property(dockingOption, "dockingSpaceship", ship);
        Property(ship, "spaceShipData", "Source.SpaceShip.SpaceShipData");
        Field("Source.SpaceShip.SpaceShipData", "dockingState", "System.Nullable`1<Source.SpaceShip.Auto.DockingState>", isStatic: false);
        // The probe compares docking state by inspected enum NAME, so the names must exist.
        var states = Type("Source.SpaceShip.Auto.DockingState").Fields.Where(f => f.IsStatic).Select(f => f.Name).ToArray();
        Assert.Contains("Docked", states);
        Assert.Contains("Leaving", states);
        Method("Source.Galaxy.POI.SpaceStation", "PlayerIsFriendly", "System.Boolean");

        // Fixture inspection/target selection members.
        Field(player, "current", player, isStatic: true);
        Field(player, "currentPointOfInterest", poi, isStatic: false);
        Field(player, "currentSystem", "Source.Galaxy.SystemMapData", isStatic: false);
        Field(player, "waypoints", "System.Collections.Generic.List`1<" + poi + ">", isStatic: false);
        Field(player, "mapPosition", "UnityEngine.Vector2", isStatic: false);
        Field(poi, "hidden", "System.Boolean", isStatic: false);
        Field(poi, "isDynamicPoi", "System.Boolean", isStatic: false);
        Field("Source.Galaxy.MapElement", "position", "UnityEngine.Vector2", isStatic: false);
        Field("Source.Galaxy.MapElement", "system", "Source.Galaxy.SystemMapData", isStatic: false);
        Property("Source.Galaxy.MapElement", "guid", "System.String");
        Property("Source.Galaxy.GalaxyMapData", "current", "Source.Galaxy.GalaxyMapData", isStatic: true);
        Assert.Contains(Type("Source.Galaxy.GalaxyMapData").Properties.Concat<IMemberDefinition>(Type("Source.Galaxy.GalaxyMapData").Fields),
            member => member.Name == "allPointsOfInterest");
        Field("GameplayManager", "Instance", "GameplayManager", isStatic: true);
        Property("GameplayManager", "spaceShip", ship);
        Assert.NotNull(Type("Source.Galaxy.POI.JumpGate"));
        Assert.NotNull(Type("Source.Galaxy.POI.Wormhole"));
        Property("Behaviour.Managers.BasePoiManager", "poi", poi);
        Property("Behaviour.Managers.BasePoiManager", "initializedAndReady", "System.Boolean");

        // Authoritative safe-target selection (Plugin.SafeInSystemTargets). The allowlist is by
        // native POI type; the ownership and story checks are the game's own read-only members.
        // MapPointOfInterest.activeEnemyCount / totalEnemyCount are deliberately NOT reflected,
        // because their getters generate native content.
        foreach (var industrial in new[] { "Source.Galaxy.POI.Mining", "Source.Galaxy.POI.Salvage" })
            Assert.Equal(poi, Type(industrial).BaseType.FullName);
        Assert.Equal(poi, Type("Source.Galaxy.POI.Combat").BaseType.FullName);
        foreach (var encounter in new[] { "Source.Galaxy.POI.CombatStation", "Source.Galaxy.POI.Escort", "Source.Galaxy.POI.LureSite" })
            Assert.Equal("Source.Galaxy.POI.Combat", Type(encounter).BaseType.FullName);
        Method(poi, "IsStoryMissionPoi", "System.Boolean");
        Assert.Single(Type("Source.Galaxy.MapElement").Properties, property => property.Name == "faction"
            && property.PropertyType.FullName == "Source.Galaxy.Faction" && property.GetMethod?.IsVirtual == true);
        Method("Source.Galaxy.Faction", "IsEnemy", "System.Boolean", "Source.Galaxy.Faction");
        Field("Source.Galaxy.Faction", "player", "Source.Galaxy.Faction", isStatic: true);

        // The native autonomy diagnostic recorded with an unsolicited-route failure.
        Field(player, "emergencyJump", "System.Boolean", isStatic: false);
        Field(player, "autoPlay", "System.Boolean", isStatic: false);
        Property(player, "currentSpaceShip", "Source.SpaceShip.SpaceShipData");
        foreach (var stat in new[] { "currentHullHP", "maxHullHP", "currentShieldHP", "maxShieldHP" })
            Field("Source.Data.AbstractUnitData", stat, "System.Single", isStatic: false);
        Property(travel, "targetPoi", poi);
        Property(travel, "localTarget", poi);
        Property(travel, "isWarping", "System.Boolean");
    }

    [Fact]
    public void ACombatPoiTargetCanStartAnUnsolicitedNativeReturnRouteSoItIsNotASafeTarget()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        // qa-82's root cause, pinned in the inspected assembly: a destroyed player hull runs the
        // native emergency jump, which starts a route to the closest station WITHOUT any player or
        // pilot request. That is why the in-system target selection must never pick a native combat
        // encounter, and why an unsolicited route fails a case instead of being waited out.
        var emergency = Calls(module, "Behaviour.Unit.SpaceShip", "TryEmergencyJump");
        Assert.Contains("TravelToClosestSpacestation", emergency);
        Assert.Contains("emergencyJump", FieldRefs(module, "Behaviour.Unit.SpaceShip", "TryEmergencyJump"));
        Assert.Contains("TryEmergencyJump", Calls(module, "Behaviour.Unit.AbstractUnit", "TakeDamage"));
        var closest = Calls(module, "Behaviour.Managers.TravelManager", "TravelToClosestSpacestation");
        Assert.Contains("SetRouteToPOI", closest);
        Assert.Contains("FindClosestStation", closest);
        Assert.Contains("PlayerIsFriendly", closest);
        // The only other autonomous route source is the autopilot, which is gated on GamePlayer.autoPlay.
        Assert.Contains("autoPlay", FieldRefs(module, "Behaviour.Gameplay.IdleManager", "Update"));
        // The safe-target members must not be the content-generating counters.
        // activeEnemyCount generates the POI's content; totalEnemyCount reaches it through that getter.
        Assert.Contains("EnsureContentGenerated", Calls(module, "Source.Galaxy.MapPointOfInterest", "get_activeEnemyCount"));
        Assert.Contains("get_activeEnemyCount", Calls(module, "Source.Galaxy.MapPointOfInterest", "get_totalEnemyCount"));
        foreach (var forbidden in new[] { "EnsureContentGenerated", "RegenerateGuardUnits" })
            Assert.DoesNotContain(forbidden, Calls(module, "Source.Galaxy.MapPointOfInterest", "IsStoryMissionPoi"));
    }

    // Field names a method references, so a plain field read or write (GamePlayer.autoPlay,
    // GamePlayer.emergencyJump) is evidence too.
    private static HashSet<string> FieldRefs(ModuleDefinition module, string owner, string name)
    {
        var type = module.GetType(owner) ?? throw new InvalidOperationException("Missing type: " + owner);
        var result = new HashSet<string>(StringComparer.Ordinal);
        var method = type.Methods.SingleOrDefault(candidate => candidate.Name == name);
        if (method?.HasBody == true)
            foreach (var instruction in method.Body.Instructions)
                if (instruction.Operand is FieldReference field) result.Add(field.Name);
        return result;
    }

    [Fact]
    public void NativeCodeStillDrivesTheChainedRouteAndTheArrivalDock()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        // The probe only sets up waypoints and lets native code advance the chain: the arrival must
        // still remove the reached waypoint, report the arrival and start the next hop itself.
        var travelCalls = Calls(module, "Behaviour.Managers.TravelManager", "Travel");
        Assert.Contains("SpaceshipHasArrived", travelCalls);
        Assert.Contains("TravelToNextWaypoint", travelCalls);
        Assert.Contains("Remove", travelCalls);
        Assert.Contains("StartTravel", Calls(module, "Behaviour.Managers.TravelManager", "TravelToNextWaypoint"));
        Assert.Contains("IsCurrentSystem", Calls(module, "Behaviour.Managers.TravelManager", "TravelToNextWaypoint"));
        // Docking on arrival is native, not driven by the probe.
        Assert.Contains("CheckForDocking", Calls(module, "SpacestationExteriorManager", "SpaceshipHasArrived"));
        Assert.Contains("AssignClosestDockingOption", Calls(module, "SpacestationExteriorManager", "CheckForDocking"));
        // The undock the probe triggers runs the real DockingOption.Undock coroutine.
        Assert.Contains("StartUndocking", Calls(module, "Behaviour.UI.Spacestation.SpaceStationInterior", "ExitSpacestation"));
        Assert.Contains("UndockSpaceship", Calls(module, "SpacestationExteriorManager", "StartUndocking"));
        Assert.Contains("AssignSpaceshipForUnDocking", Calls(module, "SpacestationExteriorManager", "UndockSpaceship"));
        Assert.Contains("Undock", Calls(module, "Behaviour.Spacestation.Docking.DockingOption", "AssignSpaceshipForUnDocking"));
        Assert.Contains("ResetDockingOption", Calls(module, "Behaviour.Spacestation.Docking.DockingOption", "Undock"));
    }

    // Called member names of a method, including the MoveNext of its compiler-generated iterator.
    private static HashSet<string> Calls(ModuleDefinition module, string owner, string name)
    {
        var type = module.GetType(owner) ?? throw new InvalidOperationException("Missing type: " + owner);
        var method = type.Methods.SingleOrDefault(m => m.Name == name)
            ?? throw new InvalidOperationException("Missing or overloaded method: " + owner + "." + name);
        var result = new HashSet<string>(StringComparer.Ordinal);
        void Collect(MethodDefinition target)
        {
            if (!target.HasBody) return;
            foreach (var instruction in target.Body.Instructions)
                if (instruction.Operand is MethodReference call) result.Add(call.Name);
        }
        Collect(method);
        var stateMachine = type.NestedTypes.FirstOrDefault(nested => nested.Name.StartsWith("<" + name + ">d__", StringComparison.Ordinal));
        if (stateMachine != null)
        {
            var moveNext = stateMachine.Methods.SingleOrDefault(m => m.Name == "MoveNext");
            if (moveNext != null) Collect(moveNext);
            foreach (var lambda in stateMachine.NestedTypes.SelectMany(nested => nested.Methods)) Collect(lambda);
        }
        return result;
    }
}
