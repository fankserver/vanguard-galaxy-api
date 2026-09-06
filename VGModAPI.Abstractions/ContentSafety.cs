using System;

namespace VGModAPI;

public enum PersistentContentKind { Item, Mission, Patron, Faction, WorldObject }
public enum ContentPersistenceImpact { IndependentlyReconstructable, ApiDependent, ProviderRequired, RemovalRequiresMigration }
public enum ContentRecoveryAction
{
    UseProvider, UseIndependentReconstruction, UseApiReconstruction,
    RequireProvider, RequireApi, RequireMigration, RejectUnknownReference, RejectDowngrade
}

/// <summary>Trusted declaration, not a claim inferred from untrusted save fields.</summary>
public sealed class ContentDeclaration
{
    public string Owner { get; }
    public string LocalId { get; }
    public PersistentContentKind Kind { get; }
    public ContentPersistenceImpact Impact { get; }
    public ContentDeclaration(string owner, string localId, PersistentContentKind kind, ContentPersistenceImpact impact)
    {
        ContentSafety.ValidateIdentifier(owner); ContentSafety.ValidateIdentifier(localId);
        if (!Enum.IsDefined(typeof(PersistentContentKind), kind) || !Enum.IsDefined(typeof(ContentPersistenceImpact), impact))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Owner = owner; LocalId = localId; Kind = kind; Impact = impact;
    }
}

/// <summary>Saved identity; neither a vanilla alias nor permission to construct game objects.</summary>
public sealed class PersistentContentReference
{
    public string Owner { get; }
    public string LocalId { get; }
    public PersistentContentKind Kind { get; }
    public Version MinimumProviderVersion { get; }
    public PersistentContentReference(string owner, string localId, PersistentContentKind kind, Version minimumProviderVersion)
    {
        ContentSafety.ValidateIdentifier(owner); ContentSafety.ValidateIdentifier(localId);
        if (!Enum.IsDefined(typeof(PersistentContentKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Owner = owner; LocalId = localId; Kind = kind;
        MinimumProviderVersion = minimumProviderVersion ?? throw new ArgumentNullException(nameof(minimumProviderVersion));
    }
}

/// <summary>Pure admission/recovery planning. Callers supply verified availability; no game registry or save is modified.</summary>
public static class ContentSafety
{
    internal static void ValidateIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 128) throw new ArgumentException("Bounded content identity required.");
        foreach (char c in text)
            if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '.' && c != '_' && c != '-')
                throw new ArgumentException("Content identity must not contain paths or aliases.");
        if (text == "." || text == "..") throw new ArgumentException("Content identity must not be a path segment.");
    }

    public static void RequireAdmission(ContentDeclaration declaration, bool explicitDependencyAcknowledged)
    {
        if (declaration == null) throw new ArgumentNullException(nameof(declaration));
        if (declaration.Impact != ContentPersistenceImpact.IndependentlyReconstructable && !explicitDependencyAcknowledged)
            throw new InvalidOperationException("Declare and acknowledge the persistent API/provider dependency before accepting content.");
    }

    public static ContentRecoveryAction Assess(PersistentContentReference reference, ContentDeclaration? trustedDeclaration,
        Version? installedProviderVersion, bool providerEnabled, bool apiAvailable, bool independentReconstructionAvailable, bool apiReconstructionAvailable)
    {
        if (reference == null) throw new ArgumentNullException(nameof(reference));
        if (trustedDeclaration == null || trustedDeclaration.Owner != reference.Owner || trustedDeclaration.LocalId != reference.LocalId || trustedDeclaration.Kind != reference.Kind)
            return ContentRecoveryAction.RejectUnknownReference;
        if (installedProviderVersion != null && installedProviderVersion < reference.MinimumProviderVersion)
            return ContentRecoveryAction.RejectDowngrade;
        if (trustedDeclaration.Impact == ContentPersistenceImpact.ApiDependent && !apiAvailable) return ContentRecoveryAction.RequireApi;
        if (providerEnabled && installedProviderVersion != null) return ContentRecoveryAction.UseProvider;
        return trustedDeclaration.Impact switch
        {
            ContentPersistenceImpact.IndependentlyReconstructable when independentReconstructionAvailable => ContentRecoveryAction.UseIndependentReconstruction,
            ContentPersistenceImpact.ApiDependent when apiAvailable && apiReconstructionAvailable => ContentRecoveryAction.UseApiReconstruction,
            ContentPersistenceImpact.ApiDependent => ContentRecoveryAction.RequireApi,
            ContentPersistenceImpact.RemovalRequiresMigration => ContentRecoveryAction.RequireMigration,
            _ => ContentRecoveryAction.RequireProvider
        };
    }

    public static string Diagnostic(ContentRecoveryAction action) => action switch
    {
        ContentRecoveryAction.UseProvider => "Invoke the compatible provider; preserve the original if restoration fails.",
        ContentRecoveryAction.UseIndependentReconstruction => "Invoke the independently retained reconstruction handler; do not substitute a vanilla identity.",
        ContentRecoveryAction.UseApiReconstruction => "Invoke the compatible API reconstruction handler; this is not permission to uninstall the API.",
        ContentRecoveryAction.RequireProvider => "Restore and enable the owning provider. Keep the original save and opaque owner data unchanged.",
        ContentRecoveryAction.RequireApi => "Restore the required API and reconstruction handler before loading this content.",
        ContentRecoveryAction.RequireMigration => "Removal requires an explicit verified migration/export on a copy; no automatic deletion or safe-uninstall promise.",
        ContentRecoveryAction.RejectDowngrade => "The installed provider is older than the saved requirement. Restore a compatible version or explicitly migrate a copy.",
        _ => "Unknown or mismatched content ownership. Refuse reinterpretation and preserve original data."
    };
}
