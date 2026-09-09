using System;
using System.IO;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldQualificationLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "world-package-" + Guid.NewGuid().ToString("N"));
    public WorldQualificationLayoutTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    [Fact]
    [Trait("Category", "BinaryInspection")]
    public void QualificationMarkerIsRejectedBeforePluginInspection()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(typeof(VGModAPI.Patches.WorldLifetimePatches).Assembly.Location);
        var constructor = typeof(System.Reflection.AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!;
        var marker = new CustomAttribute(assembly.MainModule.ImportReference(constructor));
        marker.ConstructorArguments.Add(new CustomAttributeArgument(assembly.MainModule.TypeSystem.String, "VGModAPI.WorldQualification"));
        marker.ConstructorArguments.Add(new CustomAttributeArgument(assembly.MainModule.TypeSystem.String, "empty-combat-v1"));
        assembly.CustomAttributes.Add(marker);
        var path = Path.Combine(_root, "VGModAPI.dll"); assembly.Write(path);
        Assert.Contains("Qualification-only", Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidatePluginVersion(path)).Message);
    }
    [Fact]
    public void QualificationLayoutRejectsForeignReferencesDirectoriesAndMissingFiles()
    {
        foreach (var name in new[] { "VGModAPI.dll", "VGModAPI.Core.dll", "VGModAPI.Abstractions.dll", "README.md" }) File.WriteAllText(Path.Combine(_root, name), "fixture");
        PackageChecks.ValidateQualificationLayout(_root);
        var foreign = Path.Combine(_root, "UnityEngine.dll"); File.WriteAllText(foreign, "forbidden");
        Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidateQualificationLayout(_root)); File.Delete(foreign);
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidateQualificationLayout(_root)); Directory.Delete(Path.Combine(_root, "docs"));
        File.Delete(Path.Combine(_root, "VGModAPI.dll"));
        Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidateQualificationLayout(_root));
    }
    [Fact]
    [Trait("Category", "BinaryInspection")]
    public void CandidateMustCarryMarker()
        => Assert.Contains("marker missing or invalid", Assert.Throws<InvalidOperationException>(() =>
            PackageChecks.ValidatePluginVersion(typeof(VGModAPI.Patches.WorldLifetimePatches).Assembly.Location, qualification: true)).Message);
}
