using System;
using System.IO;
using VGModAPI;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ContentSafetyTests
{
    [Theory]
    [InlineData(PersistentContentKind.Item)]
    [InlineData(PersistentContentKind.Mission)]
    [InlineData(PersistentContentKind.Patron)]
    [InlineData(PersistentContentKind.Faction)]
    [InlineData(PersistentContentKind.WorldObject)]
    public void SavedReferencesRemainIntactAcrossProviderRemovalAndDowngrade(PersistentContentKind kind)
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, new[] { "fixture.owner", "custom", kind.ToString(), "2.0.0" });
            var original = File.ReadAllBytes(file); var lines = File.ReadAllLines(file);
            var reference = new PersistentContentReference(lines[0], lines[1], Enum.Parse<PersistentContentKind>(lines[2]), new Version(lines[3]));
            var declaration = new ContentDeclaration(reference.Owner, reference.LocalId, kind, ContentPersistenceImpact.ProviderRequired);
            ContentSafety.RequireAdmission(declaration, true);
            Assert.Throws<InvalidOperationException>(() => ContentSafety.RequireAdmission(declaration, false));
            Assert.Equal(ContentRecoveryAction.UseProvider, Assess(reference, declaration, new Version(2, 0, 0), true));
            Assert.Equal(ContentRecoveryAction.RequireProvider, Assess(reference, declaration, null, false));
            Assert.Equal(ContentRecoveryAction.RequireProvider, Assess(reference, declaration, new Version(2, 0, 0), false));
            Assert.Equal(ContentRecoveryAction.RejectDowngrade, Assess(reference, declaration, new Version(1, 0, 0), true));
            Assert.Equal(ContentRecoveryAction.RejectUnknownReference, Assess(reference, null, null, false));
            Assert.Equal(original, File.ReadAllBytes(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void RecoveryNeedsExplicitTrustedHandlersAndMigrationNeverMeansDeletion()
    {
        var reference = new PersistentContentReference("owner", "thing", PersistentContentKind.Item, new Version(1, 0));
        var independent = new ContentDeclaration("owner", "thing", reference.Kind, ContentPersistenceImpact.IndependentlyReconstructable);
        ContentSafety.RequireAdmission(independent, false);
        Assert.Equal(ContentRecoveryAction.RequireProvider, Assess(reference, independent, null, false));
        Assert.Equal(ContentRecoveryAction.UseIndependentReconstruction, ContentSafety.Assess(reference, independent, null, false, false, true, false));
        var api = new ContentDeclaration("owner", "thing", reference.Kind, ContentPersistenceImpact.ApiDependent);
        Assert.Equal(ContentRecoveryAction.RequireApi, Assess(reference, api, null, false));
        Assert.Equal(ContentRecoveryAction.RequireApi, Assess(reference, api, new Version(1, 0), true));
        Assert.Equal(ContentRecoveryAction.UseApiReconstruction, ContentSafety.Assess(reference, api, null, false, true, false, true));
        var migration = new ContentDeclaration("owner", "thing", reference.Kind, ContentPersistenceImpact.RemovalRequiresMigration);
        Assert.Equal(ContentRecoveryAction.RequireMigration, Assess(reference, migration, null, false));
        Assert.Contains("no automatic deletion", ContentSafety.Diagnostic(ContentRecoveryAction.RequireMigration));
        Assert.Equal(ContentRecoveryAction.RejectUnknownReference, Assess(reference, new ContentDeclaration("another", "thing", reference.Kind, api.Impact), null, false));
        Assert.Equal(ContentRecoveryAction.RejectUnknownReference, Assess(reference, new ContentDeclaration("owner", "thing", PersistentContentKind.Faction, api.Impact), null, false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../vanilla")]
    [InlineData("..")]
    [InlineData("owner:vanilla")]
    public void IdentitiesCannotBecomePathsOrAliases(string id)
        => Assert.Throws<ArgumentException>(() => new ContentDeclaration(id, "thing", PersistentContentKind.Item, ContentPersistenceImpact.ProviderRequired));

    private static ContentRecoveryAction Assess(PersistentContentReference reference, ContentDeclaration? declaration, Version? version, bool enabled)
        => ContentSafety.Assess(reference, declaration, version, enabled, false, false, false);
}
