using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledEncounterEquipmentBindingTests
{
    [Fact]
    public void EncounterEquipmentMembersBindToInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        foreach (var spec in EncounterEquipmentBindings.Members)
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
    }

    [Fact]
    public void EncounterEquipmentMethodsBindToInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        foreach (var spec in EncounterEquipmentBindings.Methods)
        {
            var type = assembly.MainModule.GetType(spec.Type);
            Assert.NotNull(type);
            // Full declared arity is asserted: MethodInfo.Invoke never fills optional parameters, so
            // every trailing optional parameter is part of the contract and must be present.
            var method = Assert.Single(type.Methods, value => value.Name == spec.Name &&
                value.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(spec.Parameters));
            Assert.Equal(spec.ReturnType, method.ReturnType.FullName);
            Assert.Equal(spec.Static, method.IsStatic);
            Assert.True(method.HasBody, spec.Name + " must have a body to invoke.");
        }
    }
}
