using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Installed-assembly evidence for every native member the travel RESILIENCE qualification phase
/// reflects, plus the native call structure that phase depends on: the empty-origin departure
/// boundary, the restore/relink/re-init assignments that must carry NO docking-request intent (and
/// the different-docking-size branch that still takes a real <c>Dock()</c> coroutine), and the
/// dock/undock coroutine boundary the stale-session replay captures.
/// </summary>
[Trait("Category", "InstalledGame")]
public sealed class InstalledTravelResilienceProbeTests
{
    private static string AssemblyPath => Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
        ?? throw new InvalidOperationException("Run make check-bindings or set VG_GAME_ASSEMBLY to the original installed Assembly-CSharp.dll.");

    private const string Travel = "Behaviour.Managers.TravelManager";
    private const string Exterior = "SpacestationExteriorManager";
    private const string Interior = "Behaviour.UI.Spacestation.SpaceStationInterior";
    private const string Option = "Behaviour.Spacestation.Docking.DockingOption";
    private const string Ship = "Behaviour.Unit.SpaceShip";
    private const string ShipData = "Source.SpaceShip.SpaceShipData";
    private const string RoleType = "Source.SpaceShip.SpaceShipRoleType";
    private const string OptionSize = "Behaviour.Spacestation.Docking.DockingOptionSize";
    private const string Player = "Source.Player.GamePlayer";
    private const string Hud = "Behaviour.UI.HUD.HudManager";

    [Fact]
    public void ResilienceProbeReflectedMembersHaveInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        TypeDefinition Type(string name) => module.GetType(name) ?? throw new InvalidOperationException("Missing type: " + name);
        void Field(string owner, string name, string type, bool isStatic = false)
        {
            var field = Assert.Single(Type(owner).Fields, candidate => candidate.Name == name);
            Assert.Equal(type, field.FieldType.FullName);
            Assert.Equal(isStatic, field.IsStatic);
        }
        void Property(string owner, string name, string type)
        {
            var property = Assert.Single(Type(owner).Properties, candidate => candidate.Name == name);
            Assert.Equal(type, property.PropertyType.FullName);
            Assert.NotNull(property.GetMethod);
            Assert.False(property.GetMethod.IsStatic);
            Assert.Empty(property.Parameters);
        }
        MethodDefinition Method(string owner, string name, string returnType, params string[] parameters)
        {
            var method = Assert.Single(Type(owner).Methods, candidate => candidate.Name == name
                && candidate.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters));
            Assert.False(method.IsStatic);
            Assert.Equal(returnType, method.ReturnType.FullName);
            Assert.True(method.HasBody && method.Body.Instructions.Count > 2, "Non-original body: " + owner + "." + name);
            return method;
        }

        // Empty-origin re-route: the entry points it drives and the state it samples to prove the
        // departure was observed at the ACTUAL native warp loop and not during preparation.
        Method(Travel, "TryInitiateTravel", "System.Boolean", "Source.Galaxy.MapPointOfInterest");
        Method(Travel, "CancelTravel", "System.Boolean", "System.Nullable`1<UnityEngine.Vector2>");
        Method(Travel, "TravelInSystem", "System.Collections.IEnumerator");
        Method(Travel, "UnloadCurrentScene", "System.Void");
        Property(Travel, "isWarping", "System.Boolean");
        Assert.True(Assert.Single(Type(Travel).Properties, property => property.Name == "isWarping").SetMethod?.IsPrivate);

        // Restore/relink/re-init: the native entry points the case drives and the members it reads
        // to prove which docking-size branch actually ran.
        Method(Ship, "InitSpacestationAutoActions", "System.Void");
        Assert.Single(Type(Ship).Methods, candidate => candidate.Name == "RelinkDockedShipToStation" && candidate.IsPrivate);
        Method("GameplayManager", "ReinitPlayerSpaceship", "System.Void");
        Method(Player, "SetSpaceShipData", "System.Void", ShipData);
        Method(RoleType, "GetDockingOptionSize", OptionSize);
        Property(Ship, "shipRoleType", RoleType);
        Field(ShipData, "shipClass", Ship);
        Field(ShipData, "awayForRepairs", "System.Boolean");
        Field(ShipData, "boardingSimulation", "Source.Dungeon.DungeonSimulation");
        Assert.Single(Type(ShipData).Properties, property => property.Name == "IsCarrierAssigned"
            && property.PropertyType.FullName == "System.Boolean");
        Field(Player, "spaceShips", "System.Collections.Generic.List`1<" + ShipData + ">");
        Field(Player, "activeFleet", "System.Collections.Generic.List`1<" + ShipData + ">");
        Field(Exterior, "dockingOptions", "System.Collections.Generic.List`1<" + Option + ">");
        Field(Exterior, "currentDockingOption", Option);
        Property(Option, "dockingOptionSize", OptionSize);
        Property(Option, "occupied", "System.Boolean");
        Field(Option, "dockingCoroutine", "UnityEngine.Coroutine");
        Assert.Equal(Option, Type("Behaviour.Spacestation.Docking.DockingTunnel").BaseType.FullName);
        // The genuine docking request the case uses as its positive control is the HUD dock button.
        Field(Hud, "Instance", Hud, isStatic: true);
        Method(Hud, "Dock", "System.Void");

        // Stale-session replay: the exact declared coroutine factory the phase captures. It is the
        // same declaration the API patches, so the replay is the real hooked boundary.
        var undock = Assert.Single(Type(Option).Methods, candidate => candidate.Name == "Undock");
        Assert.True(undock.IsFamily && !undock.IsStatic);
        Assert.Equal("System.Collections.IEnumerator", undock.ReturnType.FullName);
        Assert.Empty(undock.Parameters);
        Method(Exterior, "GetDockingOption", Option, Ship);
        Method(Interior, "ExitSpacestation", "System.Void");
        Method(Exterior, "StartUndocking", "System.Void");
    }

    [Fact]
    public void RestoreAndRelinkAssignmentsStillHappenOutsideAnyDockingRequest()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        // The load restore, the relink and the ship re-init all reach the real assignment, and none
        // of them goes through CheckForDocking: that is exactly why the adapter must record no
        // docking-request intent for them and emit nothing, even when a real Dock() coroutine runs.
        var initializePoi = Calls(module, Exterior, "InitializePoi");
        Assert.Contains("AssignClosestDockingOption", initializePoi);
        Assert.DoesNotContain("CheckForDocking", initializePoi);
        Assert.Contains("AssignSpaceshipForDocking", Calls(module, Exterior, "AssignClosestDockingOption"));
        var relink = Calls(module, Ship, "RelinkDockedShipToStation");
        Assert.Contains("FindDockingOptionAtPosition", relink);
        Assert.Contains("AssignSpaceshipForDocking", relink);
        Assert.DoesNotContain("CheckForDocking", relink);
        Assert.Contains("RelinkDockedShipToStation", Calls(module, Ship, "InitSpacestationAutoActions"));
        var reinit = Calls(module, "GameplayManager", "ReinitPlayerSpaceshipRoutine");
        Assert.Contains("AssignSpaceshipForDocking", reinit);
        Assert.Contains("FindClosestDockingOption", reinit);
        Assert.Contains("GetDockingOptionSize", reinit);
        Assert.Contains("ResetDockingOption", reinit);
        Assert.DoesNotContain("CheckForDocking", reinit);
        // The different-docking-size branch is what the receipt proves from the option identity:
        // the routine assigns the exterior manager's current option to the newly found one.
        Assert.Contains("set_currentDockingOption", Fields(module, "GameplayManager", "ReinitPlayerSpaceshipRoutine"));
        Assert.Contains("ReinitPlayerSpaceshipRoutine", Calls(module, "GameplayManager", "ReinitPlayerSpaceship"));
        // Only the skipCoroutine argument decides whether a real (hooked) Dock() coroutine exists,
        // so the same-size branch cannot produce one and the different-size branch can.
        var perform = Calls(module, Option, "PerformDocking");
        Assert.Contains("DockQuick", perform);
        Assert.Contains("Dock", perform);
        Assert.Contains("PerformDocking", Calls(module, Option, "AssignSpaceshipForDocking"));
        // The positive control really is the native docking-request path.
        Assert.Contains("CheckForDocking", Calls(module, Hud, "Dock"));
        Assert.Contains("AssignClosestDockingOption", Calls(module, Exterior, "CheckForDocking"));
    }

    [Fact]
    public void EmptyOriginDepartureAndStaleUndockBoundariesAreStillTheInspectedOnes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        // A LOADED origin's dwell ends at the verified UnloadCurrentScene transition, which the
        // in-system travel loop triggers itself and which really clears the player's current POI.
        var unload = Calls(module, Travel, "UnloadCurrentScene").Concat(Fields(module, Travel, "UnloadCurrentScene")).ToArray();
        Assert.Contains("set_currentPointOfInterest", unload);
        Assert.Contains("set_localPoiManager", unload);
        Assert.Contains("UnloadCurrentScene", Calls(module, Travel, "CheckLocalPoiStatus"));
        // The re-routed leg can no longer depart that way (the origin is already unloaded), so its
        // departure comes from the actual transport loop, which runs only after departure
        // preparation and is the routine the adapter observes.
        var travelInSystem = Calls(module, Travel, "TravelInSystem").Concat(Fields(module, Travel, "TravelInSystem")).ToArray();
        Assert.Contains("set_isWarping", travelInSystem);
        Assert.Contains("set_delayTravelAttempt", travelInSystem);
        Assert.Contains("CheckLocalPoiStatus", travelInSystem);
        var startTravel = Calls(module, Travel, "StartTravel");
        Assert.Contains("PrepareAllForInSystemTravel", startTravel);
        Assert.Contains("Travel", startTravel);
        Assert.Contains("TravelInSystem", Calls(module, Travel, "Travel"));
        // Cancelling an already-departed leg must not put the ship back: the native cancel only
        // restores a position when a current POI still exists.
        var cancel = Calls(module, Travel, "CancelTravel");
        Assert.Contains("CancelJumpAway", cancel);
        Assert.Contains("Clear", cancel);
        // The captured stale coroutine: vanilla itself re-checks its ship before the tail that
        // would touch the world, and its ResetDockingOption is inside that guarded tail.
        var undock = Calls(module, Option, "Undock");
        Assert.Contains("UndockingProcedure", undock);
        Assert.Contains("ResetDockingOption", undock);
        Assert.Contains("op_Implicit", undock); // the Unity liveness check on the destroyed old ship
        // The undock procedures the replay steps through are the inspected ones; the pad exits
        // immediately when its ship is gone, and the hangar yields once.
        Assert.Contains("op_Equality", Calls(module, "Behaviour.Spacestation.Docking.DockingPad", "UndockingProcedure"));
    }

    // Called member names of a method, including the MoveNext of its compiler-generated iterator.
    private static HashSet<string> Calls(ModuleDefinition module, string owner, string name)
    {
        var type = module.GetType(owner) ?? throw new InvalidOperationException("Missing type: " + owner);
        var method = type.Methods.SingleOrDefault(candidate => candidate.Name == name)
            ?? throw new InvalidOperationException("Missing or overloaded method: " + owner + "." + name);
        var result = new HashSet<string>(StringComparer.Ordinal);
        void Collect(MethodDefinition target)
        {
            if (!target.HasBody) return;
            foreach (var instruction in target.Body.Instructions)
                if (instruction.Operand is MethodReference call) result.Add(call.Name);
        }
        Collect(method);
        foreach (var lambda in type.Methods.Where(candidate => candidate.Name.StartsWith("<" + name + ">b__", StringComparison.Ordinal))) Collect(lambda);
        var stateMachine = type.NestedTypes.FirstOrDefault(nested => nested.Name.StartsWith("<" + name + ">d__", StringComparison.Ordinal));
        if (stateMachine != null)
        {
            foreach (var nested in stateMachine.Methods) Collect(nested);
            foreach (var lambda in stateMachine.NestedTypes.SelectMany(nested => nested.Methods)) Collect(lambda);
        }
        return result;
    }

    // Stored field names a method (or its iterator) writes, so a plain field assignment such as
    // GamePlayer.currentPointOfInterest is evidence too.
    private static HashSet<string> Fields(ModuleDefinition module, string owner, string name)
    {
        var type = module.GetType(owner) ?? throw new InvalidOperationException("Missing type: " + owner);
        var result = new HashSet<string>(StringComparer.Ordinal);
        void Collect(MethodDefinition target)
        {
            if (!target.HasBody) return;
            foreach (var instruction in target.Body.Instructions)
                if (instruction.Operand is FieldReference field) result.Add("set_" + field.Name);
        }
        var method = type.Methods.SingleOrDefault(candidate => candidate.Name == name);
        if (method != null) Collect(method);
        var stateMachine = type.NestedTypes.FirstOrDefault(nested => nested.Name.StartsWith("<" + name + ">d__", StringComparison.Ordinal));
        if (stateMachine != null)
            foreach (var nested in stateMachine.Methods) Collect(nested);
        return result;
    }
}
