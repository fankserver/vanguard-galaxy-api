using System;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledMooredShipBindingTests
{
    [Fact]
    public void MooredShipMembersBindToInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        var module = assembly.MainModule;
        var poi = module.GetType("Source.Galaxy.MapPointOfInterest");
        Assert.Contains(poi.Fields, f => f.Name == "units");
        Assert.Contains(poi.Properties, p => p.Name == "current" && p.GetMethod.IsStatic);
        var createPayload = Assert.Single(poi.Methods, m => m.Name == "CreateFixedPayload");
        // The seam derives the loadout/rank enum types from these parameters and parses members by name.
        var loadout = ((GenericInstanceType)createPayload.Parameters[3].ParameterType).GenericArguments[0].Resolve();
        Assert.Contains(loadout.Fields, f => f.Name == "Cargo");
        Assert.Contains(createPayload.Parameters[4].ParameterType.Resolve().Fields, f => f.Name == "Standard");
        var addUnit = Assert.Single(poi.Methods, m => m.Name == "AddUnit");
        Assert.Equal(new[] { "Source.Data.AbstractUnitData", "System.String", "System.Boolean" },
            addUnit.Parameters.Select(p => p.ParameterType.FullName).ToArray());
        Assert.Contains(poi.Methods, m => m.Name == "GetWorldPosition" && !m.HasParameters);
        var unitData = module.GetType("Source.Data.AbstractUnitData");
        Assert.Contains(unitData.Properties, p => p.Name == "guid");
        Assert.Contains(unitData.Fields, f => f.Name == "positionData");
        Assert.Contains(module.GetType("Source.Data.UnitPositionData").Fields, f => f.Name == "position");
        var shipData = module.GetType("Source.SpaceShip.SpaceShipData");
        Assert.Contains(shipData.Fields, f => f.Name == "customShipName");
        Assert.Contains(shipData.Fields, f => f.Name == "dockingState");
        var commander = Assert.Single(shipData.Fields, f => f.Name == "commanderData");
        // callsign/SetName live on the personnel base type; the seam resolves them through inheritance.
        var captain = commander.FieldType.Resolve();
        bool foundCallsign = false, foundSetName = false;
        for (var walk = captain; walk != null; walk = walk.BaseType?.Resolve())
        {
            foundCallsign |= walk.Properties.Any(p => p.Name == "callsign");
            foundSetName |= walk.Methods.Any(m => m.Name == "SetName"
                && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.String", "System.String", "System.String" }));
            if (walk.BaseType == null || walk.BaseType.FullName == "System.Object") break;
        }
        Assert.True(foundCallsign); Assert.True(foundSetName);
        var ship = module.GetType("Behaviour.Unit.SpaceShip");
        Assert.Contains(ship.Fields, f => f.Name == "noBoardable");
        Assert.Contains(ship.Properties, p => p.Name == "spaceShipData");
        Assert.Contains(ship.Methods, m => m.Name == "SetTemporaryActions" && m.Parameters.Count == 1);
        Assert.Contains(ship.Methods, m => m.Name == "SpaceShipExists" && m.IsStatic);
        var unit = module.GetType("Behaviour.Unit.AbstractUnit");
        Assert.Contains(unit.Properties, p => p.Name == "unitData");
        // rigidbody lives on the TargetableUnit base; the seam resolves it through inheritance.
        TypeDefinition? rigidbodyOwner = null;
        for (var walk = unit; walk != null; walk = walk.BaseType?.Resolve())
        {
            if (walk.Properties.Any(p => p.Name == "rigidbody")) { rigidbodyOwner = walk; break; }
            if (walk.BaseType == null || walk.BaseType.FullName == "UnityEngine.MonoBehaviour") break;
        }
        Assert.NotNull(rigidbodyOwner);
        Assert.Equal("UnityEngine.Rigidbody2D", rigidbodyOwner!.Properties.Single(p => p.Name == "rigidbody").PropertyType.FullName);
    }
}
