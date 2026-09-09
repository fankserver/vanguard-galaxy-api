using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledGameplayUiBindingTests
{
    [Fact]
    public void GameplayUiBoundaryBindsToInspectedSingletonAndInitializationMethods()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        var panel = assembly.MainModule.GetType(GameplayUiBindings.PanelType);
        Assert.NotNull(panel);
        foreach (var spec in GameplayUiBindings.Members)
        {
            var field = Assert.Single(panel.Fields, value => value.Name == spec.Member);
            Assert.Equal(spec.Shape, field.FieldType.FullName); Assert.Equal(spec.Static, field.IsStatic); Assert.True(field.IsPublic);
        }
        foreach (var spec in GameplayUiBindings.Methods)
        {
            var method = Assert.Single(panel.Methods, value => value.Name == spec.Name &&
                value.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(spec.Parameters));
            Assert.Equal(spec.Static, method.IsStatic); Assert.Equal(spec.ReturnType, method.ReturnType.FullName);
            Assert.True(method.HasBody);
        }
    }
}
