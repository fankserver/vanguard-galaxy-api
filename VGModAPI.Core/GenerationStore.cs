using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VGModAPI.Core;

internal enum PublishBoundary { FilesStaged, BeforePublish, Published }

internal sealed class StoredGeneration
{
    internal SnapshotAssociation Identity { get; }
    private readonly Dictionary<string, byte[]> _owners;
    internal Dictionary<string, byte[]> Owners => _owners.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone(), StringComparer.Ordinal);
    internal StoredGeneration(SnapshotAssociation identity, IDictionary<string, byte[]> owners)
    { Identity = identity; _owners = owners.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone(), StringComparer.Ordinal); }
}

// Immutable directories: a crash before rename leaves an ignored stage, never a partial published generation.
internal sealed class GenerationStore
{
    internal const int MaxOwners = 32;
    internal const int MaxOwnerBytes = OwnerSchemaCodec.MaxPayload + 128;
    private readonly string _root;
    private readonly Action<PublishBoundary>? _boundary;
    private static readonly OwnerSchemaCodec Manifest = new("vgmodapi.manifest", 1, _ => true);

    internal GenerationStore(string root, Action<PublishBoundary>? boundary = null)
    {
        _root = Path.GetFullPath(root);
        if (Path.Combine(_root, new string('a', 32), new string('b', 64), new string('c', 32) + ".vgo").Length > 259)
            throw new ArgumentException("Persistence root exceeds the portable Windows path budget.", nameof(root));
        _boundary = boundary;
        RejectLinks(_root);
        Directory.CreateDirectory(_root);
        RejectLinks(_root);
    }

    internal static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private string Location(string slot, string vanillaHash)
    {
        // Reuse identity validation before any untrusted hash becomes a path component.
        _ = new SnapshotAssociation(slot, vanillaHash, vanillaHash, Guid.NewGuid(), Guid.NewGuid());
        return Path.Combine(_root, Hash(Encoding.UTF8.GetBytes(slot)).Substring(0, 32), vanillaHash);
    }

    private static void RejectLinks(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Persistence paths must not traverse links.");
        }
    }

    private static string OwnerFile(string owner) => Hash(Encoding.UTF8.GetBytes(owner)).Substring(0, 32) + ".vgo";

    private static Dictionary<string, byte[]> CopyOwners(IDictionary<string, byte[]> owners)
    {
        if (owners.Count > MaxOwners) throw new InvalidDataException("Too many owners.");
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var pair in owners)
        {
            _ = new OwnerSchemaCodec(pair.Key, 1, _ => true);
            if (pair.Value == null || pair.Value.Length > MaxOwnerBytes) throw new InvalidDataException("Owner envelope exceeds limit.");
            result.Add(pair.Key, (byte[])pair.Value.Clone());
        }
        return result;
    }

    private static string StateHash(Dictionary<string, byte[]> owners)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            foreach (var pair in owners.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.Write(pair.Key); writer.Write(pair.Value.Length); writer.Write(Hash(pair.Value));
            }
        return Hash(stream.ToArray());
    }

    internal StoredGeneration Publish(string slot, string vanillaHash, Guid campaign, IDictionary<string, byte[]> ownerEnvelopes)
    {
        var owners = CopyOwners(ownerEnvelopes);
        var identity = new SnapshotAssociation(slot, vanillaHash, StateHash(owners), campaign, Guid.NewGuid());
        var target = Location(slot, vanillaHash);
        RejectLinks(target);
        var existing = ReadExisting(slot, vanillaHash, false);
        if (existing != null)
        {
            if (!existing.Identity.CanReuse(identity))
            {
                var conflict = Path.Combine(Path.GetDirectoryName(target)!, "conflict-" + vanillaHash + ".vgc");
                WriteNew(conflict, Encoding.ASCII.GetBytes("Ambiguous identical vanilla bytes; retained generation requires explicit recovery."));
                throw new InvalidDataException("Immutable snapshot association conflict.");
            }
            return existing;
        }
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        RejectLinks(parent);
        var stage = Path.Combine(parent, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        foreach (var pair in owners) WriteNew(Path.Combine(stage, OwnerFile(pair.Key)), pair.Value);
        _boundary?.Invoke(PublishBoundary.FilesStaged);
        using var manifest = new MemoryStream();
        using (var writer = new BinaryWriter(manifest, Encoding.UTF8, true))
        {
            // Only a slot digest is persisted, not the local full path.
            writer.Write(Hash(Encoding.UTF8.GetBytes(slot)));
            writer.Write(identity.VanillaHash); writer.Write(identity.StateHash);
            writer.Write(identity.Campaign.ToByteArray()); writer.Write(identity.Snapshot.ToByteArray());
            writer.Write(owners.Count);
            foreach (var pair in owners.OrderBy(p => p.Key, StringComparer.Ordinal))
            { writer.Write(pair.Key); writer.Write(Hash(pair.Value)); }
        }
        WriteNew(Path.Combine(stage, "manifest.vgo"), Manifest.Encode(manifest.ToArray()));
        _boundary?.Invoke(PublishBoundary.BeforePublish);
        RejectLinks(target);
        Directory.Move(stage, target); // Never replace an existing generation, even after a competing publication.
        _boundary?.Invoke(PublishBoundary.Published);
        return new StoredGeneration(identity, owners);
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        RejectLinks(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes, 0, bytes.Length);
        file.Flush(true);
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new InvalidDataException("Persistence file exceeds limit.");
        var result = new byte[(int)file.Length];
        int offset = 0;
        while (offset < result.Length)
        {
            int count = file.Read(result, offset, result.Length - offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        if (file.ReadByte() != -1) throw new InvalidDataException("Persistence file changed while reading.");
        return result;
    }

    private static string ReadText(BinaryReader reader)
    {
        int length = reader.ReadByte(); // Our ASCII strings fit a single-byte BinaryWriter length prefix.
        if (length > 64) throw new InvalidDataException("Manifest string exceeds limit.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length || bytes.Any(b => b > 127)) throw new InvalidDataException("Invalid manifest string.");
        return Encoding.ASCII.GetString(bytes);
    }

    internal void MarkIntent(string slot, Guid operation)
    {
        var parent = Path.GetDirectoryName(Location(slot, new string('0', 64)))!;
        RejectLinks(parent); Directory.CreateDirectory(parent); RejectLinks(parent);
        WriteNew(Path.Combine(parent, "intent-" + operation.ToString("N")), Encoding.ASCII.GetBytes(Hash(Encoding.UTF8.GetBytes(slot))));
    }

    internal void ClearIntent(string slot, Guid operation)
    {
        var parent = Path.GetDirectoryName(Location(slot, new string('0', 64)))!;
        var path = Path.Combine(parent, "intent-" + operation.ToString("N"));
        RejectLinks(path); File.Delete(path);
    }

    internal StoredGeneration? Load(string slot, string vanillaHash) => ReadExisting(slot, vanillaHash, true);

    private StoredGeneration? ReadExisting(string slot, string vanillaHash, bool protectUnknown)
    {
        var target = Location(slot, vanillaHash);
        RejectLinks(target);
        var conflict = Path.Combine(Path.GetDirectoryName(target)!, "conflict-" + vanillaHash + ".vgc");
        RejectLinks(conflict);
        try { _ = File.GetAttributes(conflict); throw new InvalidDataException("Snapshot has unresolved identical-byte conflict evidence."); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        FileAttributes attributes;
        try { attributes = File.GetAttributes(target); }
        catch (FileNotFoundException) { return Missing(target, protectUnknown); }
        catch (DirectoryNotFoundException) { return Missing(target, protectUnknown); }
        if ((attributes & FileAttributes.Directory) == 0) throw new InvalidDataException("Generation path is not a directory.");
        try { return ReadGeneration(target, slot, vanillaHash); }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is IOException || error is ArgumentException || error is UnauthorizedAccessException)
        { throw new InvalidDataException("Published generation is unreadable or invalid.", error); }
    }

    private static StoredGeneration? Missing(string target, bool protectUnknown)
    {
        if (protectUnknown)
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(target)!).Any())
                    throw new InvalidDataException("Unassociated snapshot in a slot with persistence history or interrupted intent.");
            }
            catch (DirectoryNotFoundException) { }
        }
        return null;
    }

    private static StoredGeneration ReadGeneration(string target, string slot, string vanillaHash)
    {
        var decoded = Manifest.Decode(ReadBounded(Path.Combine(target, "manifest.vgo"), 16384));
        if (decoded.Status != SchemaReadStatus.Ready) throw new InvalidDataException("Manifest is protected: " + decoded.Status);
        using var reader = new BinaryReader(new MemoryStream(decoded.Payload!), Encoding.UTF8);
        if (ReadText(reader) != Hash(Encoding.UTF8.GetBytes(slot)) || ReadText(reader) != vanillaHash)
            throw new InvalidDataException("Manifest identity mismatch.");
        var stateHash = ReadText(reader);
        var campaign = new Guid(reader.ReadBytes(16)); var snapshot = new Guid(reader.ReadBytes(16));
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxOwners) throw new InvalidDataException("Owner count exceeds limit.");
        var owners = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal) { "manifest.vgo" };
        for (int i = 0; i < count; i++)
        {
            var owner = ReadText(reader); var expected = ReadText(reader);
            _ = new OwnerSchemaCodec(owner, 1, _ => true);
            var file = OwnerFile(owner);
            var bytes = ReadBounded(Path.Combine(target, file), MaxOwnerBytes);
            if (Hash(bytes) != expected) throw new InvalidDataException("Owner generation digest mismatch.");
            owners.Add(owner, bytes); names.Add(file);
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length || !names.SetEquals(Directory.EnumerateFileSystemEntries(target).Select(Path.GetFileName)))
            throw new InvalidDataException("Generation layout mismatch.");
        if (StateHash(owners) != stateHash) throw new InvalidDataException("Generation state digest mismatch.");
        return new StoredGeneration(new SnapshotAssociation(slot, vanillaHash, stateHash, campaign, snapshot), owners);
    }
}
