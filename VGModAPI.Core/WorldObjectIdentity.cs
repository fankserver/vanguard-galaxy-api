using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VGModAPI.Core;

/// <summary>Deterministic native identity for one owner-scoped world instance, not an admission token.</summary>
internal sealed class WorldObjectIdentity
{
    internal const string ReservedPrefix = "vgmodapi.world.";
    internal string Owner { get; }
    internal string LocalId { get; }
    internal Guid InstanceId { get; }
    internal string NativeId { get; }

    internal WorldObjectIdentity(ContentDeclaration declaration, Guid instanceId)
    {
        if (declaration == null) throw new ArgumentNullException(nameof(declaration));
        if (declaration.Kind != PersistentContentKind.WorldObject)
            throw new ArgumentException("A world declaration is required.", nameof(declaration));
        if (instanceId == Guid.Empty) throw new ArgumentException("A nonempty instance identity is required.", nameof(instanceId));
        Owner = declaration.Owner;
        LocalId = declaration.LocalId;
        InstanceId = instanceId;
        // BinaryWriter strings are UTF-8 with length prefixes: tuple boundaries cannot alias.
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            writer.Write("VGModAPI.WorldIdentity.v1");
            writer.Write(Owner);
            writer.Write(LocalId);
            writer.Write(InstanceId.ToString("N"));
        }
        using var hash = SHA256.Create();
        NativeId = ReservedPrefix + "v1." + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
    }

    // Unknown versions and malformed owned markers must not be mistaken for vanilla identities.
    internal static bool IsReserved(string? nativeId) =>
        nativeId != null && nativeId.StartsWith(ReservedPrefix, StringComparison.Ordinal);
}
