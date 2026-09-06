using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Installed-assembly evidence for every native member the CROSS-SYSTEM qualification phase
/// reflects, plus the native call structure that phase depends on: the two jump routines must
/// still be the owned iterators that assign the destination and wait for its manager, and they
/// must still never call <c>SpaceshipHasArrived</c> — that is exactly why the phase requires its
/// arrival to be observed inside the running jump iterator.
/// </summary>
[Trait("Category", "InstalledGame")]
public sealed class InstalledTravelCrossSystemProbeTests
{
    private static string AssemblyPath => Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
        ?? throw new InvalidOperationException("Run make check-bindings or set VG_GAME_ASSEMBLY to the original installed Assembly-CSharp.dll.");

    private const string Travel = "Behaviour.Managers.TravelManager";
    private const string Poi = "Source.Galaxy.MapPointOfInterest";
    private const string JumpGate = "Source.Galaxy.POI.JumpGate";
    private const string Wormhole = "Source.Galaxy.POI.Wormhole";
    private const string GateManager = "Behaviour.Travel.JumpGateManager";
    private const string WormholeManager = "Behaviour.Travel.WormholeManager";
    private const string Player = "Source.Player.GamePlayer";

    [Fact]
    public void CrossSystemProbeReflectedMembersHaveInspectedShapes()
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

        // The read-only native route planner the phase uses to select a hop without driving it.
        Method(Travel, "GenerateShortestRoute", "System.Collections.Generic.List`1<" + Poi + ">", Poi);
        // The native jump entry points and the flag the phase samples to prove the hop was observed
        // from inside the running jump routine.
        Method(Travel, "JumpToPOIFrom", "System.Void", JumpGate);
        Method(Travel, "JumpToWormholeFrom", "System.Void", Wormhole);
        Property(Travel, "usingJumpgate", "System.Boolean");
        Assert.True(Assert.Single(Type(Travel).Properties, property => property.Name == "usingJumpgate").SetMethod?.IsPrivate);

        // Gate/wormhole selection and the RAW request the adapter must preserve.
        Field(JumpGate, "targetSystemGuid", "System.String");
        Field(JumpGate, "targetPoiGuid", "System.String");
        Property(JumpGate, "canUseJumpGate", "System.Boolean");
        Property(JumpGate, "targetSystem", "Source.Galaxy.SystemMapData");
        Method(JumpGate, "GetTargetPOI", Poi);
        Property(Wormhole, "canUseWormhole", "System.Boolean");
        Method(Wormhole, "GetConnectedWormholes", "System.Collections.Generic.List`1<" + Wormhole + ">");
        Field(Wormhole, "discovered", "System.Boolean");
        Property(Player, "wormholesUnlocked", "System.Boolean");
        // The stored name field, read directly so the lazy name getter cannot generate a name (and
        // consume world randomness) while the phase excludes the one-way tutorial exit gate.
        Field("Source.Galaxy.MapElement", "_name", "System.String");

        // The opt-in disposable fixture preparation: the ONLY native content factory the phase may
        // call, pinned by its exact declared static signature, plus the map members it reads to
        // choose an actual other native system and the list the factory itself assigns.
        var factory = Assert.Single(Type("Source.Simulation.World.WormholeSpawner").Methods,
            candidate => candidate.Name == "PlaceWormhole");
        Assert.True(factory.IsStatic && factory.IsPublic);
        Assert.Equal(Wormhole, factory.ReturnType.FullName);
        Assert.Equal(new[] { "Source.Galaxy.SystemMapData", "System.Boolean", "System.Collections.Generic.List`1<" + Wormhole + ">" },
            factory.Parameters.Select(parameter => parameter.ParameterType.FullName).ToArray());
        Field(Wormhole, "targetWormholeGuids", "System.Collections.Generic.List`1<System.String>");
        Method(Wormhole, "CanConnectTo", "System.Boolean", Wormhole);
        Field("Source.Galaxy.SystemMapData", "pointsOfInterest", "System.Collections.Generic.List`1<" + Poi + ">");
        Field("Source.Galaxy.SystemMapData", "pocketSystem", "System.Boolean");
        Field("Source.Galaxy.SystemMapData", "sector", "Source.Galaxy.SectorMapData");
        Property("Source.Galaxy.SystemMapData", "mapPosition", "UnityEngine.Vector2");
        // Destination restriction: the same native map quadrant and a sector the player can already
        // reach. Both are plain native members, never the lazy name generator.
        Field("Source.Galaxy.SectorMapData", "quadrant", "System.Int32");
        Method("Source.Galaxy.SectorMapData", "IsUnlocked", "System.Boolean");
        Assert.Single(Type("Source.Galaxy.GalaxyMapData").Properties, property => property.Name == "allSystems"
            && property.PropertyType.FullName == "System.Collections.Generic.IEnumerable`1<Source.Galaxy.SystemMapData>");

        // The destination managers the phase requires at a cross-system arrival.
        Assert.Equal("Behaviour.Managers.BasePoiManager", Type(GateManager).BaseType.FullName);
        Assert.Equal("Behaviour.Managers.BasePoiManager", Type(WormholeManager).BaseType.FullName);
        Method(GateManager, "InitiateTravelThroughGate", "System.Void");
        Method(WormholeManager, "InitiateTravelThroughWormhole", "System.Void");
    }

    [Fact]
    public void NativeJumpRoutinesStillOwnTheCrossSystemArrivalAndNeverReportItAsAnInSystemArrival()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        // The jump routines assign the destination themselves, wait for the destination manager and
        // close the route through TravelToNextWaypoint. If any of them ever called
        // SpaceshipHasArrived, the phase's "observed inside the jump iterator" rule would be
        // observing the in-system arrival path instead.
        foreach (var routine in new[] { "JumpToSystem", "JumpToWormhole", "TravelToWormholeDestination" })
        {
            var calls = Calls(module, Travel, routine);
            Assert.DoesNotContain("SpaceshipHasArrived", calls);
        }
        var jumpToSystem = Calls(module, Travel, "JumpToSystem");
        Assert.Contains("set_usingJumpgate", jumpToSystem);
        Assert.Contains("LoadScene", jumpToSystem);
        Assert.Contains("set_currentPointOfInterest", jumpToSystem.Concat(Fields(module, Travel, "JumpToSystem")));
        Assert.Contains("get_initializedAndReady", jumpToSystem);
        Assert.Contains("TravelToNextWaypoint", jumpToSystem);
        Assert.Contains("ArriveAtGate", jumpToSystem);
        var jumpToWormhole = Calls(module, Travel, "JumpToWormhole");
        Assert.Contains("set_usingJumpgate", jumpToWormhole);
        Assert.Contains("TravelToWormholeDestination", jumpToWormhole);
        Assert.Contains("ArriveAtWormhole", jumpToWormhole);
        Assert.Contains("TravelToNextWaypoint", jumpToWormhole);
        var wormholeDestination = Calls(module, Travel, "TravelToWormholeDestination");
        Assert.Contains("LoadScene", wormholeDestination);
        Assert.Contains("get_initializedAndReady", wormholeDestination);

        // The two-step drive the phase performs: a normal route request hands off to the gate or
        // wormhole, whose own approach starts the jump routine. The pilot never starts it.
        var startTravel = Calls(module, Travel, "StartTravel");
        Assert.Contains("InitiateTravelThroughGate", startTravel);
        Assert.Contains("InitiateTravelThroughWormhole", startTravel);
        Assert.Contains("GenerateShortestRoute", Calls(module, Travel, "SetRouteToPOI"));
        var initiateGate = Calls(module, GateManager, "InitiateTravelThroughGate");
        Assert.Contains("nextWaypointIsSystem", initiateGate);
        Assert.Contains("SetJumpingShip", initiateGate);
        Assert.Contains("SetJumpingShip", Calls(module, WormholeManager, "InitiateTravelThroughWormhole"));
        // The arrival at the source gate/wormhole runs the same auto-handoff, which is a no-op with
        // an emptied waypoint list; that is what parks the ship for the second step.
        Assert.Contains("InitiateTravelThroughGate", Calls(module, GateManager, "SpaceshipHasArrived"));
        Assert.Contains("InitiateTravelThroughWormhole", Calls(module, WormholeManager, "SpaceshipHasArrived"));
        // The fixture factory is genuine native content creation (it sets the POI up through the
        // system's own SetupPOI and adds it to that system), not a hand-built object the phase
        // could shape, and it never moves the player.
        var placeWormhole = Calls(module, "Source.Simulation.World.WormholeSpawner", "PlaceWormhole");
        Assert.Contains("SetupPOI", placeWormhole);
        Assert.Contains("Add", placeWormhole);
        Assert.DoesNotContain("set_currentPointOfInterest", placeWormhole);
        Assert.DoesNotContain("SetRouteToPOI", placeWormhole);
        Assert.DoesNotContain("TryInitiateTravel", placeWormhole);
        Assert.Contains("set_system", Fields(module, "Source.Galaxy.SystemMapData", "SetupPOI"));
        // The sector reachability check the fixture selection reuses is read-only: it only inspects
        // gate/wormhole usability and never generates names, content or randomness.
        var isUnlocked = Calls(module, "Source.Galaxy.SectorMapData", "IsUnlocked");
        Assert.Contains("get_canUseJumpGate", isUnlocked);
        Assert.Contains("get_canUseWormhole", isUnlocked);
        foreach (var forbidden in new[] { "get_name", "GenerateDefaultName", "EnsureContentGenerated", "RandomRange", "RandomInt" })
            Assert.DoesNotContain(forbidden, isUnlocked);

        // Why the probe may never hold a manager across a fixture load: TravelManager is a
        // MonoBehaviour singleton and its route entry point starts a coroutine ON ITSELF, which
        // throws on a destroyed behaviour. The singleton getter re-finds the live instance when its
        // cached one is destroyed, so re-reading Instance after the load is the only correct
        // capture; a reference captured before it stays non-null but dead.
        var singleton = module.GetType("Behaviour.Util.Singleton`1") ?? throw new InvalidOperationException("Missing Singleton`1.");
        Assert.Equal("UnityEngine.MonoBehaviour", singleton.BaseType.FullName);
        Assert.Equal("Behaviour.Util.Singleton`1<" + Travel + ">", (module.GetType(Travel) ?? throw new InvalidOperationException("Missing TravelManager.")).BaseType.FullName);
        Assert.Contains("StartCoroutine", Calls(module, Travel, "SetRouteToPOI"));
        Assert.Contains("FindAnyObjectByType", Calls(module, "Behaviour.Util.Singleton`1", "get_Instance"));

        // Only the native gate/wormhole objects start the jump routines.
        Assert.Contains("JumpToPOIFrom", Calls(module, "Behaviour.Travel.TheGate", "Update"));
        Assert.Contains("JumpToWormholeFrom", Calls(module, "Behaviour.Travel.TheWormhole", "FinishDeparture"));
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
        // A this-capturing yield condition (the destination-readiness WaitUntil) is compiled into a
        // '<name>b__' method on the declaring type, so those bodies are part of the routine too.
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
