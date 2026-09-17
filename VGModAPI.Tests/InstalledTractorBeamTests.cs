using System;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledTractorBeamTests
{
    [Fact]
    public void TractorHooksAndNativeSafetyPredicatesExist()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        var module = assembly.MainModule;
        var tractor = module.GetType("Behaviour.Equipment.Module.TractorModule");
        var available = Assert.Single(tractor.Methods, m => m.Name == "GetAvailableTractorBeam");
        Assert.Equal("Behaviour.Tractoring.TractorBeam", available.ReturnType.FullName);
        Assert.Equal("System.Boolean", Assert.Single(available.Parameters).ParameterType.FullName);
        foreach (var name in new[] { "UpdateAvailableTargets", "SetMainSubStats", "IsCrewPodTargetingBlocked" })
            Assert.True(Assert.Single(tractor.Methods, m => m.Name == name).HasBody);
        var targets = tractor.Methods.Single(m => m.Name == "UpdateAvailableTargets");
        Assert.Equal("System.Collections.Generic.IEnumerable`1<Behaviour.Weapons.TargetableUnit>", Assert.Single(targets.Parameters).ParameterType.FullName);
        foreach (var name in new[] { "CanBeAutoTractoredBy", "IsCrewPodTargetingBlocked" })
            Assert.Contains(targets.Body.Instructions, i => i.Operand is MethodReference method && method.Name == name);
        Assert.Equal("System.Int32", tractor.Fields.Single(f => f.Name == "amountOfBonusBeams").FieldType.FullName);
        Assert.Contains(tractor.Fields, f => f.Name == "tractorBeams" && f.FieldType.FullName.Contains("TractorBeam"));
        Assert.Contains(module.GetType("Behaviour.Equipment.Module.AbstractTargetingModule").Fields, f => f.Name == "filteredTargets");
        var equipment = module.GetType("Behaviour.Equipment.AbstractEquipment");
        Assert.Contains(equipment.Fields, f => f.Name == "mainSubStats");
        Assert.Contains(equipment.Properties, p => p.Name == "parent" && p.PropertyType.FullName == "Behaviour.Unit.AbstractUnit");
        Assert.Equal("System.Int32", module.GetType("Source.Util.GameMath").Properties.Single(p => p.Name == "maxLevel").PropertyType.FullName);
        Assert.Contains(module.GetType("Source.Player.GamePlayer").Properties, p => p.Name == "commander");
        var badge = module.GetType("Behaviour.UI.MasteryBadge");
        Assert.Contains(badge.Properties, p => p.Name == "skillTree");
        Assert.Equal("Behaviour.UI.UITooltip", badge.Methods.Single(m => m.Name == "AddTooltipCustomContent").Parameters.Single().ParameterType.FullName);
        var addText = module.GetType("Behaviour.UI.UITooltip").Methods.Single(m => m.Name == "AddTextLine");
        Assert.Equal(new[] { "System.String", "System.Int32", "System.Single" }, addText.Parameters.Select(p => p.ParameterType.FullName));
    }

}
