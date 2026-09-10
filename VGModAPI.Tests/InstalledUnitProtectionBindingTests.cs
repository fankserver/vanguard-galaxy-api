using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledUnitProtectionBindingTests
{
    [Fact]
    public void DamageBracketAndUnitConditionMembersBindToInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        foreach (var spec in UnitProtectionBindings.Members)
        {
            var type = assembly.MainModule.GetType(spec.Type);
            Assert.NotNull(type);
            if (spec.Field)
            {
                var field = Assert.Single(type.Fields, value => value.Name == spec.Member);
                Assert.Equal(spec.Shape, field.FieldType.FullName); Assert.Equal(spec.Static, field.IsStatic);
                Assert.True(field.IsPublic);
            }
            else
            {
                var property = Assert.Single(type.Properties, value => value.Name == spec.Member);
                Assert.Equal(spec.Shape, property.PropertyType.FullName);
                Assert.Equal(spec.Static, property.GetMethod.IsStatic); Assert.Empty(property.Parameters);
            }
        }
        // The destroyed latch must remain writable for the post-damage restore.
        var destroyed = assembly.MainModule.GetType("Behaviour.Weapons.TargetableUnit").Properties.Single(p => p.Name == "isDestroyed");
        Assert.NotNull(destroyed.SetMethod);
        var spec0 = UnitProtectionBindings.Methods.Single();
        var damage = assembly.MainModule.GetType(spec0.Type).Methods.Single(method => method.Name == spec0.Name &&
            method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(spec0.Parameters));
        Assert.True(damage.IsVirtual && damage.HasBody && !damage.IsStatic);
        Assert.Equal(spec0.ReturnType, damage.ReturnType.FullName);
        // SpaceShip's override must keep delegating to this base bracket.
        var ship = assembly.MainModule.GetType("Behaviour.Unit.SpaceShip").Methods.Single(method => method.Name == "TakeDamage");
        Assert.Contains(ship.Body.Instructions, instruction => instruction.Operand is MethodReference target &&
            target.Name == "TakeDamage" && target.DeclaringType.FullName == "Behaviour.Unit.AbstractUnit");
        // The damage body still consults the invincibility flag at all; the restore logic does not
        // depend on where the clamp sits relative to the destroyed latch, since both are restored.
        Assert.Contains(damage.Body.Instructions, instruction => instruction.Operand is FieldReference field &&
            field.Name == "isInvincible");
    }
}
