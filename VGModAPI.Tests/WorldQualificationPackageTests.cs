using System;
using System.IO;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "WorldQualificationPackage")]
public sealed class WorldQualificationPackageTests
{
    [Fact]
    public void CandidateContainsOnlyOwnedAssembliesAndRequiredMarker()
    {
        var root = Environment.GetEnvironmentVariable("VG_WORLD_QUALIFICATION_PACKAGE_ROOT")
            ?? throw new InvalidOperationException("Run make package-world-qualification.");
        PackageChecks.ValidateQualificationLayout(root);
        foreach (var name in PackageChecks.Assemblies) PackageChecks.ValidateAssembly(Path.Combine(root, name + ".dll"), name);
        var plugin = Path.Combine(root, "VGModAPI.dll");
        PackageChecks.ValidatePluginVersion(plugin, qualification: true);
        Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidatePluginVersion(plugin));
        using var resolver = new Mono.Cecil.DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Environment.GetEnvironmentVariable("VG_QUALIFICATION_REFERENCE_DIR")
            ?? throw new InvalidOperationException("Run the qualification package make target with local BepInEx references."));
        using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(plugin, new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver });
        Assert.NotNull(assembly.MainModule.GetType("VGModAPI.QualificationRunContext"));
        var initialization = assembly.MainModule.GetType("VGModAPI.Plugin").Methods;
        var world = Assert.Single(initialization, method => method.Name == "InitializeWorldProtection");
        foreach (var name in new[] { ".ctor", "Authenticate", "ParticipantsReady" })
            Assert.Contains(world.Body.Instructions, instruction => instruction.Operand is Mono.Cecil.MethodReference method &&
                method.DeclaringType.FullName == "VGModAPI.QualificationRunContext" && method.Name == name);
        var dependency = Assert.Single(assembly.MainModule.GetType("VGModAPI.Plugin").CustomAttributes,
            attribute => attribute.AttributeType.FullName == "BepInEx.BepInDependency" &&
                attribute.ConstructorArguments.Count == 2 && Equals(attribute.ConstructorArguments[0].Value, "vgmodapi.qualification.guard"));
        Assert.Equal("0.1.0", dependency.ConstructorArguments[1].Value);
    }
}
