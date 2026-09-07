using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Installed-assembly evidence for every native member the travel RECOVERY/CONTINUATION
/// qualification phase reflects, plus the native call structure both of its cases depend on:
/// <list type="bullet">
/// <item>the in-system travel routine really assigns the destination POI to the player and then
/// waits for the destination manager's readiness BEFORE calling <c>SpaceshipHasArrived</c>, and the
/// native cancel leaves that assignment and that manager in place - so the readiness window the
/// recovery case samples exists in the installed build and is not a harness invention;</item>
/// <item>a cross-system route really plans [gate, follow-on] waypoints, the gate arrival hands the
/// ship to the jump routine, and the jump routine ends by continuing to the next waypoint - so the
/// post-gate leg is native behaviour and the route can only end when no waypoint remains.</item>
/// </list>
/// </summary>
[Trait("Category", "InstalledGame")]
public sealed class InstalledTravelRecoveryProbeTests
{
    private static string AssemblyPath => Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
        ?? throw new InvalidOperationException("Run make check-bindings or set VG_GAME_ASSEMBLY to the original installed Assembly-CSharp.dll.");

    private const string Travel = "Behaviour.Managers.TravelManager";
    private const string BaseManager = "Behaviour.Managers.BasePoiManager";
    private const string GateManager = "Behaviour.Travel.JumpGateManager";
    private const string Poi = "Source.Galaxy.MapPointOfInterest";
    private const string Gate = "Source.Galaxy.POI.JumpGate";
    private const string Player = "Source.Player.GamePlayer";
    private const string Map = "Source.Galaxy.GalaxyMapData";
    private const string Faction = "Source.Galaxy.Faction";
    private const string Element = "Source.Galaxy.MapElement";

    [Fact]
    public void RecoveryProbeReflectedMembersHaveInspectedShapes()
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
        void Property(string owner, string name, string type, bool isStatic = false)
        {
            var property = Assert.Single(Type(owner).Properties, candidate => candidate.Name == name);
            Assert.Equal(type, property.PropertyType.FullName);
            Assert.NotNull(property.GetMethod);
            Assert.Equal(isStatic, property.GetMethod.IsStatic);
            Assert.Empty(property.Parameters);
        }
        void Method(string owner, string name, string returnType, params string[] parameters)
        {
            var method = Assert.Single(Type(owner).Methods, candidate => candidate.Name == name
                && candidate.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters));
            Assert.Equal(returnType, method.ReturnType.FullName);
            Assert.True(method.HasBody && method.Body.Instructions.Count > 2, "Non-original body: " + owner + "." + name);
        }

        // The vanilla entry points the phase drives, exactly as a player action would.
        Method(Travel, "TryInitiateTravel", "System.Boolean", Poi);
        Method(Travel, "CanWeTravel", "System.Boolean", Poi);
        Method(Travel, "CancelTravel", "System.Boolean", "System.Nullable`1<UnityEngine.Vector2>");
        Method(Travel, "TravelActive", "System.Boolean");
        Method(Travel, "IsLocalPoiReady", "System.Boolean");
        Method(Travel, "GenerateShortestRoute", "System.Collections.Generic.List`1<" + Poi + ">", Poi);
        Method(Gate, "GetTargetPOI", Poi);
        // The read-only native state both cases sample.
        Property(Travel, "localPoiManager", BaseManager);
        Property(Travel, "usingJumpgate", "System.Boolean");
        Property(BaseManager, "poi", Poi);
        Property(BaseManager, "initializedAndReady", "System.Boolean");
        Field(Player, "waypoints", "System.Collections.Generic.List`1<" + Poi + ">");
        Field(Player, "currentPointOfInterest", Poi);
        Field(Player, "currentSystem", "Source.Galaxy.SystemMapData");
        Field(Player, "mapPosition", "UnityEngine.Vector2");
        Field(Player, "current", Player, isStatic: true);
        Property(Element, "guid", "System.String");
        Field(Element, "position", "UnityEngine.Vector2");
        Field(Element, "system", "Source.Galaxy.SystemMapData");
        Field(Gate, "targetSystemGuid", "System.String");
        Field(Gate, "targetPoiGuid", "System.String");
        Property(Gate, "targetSystem", "Source.Galaxy.SystemMapData");
        Property(Gate, "canUseJumpGate", "System.Boolean");
        // The shared safe-target selection the follow-on POI reuses in the destination system.
        Property(Map, "current", Map, isStatic: true);
        Property(Map, "allPointsOfInterest", "System.Collections.Generic.IEnumerable`1<" + Poi + ">");
        Method(Poi, "IsStoryMissionPoi", "System.Boolean");
        Method(Faction, "IsEnemy", "System.Boolean", Faction);
        Field(Poi, "hidden", "System.Boolean");
        Field(Poi, "isDynamicPoi", "System.Boolean");
        // The guard list whose COUNT the shared refusal rule reads; reading it generates nothing.
        Field(Poi, "guardDescriptors", "System.Collections.Generic.List`1<Source.Galaxy.UnitGenerationDescriptor>");
    }

    [Fact]
    public void TheRecoveryReadinessWindowExistsBetweenThePoiAssignmentAndSpaceshipHasArrived()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        // The in-system travel routine assigns the destination POI to the player, then WAITS for the
        // destination manager's readiness, and only afterwards calls SpaceshipHasArrived - the
        // boundary the adapter's arrival observation hooks. Everything between those two points is
        // the window this phase cancels in.
        var travel = Calls(module, Travel, "Travel").ToArray();
        Assert.Contains("currentPointOfInterest", Stores(module, Travel, "Travel"));
        Assert.Contains("get_localPoiManager", travel);
        Assert.Contains("get_initializedAndReady", travel);
        Assert.Contains("SpaceshipHasArrived", travel);
        Assert.Contains("TravelToNextWaypoint", travel);
        // The native cancel stops the route but never clears the assigned POI or the registered
        // manager, so the world stays at a loaded, initialized POI with no route running.
        var cancel = Calls(module, Travel, "CancelTravel");
        var cancelStores = Stores(module, Travel, "CancelTravel");
        Assert.Contains("StopCoroutine", cancel);
        Assert.Contains("Clear", cancel);
        // Store instructions only: a plain READ of currentPointOfInterest (the cancel's own guard)
        // must not be mistaken for clearing it.
        Assert.DoesNotContain("currentPointOfInterest", cancelStores);
        Assert.DoesNotContain("set_localPoiManager", cancel);
        Assert.DoesNotContain("UnloadCurrentScene", cancel);
        // Only UnloadCurrentScene clears the current POI, and it is the loaded origin's departure
        // boundary the leg passes before the window opens.
        Assert.Contains("currentPointOfInterest", Stores(module, Travel, "UnloadCurrentScene"));
        // localPoiManager is a property with a private setter, so clearing it is a setter CALL.
        Assert.Contains("set_localPoiManager", Calls(module, Travel, "UnloadCurrentScene"));
        // The manager only reports readiness at the end of its own init coroutine, which is what the
        // phase requires before cancelling (a cancel also stops a running init coroutine).
        Assert.Contains("set_initializedAndReady", Calls(module, BaseManager, "Init"));
        Assert.Contains("InitializePoi", Calls(module, BaseManager, "Init"));
        Assert.Contains("get_initCoroutine", cancel);
    }

    [Fact]
    public void ACrossSystemRoutePlansFollowOnWaypointsAndTheJumpContinuesToThem()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        // A destination outside the current system is planned as a waypoint list by the native BFS,
        // and the route starts with its FIRST waypoint - so [gate, follow-on] is native behaviour.
        var route = Calls(module, Travel, "SetRouteToPOI").ToArray();
        Assert.Contains("GenerateShortestRoute", route);
        Assert.Contains("waypoints", Stores(module, Travel, "SetRouteToPOI"));
        Assert.Contains("StartTravel", route);
        var shortest = Calls(module, Travel, "GenerateShortestRoute");
        Assert.Contains("JumpGatesInSystem", shortest);
        Assert.Contains("IsPOIInSystem", shortest);
        // The gate's own arrival hands the ship to the jump procedure when the NEXT waypoint is in
        // the gate's target system: that is what turns the approach leg into a gate hop.
        var gateArrived = Calls(module, GateManager, "SpaceshipHasArrived");
        Assert.Contains("InitiateTravelThroughGate", gateArrived);
        var initiate = Calls(module, GateManager, "InitiateTravelThroughGate");
        Assert.Contains("nextWaypointIsSystem", initiate);
        Assert.Contains("SetJumpingShip", initiate);
        // The jump routine ends by continuing the route, so a remaining waypoint really produces a
        // post-gate in-system leg instead of completing the route at the gate.
        var jump = Calls(module, Travel, "JumpToSystem").ToArray();
        Assert.Contains("currentPointOfInterest", Stores(module, Travel, "JumpToSystem"));
        Assert.Contains("get_initializedAndReady", jump);
        Assert.Contains("TravelToNextWaypoint", jump);
        Assert.Contains("set_usingJumpgate", jump);
        // The route boundary itself: only an EMPTY waypoint list clears the travel coroutine, and a
        // remaining in-system waypoint starts the next leg.
        var next = Calls(module, Travel, "TravelToNextWaypoint").ToArray();
        Assert.Contains("get_Count", next);
        Assert.Contains("IsCurrentSystem", next);
        Assert.Contains("StartTravel", next);
        Assert.Contains("travelCoroutine", Stores(module, Travel, "TravelToNextWaypoint"));
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

    // Field names a method (or its iterator) actually STORES to (stfld/stsfld). A plain read is
    // deliberately not evidence, so a negative assertion such as "the cancel never clears the
    // current POI" means what it says.
    private static HashSet<string> Stores(ModuleDefinition module, string owner, string name)
    {
        var type = module.GetType(owner) ?? throw new InvalidOperationException("Missing type: " + owner);
        var result = new HashSet<string>(StringComparer.Ordinal);
        void Collect(MethodDefinition target)
        {
            if (!target.HasBody) return;
            foreach (var instruction in target.Body.Instructions)
                if ((instruction.OpCode == Mono.Cecil.Cil.OpCodes.Stfld || instruction.OpCode == Mono.Cecil.Cil.OpCodes.Stsfld)
                    && instruction.Operand is FieldReference field)
                {
                    // Compiler-generated backing fields carry the property name in angle brackets.
                    var stored = field.Name;
                    int close = stored.IndexOf('>');
                    if (stored.StartsWith("<", StringComparison.Ordinal) && close > 1) stored = stored.Substring(1, close - 1);
                    result.Add(stored);
                }
        }
        var method = type.Methods.SingleOrDefault(candidate => candidate.Name == name);
        if (method != null) Collect(method);
        foreach (var setter in type.Methods.Where(candidate => candidate.Name == "set_" + name)) Collect(setter);
        var stateMachine = type.NestedTypes.FirstOrDefault(nested => nested.Name.StartsWith("<" + name + ">d__", StringComparison.Ordinal));
        if (stateMachine != null)
            foreach (var nested in stateMachine.Methods) Collect(nested);
        return result;
    }
}
