using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Installed-assembly evidence for the native FAST-LANE branch the qualification phase drives, and
/// for the members it reads. The decisive pin is the uniqueness of the branch's own store: on the
/// inspected build <c>travelMultiplier = 7f</c> exists in exactly ONE method, immediately after
/// <c>TheGate.ChargeFastLaneTravelToNextGate</c> inside the jump routine. That is what makes the
/// runtime observation of a 7 a proof that the branch RAN, rather than a proof that the save
/// carries the unlock flag or that the route contained gates.
/// </summary>
[Trait("Category", "InstalledGame")]
public sealed class InstalledTravelFastLaneProbeTests
{
    private static string AssemblyPath => Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
        ?? throw new InvalidOperationException("Run make check-bindings or set VG_GAME_ASSEMBLY to the original installed Assembly-CSharp.dll.");

    private const string Travel = "Behaviour.Managers.TravelManager";
    private const string Gate = "Behaviour.Travel.TheGate";
    private const string GateManager = "Behaviour.Travel.JumpGateManager";
    private const string Player = "Source.Player.GamePlayer";
    private const string JumpGate = "Source.Galaxy.POI.JumpGate";
    private const string Poi = "Source.Galaxy.MapPointOfInterest";

    [Fact]
    public void FastLaneProbeReflectedMembersHaveInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        TypeDefinition Type(string name) => module.GetType(name) ?? throw new InvalidOperationException("Missing type: " + name);
        // The native state the phase samples read-only at every public fact.
        var multiplier = Assert.Single(Type(Travel).Fields, field => field.Name == "travelMultiplier");
        Assert.Equal("System.Single", multiplier.FieldType.FullName);
        Assert.False(multiplier.IsStatic);
        var active = Assert.Single(Type(Travel).Properties, property => property.Name == "fastLaneTravelActive");
        Assert.Equal("System.Boolean", active.PropertyType.FullName);
        Assert.Null(active.SetMethod);
        // The unlock precondition the phase READS. It has no public setter, and this suite pins that
        // the phase can only ever observe it.
        var unlocked = Assert.Single(Type(Player).Properties, property => property.Name == "fastLaneTravelUnlocked");
        Assert.Equal("System.Boolean", unlocked.PropertyType.FullName);
        Assert.True(unlocked.SetMethod == null || unlocked.SetMethod.IsPrivate);
        // The gate members the chain selection reads, and the charge coroutine itself.
        Assert.Single(Type(JumpGate).Properties, property => property.Name == "canUseJumpGate");
        Assert.Single(Type(JumpGate).Methods, method => method.Name == "GetTargetPOI" && method.ReturnType.FullName == Poi);
        var charge = Assert.Single(Type(Gate).Methods, method => method.Name == "ChargeFastLaneTravelToNextGate");
        Assert.Equal("System.Collections.IEnumerator", charge.ReturnType.FullName);
        Assert.Equal(new[] { "Behaviour.Unit.SpaceShip", JumpGate },
            charge.Parameters.Select(parameter => parameter.ParameterType.FullName).ToArray());
        Assert.Single(Type(GateManager).Methods, method => method.Name == "ArriveAtGate");
        Assert.Single(Type(Travel).Methods, method => method.Name == "GenerateShortestRoute");
    }

    /// <summary>
    /// <c>fastLaneTravelActive</c> is literally <c>travelMultiplier &gt; 1</c>, and the resting value
    /// is stored by the constructor, by <c>CancelTravel</c> and at the end of <c>StartTravel</c> - so
    /// the 7 the phase observes on the gate-to-gate leg really is a transient that is reset when
    /// that leg's own <c>StartTravel</c> returns.
    /// </summary>
    [Fact]
    public void TheFastLaneMultiplierIsSetOnlyByTheChargeBranchAndResetAfterThatLeg()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var stores = new List<(string Method, float Value, string Previous)>();
        foreach (var type in module.Types.SelectMany(Nested))
            foreach (var method in type.Methods.Where(candidate => candidate.HasBody))
                foreach (var instruction in method.Body.Instructions)
                    if (instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field
                        && field.Name == "travelMultiplier")
                    {
                        Assert.Equal(OpCodes.Ldc_R4, instruction.Previous.OpCode);
                        stores.Add((type.FullName + "." + method.Name, (float)instruction.Previous.Operand, instruction.Previous.ToString()));
                    }
        // Exactly one store of the fast-lane value in the WHOLE assembly.
        var fastLane = stores.Where(store => store.Value == 7f).ToArray();
        Assert.Single(fastLane);
        Assert.Contains("<JumpToSystem>", fastLane[0].Method);
        // Every other store is the resting value, including the reset at the end of StartTravel.
        Assert.All(stores.Except(fastLane), store => Assert.Equal(1f, store.Value));
        Assert.Contains(stores, store => store.Value == 1f && store.Method.Contains("<StartTravel>"));
        Assert.Contains(stores, store => store.Value == 1f && store.Method.EndsWith("TravelManager.CancelTravel", StringComparison.Ordinal));
        // fastLaneTravelActive is the comparison against the resting value, not a separate flag.
        var getter = module.GetType(Travel).Methods.Single(method => method.Name == "get_fastLaneTravelActive");
        var names = getter.Body.Instructions.Select(instruction => instruction.ToString()).ToArray();
        Assert.Contains(names, text => text.Contains("ldfld System.Single Behaviour.Managers.TravelManager::travelMultiplier", StringComparison.Ordinal));
        Assert.Contains(names, text => text.Contains("ldc.r4 1", StringComparison.Ordinal));
        Assert.Contains(names, text => text.Contains("cgt", StringComparison.Ordinal));
    }

    /// <summary>
    /// The branch is only reachable when the NEXT waypoint is a usable gate, which is why the
    /// post-gate continuation phase (whose follow-on POI is deliberately a safe non-gate) cannot
    /// reach it and this phase must drive a two-gate chain.
    /// </summary>
    [Fact]
    public void TheChargeBranchRequiresANextWaypointGateAndTheUnlockFlag()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var decide = module.GetType(Player).Methods.Single(method => method.Name == "DoFastLaneTravel");
        var decideCalls = decide.Body.Instructions.Select(instruction => instruction.ToString()).ToArray();
        Assert.Contains(decideCalls, text => text.Contains("isinst " + JumpGate, StringComparison.Ordinal));
        Assert.Contains(decideCalls, text => text.Contains("get_canUseJumpGate", StringComparison.Ordinal));
        Assert.Contains(decideCalls, text => text.Contains("get_fastLaneTravelUnlocked", StringComparison.Ordinal));
        Assert.Contains(decideCalls, text => text.Contains("get_waypoints", StringComparison.Ordinal)
            || text.Contains("Source.Player.GamePlayer::waypoints", StringComparison.Ordinal));
        // The jump routine asks that question and, when it is true, charges the next gate before it
        // stores the fast-lane multiplier; otherwise it takes the ordinary arrival animation. Both
        // branches then continue the route with the next waypoint.
        var jump = StateMachine(module, Travel, "JumpToSystem");
        var order = jump.Body.Instructions
            .Select(instruction => instruction.Operand is MemberReference member ? member.Name : instruction.ToString())
            .ToArray();
        int decision = Array.IndexOf(order, "DoFastLaneTravel");
        int chargeCall = Array.IndexOf(order, "ChargeFastLaneTravelToNextGate");
        int arriveCall = Array.IndexOf(order, "ArriveAtGate");
        int continueRoute = Array.IndexOf(order, "TravelToNextWaypoint");
        Assert.True(decision >= 0 && chargeCall > decision, "The jump routine must decide the fast lane before charging the next gate.");
        Assert.True(arriveCall > chargeCall, "The ordinary arrival animation is the other branch of that decision.");
        Assert.True(continueRoute > arriveCall, "Both branches continue the route with the next waypoint.");
        var fastLaneStore = jump.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Stfld
            && instruction.Operand is FieldReference field && field.Name == "travelMultiplier");
        Assert.Equal(7f, (float)fastLaneStore.Previous.Operand);
        Assert.True(jump.Body.Instructions.IndexOf(fastLaneStore) > chargeCall,
            "The fast-lane multiplier is stored by the charge branch, after the charge coroutine.");
    }

    private static IEnumerable<TypeDefinition> Nested(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
            foreach (var inner in Nested(nested)) yield return inner;
    }

    private static MethodDefinition StateMachine(ModuleDefinition module, string owner, string name)
    {
        var type = module.GetType(owner) ?? throw new InvalidOperationException("Missing type: " + owner);
        var machine = type.NestedTypes.Single(nested => nested.Name.StartsWith("<" + name + ">d__", StringComparison.Ordinal));
        return machine.Methods.Single(method => method.Name == "MoveNext");
    }
}
