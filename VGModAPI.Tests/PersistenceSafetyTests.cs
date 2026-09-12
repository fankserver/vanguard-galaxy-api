using System;
using System.IO;
using VGModAPI;
using Xunit;

namespace VGModAPI.Tests;

public sealed class PersistenceSafetyTests
{
    [Theory]
    [InlineData(PersistentKind.Item)]
    [InlineData(PersistentKind.Mission)]
    [InlineData(PersistentKind.Patron)]
    [InlineData(PersistentKind.Faction)]
    [InlineData(PersistentKind.WorldObject)]
    public void SavedReferencesRemainIntactAcrossProviderRemovalAndDowngrade(PersistentKind kind)
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, new[] { "fixture.owner", "custom", kind.ToString(), "2.0.0" });
            var original = File.ReadAllBytes(file); var lines = File.ReadAllLines(file);
            var reference = new PersistentReference(lines[0], lines[1], Enum.Parse<PersistentKind>(lines[2]), new Version(lines[3]));
            var declaration = new PersistentDeclaration(reference.Owner, reference.LocalId, kind, PersistenceImpact.ProviderRequired);
            PersistenceSafety.RequireAdmission(declaration, true);
            Assert.Throws<InvalidOperationException>(() => PersistenceSafety.RequireAdmission(declaration, false));
            Assert.Equal(RecoveryAction.UseProvider, Assess(reference, declaration, new Version(2, 0, 0), true));
            Assert.Equal(RecoveryAction.RequireProvider, Assess(reference, declaration, null, false));
            Assert.Equal(RecoveryAction.RequireProvider, Assess(reference, declaration, new Version(2, 0, 0), false));
            Assert.Equal(RecoveryAction.RejectDowngrade, Assess(reference, declaration, new Version(1, 0, 0), true));
            Assert.Equal(RecoveryAction.RejectUnknownReference, Assess(reference, null, null, false));
            Assert.Equal(original, File.ReadAllBytes(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void RecoveryNeedsExplicitTrustedHandlersAndMigrationNeverMeansDeletion()
    {
        var reference = new PersistentReference("owner", "thing", PersistentKind.Item, new Version(1, 0));
        var independent = new PersistentDeclaration("owner", "thing", reference.Kind, PersistenceImpact.IndependentlyReconstructable);
        PersistenceSafety.RequireAdmission(independent, false);
        Assert.Equal(RecoveryAction.RequireProvider, Assess(reference, independent, null, false));
        Assert.Equal(RecoveryAction.UseIndependentReconstruction, PersistenceSafety.Assess(reference, independent, null, false, false, true, false));
        var api = new PersistentDeclaration("owner", "thing", reference.Kind, PersistenceImpact.ApiDependent);
        Assert.Equal(RecoveryAction.RequireApi, Assess(reference, api, null, false));
        Assert.Equal(RecoveryAction.RequireApi, Assess(reference, api, new Version(1, 0), true));
        Assert.Equal(RecoveryAction.UseApiReconstruction, PersistenceSafety.Assess(reference, api, null, false, true, false, true));
        var migration = new PersistentDeclaration("owner", "thing", reference.Kind, PersistenceImpact.RemovalRequiresMigration);
        Assert.Equal(RecoveryAction.RequireMigration, Assess(reference, migration, null, false));
        Assert.Contains("no automatic deletion", PersistenceSafety.Diagnostic(RecoveryAction.RequireMigration));
        Assert.Equal(RecoveryAction.RejectUnknownReference, Assess(reference, new PersistentDeclaration("another", "thing", reference.Kind, api.Impact), null, false));
        Assert.Equal(RecoveryAction.RejectUnknownReference, Assess(reference, new PersistentDeclaration("owner", "thing", PersistentKind.Faction, api.Impact), null, false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../vanilla")]
    [InlineData("..")]
    [InlineData("owner:vanilla")]
    public void IdentitiesCannotBecomePathsOrAliases(string id)
        => Assert.Throws<ArgumentException>(() => new PersistentDeclaration(id, "thing", PersistentKind.Item, PersistenceImpact.ProviderRequired));

    [Theory]
    [InlineData("1.0", "1.0.0.0")]
    [InlineData("1.0.0", "1.0")]
    public void VersionArityDoesNotCreateFalseDowngrade(string installed, string minimum)
    {
        var reference = new PersistentReference("owner", "thing", PersistentKind.Item, new Version(minimum));
        var declaration = new PersistentDeclaration("owner", "thing", reference.Kind, PersistenceImpact.ProviderRequired);
        Assert.Equal(RecoveryAction.UseProvider, Assess(reference, declaration, new Version(installed), true));
    }

    [Fact]
    public void ContradictionsBoundsDiagnosticsAndDowngradePrecedenceAreExplicit()
    {
        var reference = new PersistentReference("owner", "thing", PersistentKind.Item, new Version(2, 0));
        var declaration = new PersistentDeclaration("owner", "thing", reference.Kind, PersistenceImpact.ApiDependent);
        Assert.Throws<ArgumentException>(() => Assess(reference, declaration, null, true));
        Assert.Equal(RecoveryAction.RejectDowngrade, Assess(reference, declaration, new Version(1, 0), true));
        _ = new PersistentDeclaration(new string('a', 128), "thing", reference.Kind, declaration.Impact);
        Assert.Throws<ArgumentException>(() => new PersistentDeclaration(new string('a', 129), "thing", reference.Kind, declaration.Impact));
        Assert.Equal("impact", Assert.Throws<ArgumentOutOfRangeException>(() => new PersistentDeclaration("owner", "thing", reference.Kind, (PersistenceImpact)99)).ParamName);
        foreach (RecoveryAction action in Enum.GetValues(typeof(RecoveryAction))) Assert.False(string.IsNullOrWhiteSpace(PersistenceSafety.Diagnostic(action)));
        Assert.Contains("Restore and enable", PersistenceSafety.Diagnostic(RecoveryAction.RequireMigration));
    }

    private static RecoveryAction Assess(PersistentReference reference, PersistentDeclaration? declaration, Version? version, bool enabled)
        => PersistenceSafety.Assess(reference, declaration, version, enabled, false, false, false);
}
