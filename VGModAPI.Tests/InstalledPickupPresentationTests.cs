using System;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledPickupPresentationTests
{
    [Fact]
    public void PickupHooksAndFadeFieldsMatchInspectedGame()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        var module = assembly.MainModule;
        var item = module.GetType("Behaviour.Item.InventoryItemType");
        foreach (var property in new[] { "identifier", "displayName", "rarity" })
            Assert.NotNull(Assert.Single(item.Properties, p => p.Name == property).GetMethod);
        var notify = Assert.Single(module.GetType("Source.Data.AbstractUnitData").Methods, m => m.Name == "NotifyItemPickup");
        Assert.Equal(new[] { item.FullName, "System.Int32" }, notify.Parameters.Select(p => p.ParameterType.FullName));
        Assert.Equal("System.Void", notify.ReturnType.FullName);
        Assert.Contains(notify.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "ShowPickupText");
        var floating = module.GetType("Behaviour.UI.FloatingInfoText");
        Assert.Equal("TMPro.TextMeshPro", Assert.Single(floating.Fields, f => f.Name == "numberText").FieldType.FullName);
        Assert.Equal("UnityEngine.Color", Assert.Single(floating.Fields, f => f.Name == "textColor").FieldType.FullName);
        var show = Assert.Single(floating.Methods, m => m.Name == "Show");
        Assert.Equal(new[] { "UnityEngine.GameObject", "System.Single", "Behaviour.UI.InfoType", "UnityEngine.Vector2", "System.String", "System.Nullable`1<UnityEngine.Color>" },
            show.Parameters.Select(p => p.ParameterType.FullName));
        Assert.Contains(show.Body.Instructions, i => i.Operand is FieldReference f && f.Name == "textColor");
        var update = Assert.Single(floating.Methods, m => m.Name == "Update");
        Assert.Contains(update.Body.Instructions, i => i.Operand is FieldReference f && f.Name == "textColor");
        var color = Assert.Single(module.GetType("Source.Util.RarityExtensions").Methods, m => m.Name == "GetColor");
        Assert.True(color.IsStatic);
        Assert.Equal("UnityEngine.Color", color.ReturnType.FullName);
    }
}
