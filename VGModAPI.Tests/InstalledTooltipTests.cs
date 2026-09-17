using System;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledTooltipTests
{
    [Fact]
    public void TooltipExtensionNativeShapesExist()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        var module = assembly.MainModule;
        // Ship module family: every required module declares its own stat builder on AbstractEquipment.
        var equipment = module.GetType("Behaviour.Equipment.AbstractEquipment");
        Assert.NotNull(Assert.Single(equipment.Methods, m => m.Name == "SetMainSubStats" && !m.HasBody));
        Assert.Equal("System.String", equipment.Methods.Single(m => m.Name == "GetName").ReturnType.FullName);
        Assert.Equal("System.Int32", equipment.Properties.Single(p => p.Name == "qualityLevel").PropertyType.FullName);
        Assert.True(Assert.Single(module.GetType("Behaviour.Equipment.Turret.AbstractTurret").Methods, m => m.Name == "SetMainSubStats").HasBody); // abstract intermediates hold real builders
        foreach (var name in new[] { "TractorModule", "MiningModule", "SalvageModule", "ShieldGeneratorModule", "DroneBayModule" })
        {
            var type = module.GetType("Behaviour.Equipment.Module." + name);
            Assert.True(Assert.Single(type.Methods, m => m.Name == "SetMainSubStats").HasBody);
        }
        var mainSubStats = module.GetType("Behaviour.Equipment.MainSubStats");
        Assert.NotNull(Assert.Single(mainSubStats.Methods, m => m.Name == "AddMainSubStat"
            && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.String", "System.String" })));
        Assert.Contains(mainSubStats.Properties, p => p.Name == "subStatsList");
        var subStat = module.GetType("Behaviour.Equipment.SubStat");
        foreach (var name in new[] { "mainSubStatName", "mainSubStatAmount" })
            Assert.Equal("System.String", subStat.Properties.Single(p => p.Name == name).PropertyType.FullName);
        // Item tooltip family: the shared fill plus the item source surface.
        var tooltip = module.GetType("Behaviour.UI.UITooltip");
        var fill = Assert.Single(tooltip.Methods, m => m.Name == "SetContent");
        Assert.Equal("Behaviour.UI.Tooltip.TooltipSource", fill.Parameters.Single().ParameterType.FullName);
        Assert.Contains(tooltip.Properties, p => p.Name == "Source" && p.PropertyType.FullName == "Behaviour.UI.Tooltip.TooltipSource");
        var source = module.GetType("Behaviour.UI.Tooltip.ItemTooltipSource");
        Assert.Equal("Behaviour.Item.InventoryItemType", source.Properties.Single(p => p.Name == "item").PropertyType.FullName);
        Assert.Equal("System.Int32", source.Properties.Single(p => p.Name == "count").PropertyType.FullName);
        var item = module.GetType("Behaviour.Item.InventoryItemType");
        foreach (var name in new[] { "identifier", "displayName", "description" })
            Assert.Equal("System.String", item.Properties.Single(p => p.Name == name).PropertyType.FullName);
        Assert.Equal("System.Int32", item.Properties.Single(p => p.Name == "itemLevel").PropertyType.FullName);
        Assert.Contains(module.GetType("Source.Util.ColorHelper").Fields, f => f.Name == "greenish");
        Assert.Contains(module.GetType("Source.Util.ColorHelper").Fields, f => f.Name == "detailsColor");
    }
}
