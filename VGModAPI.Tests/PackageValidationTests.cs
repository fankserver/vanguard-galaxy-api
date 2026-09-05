using System;
using System.IO;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

public sealed class PackageValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vgmodapi-package-" + Guid.NewGuid().ToString("N"));

    public PackageValidationTests()
    {
        foreach (var relative in PackageChecks.Files)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "synthetic layout fixture");
        }
    }

    [Fact]
    public void ExactLayoutIsAccepted() => PackageChecks.ValidateLayout(_root);

    [Theory]
    [InlineData("Assembly-CSharp.dll")]
    [InlineData("UnityEngine.dll")]
    [InlineData("BepInEx.dll")]
    [InlineData("0Harmony.dll")]
    [InlineData("QualificationRunner.dll")]
    [InlineData("old-build.pdb")]
    [InlineData("docs/unlisted.md")]
    public void ExtraFilesAreRejected(string name)
    {
        File.WriteAllText(Path.Combine(_root, name), "not allowed");
        Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidateLayout(_root));
    }

    [Fact]
    public void MissingOwnedAssemblyIsRejected()
    {
        File.Delete(Path.Combine(_root, "VGModAPI.Core.dll"));
        Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidateLayout(_root));
    }

    [Fact]
    public void EmptyUnexpectedDirectoriesAreRejected()
    {
        Directory.CreateDirectory(Path.Combine(_root, "lib"));
        Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidateLayout(_root));
    }

    [UnixFact]
    public void LinkedRootsAndFilesAreRejectedWithoutFollowingThem()
    {
        var linkedRoot = _root + "-link";
        Directory.CreateSymbolicLink(linkedRoot, _root);
        try { Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidateLayout(linkedRoot + Path.DirectorySeparatorChar)); }
        finally { Directory.Delete(linkedRoot); }
        var dll = Path.Combine(_root, "VGModAPI.dll");
        File.Delete(dll);
        File.CreateSymbolicLink(dll, Path.Combine(_root, "README.md"));
        Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidateLayout(_root));
    }

    [Fact]
    public void StableContractHasOnlyFrameworkReferences() => PackageChecks.ValidateContract(typeof(ILifecycleApi).Assembly.Location);

    [Fact]
    public void ForeignContractReferenceIsRejected()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(typeof(ILifecycleApi).Assembly.Location);
        assembly.MainModule.AssemblyReferences.Add(new AssemblyNameReference("UnityEngine", new Version(1, 0)));
        var altered = Path.Combine(_root, "altered.dll");
        assembly.Write(altered);
        Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidateContract(altered));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix link behavior; Windows link creation may require additional privileges.";
    }
}

[Trait("Category", "Package")]
public sealed class BuiltPackageTests
{
    [Fact]
    public void BuiltPackageContainsOnlyOwnedAssembliesAndAllowedDocumentation()
    {
        var root = Environment.GetEnvironmentVariable("VG_PACKAGE_ROOT")
            ?? throw new InvalidOperationException("Run make package or set VG_PACKAGE_ROOT for built-package checks.");
        PackageChecks.ValidateLayout(root);
        foreach (var name in PackageChecks.Assemblies)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(root, name + ".dll"));
            Assert.Equal(name, assembly.Name.Name);
        }
        PackageChecks.ValidateContract(Path.Combine(root, "VGModAPI.Abstractions.dll"));
    }
}
