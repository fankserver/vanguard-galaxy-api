using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledRecipeBindingTests
{
    [Fact]
    public void RecipeReadsBindToInspectedMemberShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        foreach (var spec in RecipeCatalogBindings.Members.Concat(RecipeQuoteBindings.Members).Concat(CraftingJobBindings.Members).Concat(CraftingCommandBindings.Members).Concat(ForgeUiBindings.Members))
        {
            var type = assembly.MainModule.GetType(spec.Type);
            Assert.NotNull(type);
            if (spec.Field)
            {
                var field = Assert.Single(type.Fields, value => value.Name == spec.Member);
                Assert.Equal(spec.Shape, field.FieldType.FullName); Assert.Equal(spec.Static, field.IsStatic);
            }
            else
            {
                var property = Assert.Single(type.Properties, value => value.Name == spec.Member);
                Assert.Equal(spec.Shape, property.PropertyType.FullName); Assert.Equal(spec.Static, property.GetMethod.IsStatic);
                Assert.Empty(property.Parameters);
            }
        }
        foreach (var spec in RecipeQuoteBindings.Methods.Concat(CraftingJobBindings.Hooks).Concat(CraftingJobBindings.Persistence).Concat(CraftingCommandBindings.Actions).Concat(CraftingCommandBindings.Serialization).Concat(ForgeUiBindings.Methods))
        {
            var type = assembly.MainModule.GetType(spec.Type);
            Assert.Contains(type.Methods, method => method.Name == spec.Name && method.IsStatic == spec.Static &&
                method.ReturnType.FullName == spec.ReturnType && method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(spec.Parameters));
        }
        var translation = assembly.MainModule.GetType("Source.Util.Translation");
        var method = Assert.Single(translation.Methods, value => value.Name == "Translate" && value.Parameters.Count == 2);
        Assert.True(method.IsStatic); Assert.Equal("System.String", method.ReturnType.FullName);
        Assert.Equal(new[] { "System.String", "System.Object[]" }, method.Parameters.Select(value => value.ParameterType.FullName));
    }
}
