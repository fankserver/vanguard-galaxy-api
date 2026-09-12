using System;

namespace VGModAPI;

public enum PersistentKind { Item, Mission, Patron, Faction, WorldObject }
public enum PersistenceImpact { IndependentlyReconstructable, ApiDependent, ProviderRequired, RemovalRequiresMigration }
public enum RecoveryAction
{
    UseProvider, UseIndependentReconstruction, UseApiReconstruction,
    RequireProvider, RequireApi, RequireMigration, RejectUnknownReference, RejectDowngrade
}

/// <summary>Trusted declaration, not a claim inferred from untrusted save fields.</summary>
public sealed class PersistentDeclaration
{
    public string Owner { get; }
    public string LocalId { get; }
    public PersistentKind Kind { get; }
    public PersistenceImpact Impact { get; }
    public PersistentDeclaration(string owner, string localId, PersistentKind kind, PersistenceImpact impact)
    {
        PersistenceSafety.ValidateIdentifier(owner); PersistenceSafety.ValidateIdentifier(localId);
        if (!Enum.IsDefined(typeof(PersistentKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(typeof(PersistenceImpact), impact)) throw new ArgumentOutOfRangeException(nameof(impact));
        Owner = owner; LocalId = localId; Kind = kind; Impact = impact;
    }
}

/// <summary>Saved identity; neither a vanilla alias nor permission to construct game objects.</summary>
public sealed class PersistentReference
{
    public string Owner { get; }
    public string LocalId { get; }
    public PersistentKind Kind { get; }
    public Version MinimumProviderVersion { get; }
    public PersistentReference(string owner, string localId, PersistentKind kind, Version minimumProviderVersion)
    {
        PersistenceSafety.ValidateIdentifier(owner); PersistenceSafety.ValidateIdentifier(localId);
        if (!Enum.IsDefined(typeof(PersistentKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Owner = owner; LocalId = localId; Kind = kind;
        MinimumProviderVersion = minimumProviderVersion ?? throw new ArgumentNullException(nameof(minimumProviderVersion));
    }
}

/// <summary>Pure admission/recovery planning. Callers supply verified availability; no game registry or save is modified.</summary>
public static class PersistenceSafety
{
    internal static void ValidateIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 128) throw new ArgumentException("Bounded content identity required.");
        foreach (char c in text)
            if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '.' && c != '_' && c != '-')
                throw new ArgumentException("Content identity must not contain paths or aliases.");
        if (text == "." || text == "..") throw new ArgumentException("Content identity must not be a path segment.");
    }

    public static void RequireAdmission(PersistentDeclaration declaration, bool explicitDependencyAcknowledged)
    {
        if (declaration == null) throw new ArgumentNullException(nameof(declaration));
        if (declaration.Impact != PersistenceImpact.IndependentlyReconstructable && !explicitDependencyAcknowledged)
            throw new InvalidOperationException("Declare and acknowledge the persistent API/provider dependency before accepting content.");
    }

    public static RecoveryAction Assess(PersistentReference reference, PersistentDeclaration? trustedDeclaration,
        Version? installedProviderVersion, bool providerEnabled, bool apiAvailable, bool independentReconstructionAvailable, bool apiReconstructionAvailable)
    {
        if (reference == null) throw new ArgumentNullException(nameof(reference));
        if (providerEnabled && installedProviderVersion == null) throw new ArgumentException("Enabled provider requires a verified version.", nameof(installedProviderVersion));
        if (trustedDeclaration == null || trustedDeclaration.Owner != reference.Owner || trustedDeclaration.LocalId != reference.LocalId || trustedDeclaration.Kind != reference.Kind)
            return RecoveryAction.RejectUnknownReference;
        if (installedProviderVersion != null && Normalize(installedProviderVersion) < Normalize(reference.MinimumProviderVersion))
            return RecoveryAction.RejectDowngrade;
        if (trustedDeclaration.Impact == PersistenceImpact.ApiDependent && !apiAvailable) return RecoveryAction.RequireApi;
        if (providerEnabled && installedProviderVersion != null) return RecoveryAction.UseProvider;
        return trustedDeclaration.Impact switch
        {
            PersistenceImpact.IndependentlyReconstructable when independentReconstructionAvailable => RecoveryAction.UseIndependentReconstruction,
            PersistenceImpact.ApiDependent when apiAvailable && apiReconstructionAvailable => RecoveryAction.UseApiReconstruction,
            PersistenceImpact.ApiDependent => RecoveryAction.RequireApi,
            PersistenceImpact.RemovalRequiresMigration => RecoveryAction.RequireMigration,
            _ => RecoveryAction.RequireProvider
        };
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    public static string Diagnostic(RecoveryAction action) => action switch
    {
        RecoveryAction.UseProvider => "Invoke the compatible provider; preserve the original if restoration fails.",
        RecoveryAction.UseIndependentReconstruction => "Invoke the independently retained reconstruction handler; do not substitute a vanilla identity.",
        RecoveryAction.UseApiReconstruction => "Invoke the compatible API reconstruction handler; this is not permission to uninstall the API.",
        RecoveryAction.RequireProvider => "Restore and enable the owning provider. Keep the original save and opaque owner data unchanged.",
        RecoveryAction.RequireApi => "Restore the required API and reconstruction handler before loading this content.",
        RecoveryAction.RequireMigration => "Restore and enable the owning provider. Only if intentionally removing it, use an explicit verified migration/export on a copy; no automatic deletion or safe-uninstall promise.",
        RecoveryAction.RejectDowngrade => "The installed provider is older than the saved requirement. Restore a compatible version or explicitly migrate a copy.",
        _ => "Unknown or mismatched content ownership. Refuse reinterpretation and preserve original data."
    };
}
