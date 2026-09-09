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
    }
}
