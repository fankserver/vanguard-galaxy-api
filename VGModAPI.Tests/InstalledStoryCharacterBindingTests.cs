using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledStoryCharacterBindingTests
{
    [Fact]
    public void CharacterRegistryAndContentMembersBindToInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        foreach (var spec in StoryCharacterBindings.Members)
        {
            var type = assembly.MainModule.GetType(spec.Type);
            Assert.NotNull(type);
            var field = Assert.Single(type.Fields, value => value.Name == spec.Member);
            Assert.Equal(spec.Shape, field.FieldType.FullName);
            Assert.Equal(spec.Static, field.IsStatic); Assert.True(field.IsPublic);
        }
        var spec0 = StoryCharacterBindings.Methods.Single();
        var lookup = assembly.MainModule.GetType(spec0.Type).Methods.Single(method => method.Name == spec0.Name &&
            method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(spec0.Parameters));
        Assert.True(lookup.IsStatic && lookup.IsPublic && lookup.HasBody);
        Assert.Equal(spec0.ReturnType, lookup.ReturnType.FullName);
        // The registry resolves per call via reflection over static factory methods and returns null
        // for unknown names, so an owned name can be supplied without displacing any vanilla entry.
        Assert.Contains(lookup.Body.Instructions, instruction => instruction.Operand is MethodReference target &&
            target.Name == "GetMethod");
        var character = assembly.MainModule.GetType("Source.Dialogues.Character");
        Assert.Single(character.Methods, method => method.IsConstructor && !method.IsStatic &&
            method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(new[] { "System.String" }));
        var line = assembly.MainModule.GetType("Source.Dialogues.DialogueLine");
        Assert.Single(line.Methods, method => method.IsConstructor && !method.IsStatic && method.Parameters.Select(
            parameter => parameter.ParameterType.FullName).SequenceEqual(new[] { "Source.Dialogues.Character", "System.String" }));
        Assert.Single(assembly.MainModule.GetType("Source.Dialogues.Dialogue").Methods,
            method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0);
    }
}
