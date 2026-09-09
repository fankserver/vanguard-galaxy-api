using System;
using System.IO;
using System.Text;

namespace VGModAPI.Core;

/// <summary>A bounded, self-contained plain-item declaration carried by vanilla's saved item identifier.</summary>
internal sealed class OwnedItemIdentity
{
    internal const string Prefix = "vgmodapi.item.v1.";
    internal string Owner { get; }
    internal OwnedItemDefinition Definition { get; }
    internal string NativeId { get; }
    internal OwnedItemIdentity(string owner, OwnedItemDefinition definition)
    {
        _ = new ContentDeclaration(owner, definition.LocalId, PersistentContentKind.Item, ContentPersistenceImpact.ApiDependent);
        if (definition.Revision < 1 || definition.Name.Length < 1 || definition.Name.Length > 128 ||
            definition.Description.Length > 2048 || definition.IconItemId.Length < 1 || definition.IconItemId.Length > 256 ||
            definition.IconItemId.StartsWith("vgmodapi.", StringComparison.Ordinal) ||
            float.IsNaN(definition.Volume) || float.IsInfinity(definition.Volume) || definition.Volume <= 0 || definition.Volume > 1000000 ||
            definition.BaseCost < 0 || definition.BaseCost > 100000000 || !Enum.IsDefined(typeof(OwnedItemStorage), definition.Storage))
            throw new ArgumentException("Unsupported plain item definition.", nameof(definition));
        Owner = owner; Definition = definition;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
        {
            writer.Write(owner); writer.Write(definition.LocalId); writer.Write(definition.Revision);
            writer.Write(definition.Name); writer.Write(definition.Description); writer.Write(definition.IconItemId);
            writer.Write(definition.Volume); writer.Write(definition.BaseCost); writer.Write((int)definition.Storage);
        }
        NativeId = Prefix + Convert.ToBase64String(stream.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (NativeId.Length > 12288) throw new ArgumentException("Owned item declaration exceeds identity limit.");
    }
    internal static bool IsReserved(string id) => id.StartsWith("vgmodapi.item.", StringComparison.Ordinal);
    internal static OwnedItemIdentity Read(string id)
    {
        if (id == null || id.Length > 12288 || !id.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unsupported owned item identity.");
        try
        {
            var encoded = id.Substring(Prefix.Length).Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            using var stream = new MemoryStream(Convert.FromBase64String(encoded), false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
            var result = new OwnedItemIdentity(reader.ReadString(), new OwnedItemDefinition(reader.ReadString(),
                reader.ReadInt32(), reader.ReadString(), reader.ReadString(), reader.ReadString(),
                reader.ReadSingle(), reader.ReadInt32(), (OwnedItemStorage)reader.ReadInt32()));
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing owned item data.");
            if (result.NativeId != id) throw new InvalidDataException("Noncanonical owned item declaration.");
            return result;
        }
        catch (Exception error) when (error is not InvalidDataException)
        { throw new InvalidDataException("Invalid owned item declaration.", error); }
    }
}
