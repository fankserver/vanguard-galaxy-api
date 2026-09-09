using System;
using System.IO;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "WorldQualificationPackage")]
public sealed class WorldAuthorPackageTests
{
    [Theory]
    [InlineData("A", "vgmodapi.qualification.world.a")]
    [InlineData("B", "vgmodapi.qualification.world.b")]
    public void BuiltAuthorsHaveDistinctIdentityAndOnlyPublicApiReferences(string suffix, string id)
    {
        var root = Environment.GetEnvironmentVariable("VG_WORLD_AUTHOR_ROOT") ?? throw new InvalidOperationException("Run make package-world-qualification.");
        var configuration = Environment.GetEnvironmentVariable("VG_WORLD_AUTHOR_CONFIGURATION") ?? throw new InvalidOperationException("Missing author configuration.");
        var name = "WorldAuthor" + suffix;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Environment.GetEnvironmentVariable("VG_QUALIFICATION_REFERENCE_DIR") ?? throw new InvalidOperationException("Missing BepInEx reference directory."));
        using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(root, name, "bin", configuration, "netstandard2.1", name + ".dll"), new ReaderParameters { AssemblyResolver = resolver });
        Assert.Equal(name, assembly.Name.Name);
        foreach (var reference in assembly.MainModule.AssemblyReferences)
            Assert.Contains(reference.Name, new[] { "netstandard", "BepInEx", "UnityEngine", "UnityEngine.CoreModule", "VGModAPI.Abstractions" });
        var plugin = assembly.MainModule.GetType("VGModAPI.WorldQualificationAuthor.Plugin");
        Assert.Equal("BepInEx.BaseUnityPlugin", plugin.BaseType.FullName);
        var metadata = Assert.Single(plugin.CustomAttributes, attribute => attribute.AttributeType.FullName == "BepInEx.BepInPlugin");
        Assert.Equal(id, metadata.ConstructorArguments[0].Value);
        Assert.Equal(id, Assert.Single(plugin.Fields, field => field.Name == "Id").Constant);
        var awake = Assert.Single(plugin.Methods, method => method.Name == "Awake");
        Assert.Contains(awake.Body.Instructions, instruction => instruction.Operand is MethodReference method && method.DeclaringType.FullName == "VGModAPI.IWorldApi" && method.Name == "AcquireProvider");
        foreach (var pair in new[] { ("Create", "CreatePersistentCombatSite"), ("Find", "FindPersistentCombatSite") })
            Assert.Contains(Assert.Single(plugin.Methods, method => method.Name == pair.Item1).Body.Instructions,
                instruction => instruction.Operand is MethodReference method && method.DeclaringType.FullName == "VGModAPI.IWorldProvider" && method.Name == pair.Item2);
    }
}
