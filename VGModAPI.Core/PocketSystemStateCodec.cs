using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VGModAPI.Core;

/// <summary>
/// One persisted authored-pocket poi row. Retained declarative content (poi key, revision,
/// declared gate state) plus the fresh per-game native references owned by the poi. Nothing
/// executable or callback-shaped is stored; every live reference is re-resolved per session.
/// </summary>
internal sealed class PocketSystemPoi
{
    internal string Owner { get; }
    internal string LocalId { get; }
    internal string PoiKey { get; }
    internal int Revision { get; private set; }
    internal string SystemId { get; }
    internal string EntranceGateId { get; }
    internal string PocketGateId { get; }
    internal bool DeclaredOpen { get; set; }
    internal PocketSystemPoi(string owner, string localId, string poiKey, int revision,
        string systemId, string entranceGateId, string pocketGateId, bool declaredOpen)
    {
        if (string.IsNullOrWhiteSpace(owner) || WorldStateCodec.TextByteCount(owner) > 128) throw new ArgumentException("A bounded owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(localId) || WorldStateCodec.TextByteCount(localId) > 128) throw new ArgumentException("A bounded local identity is required.", nameof(localId));
        if (string.IsNullOrWhiteSpace(poiKey) || WorldStateCodec.TextByteCount(poiKey) > 256) throw new ArgumentException("A bounded poi key is required.", nameof(poiKey));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (string.IsNullOrWhiteSpace(systemId) || WorldStateCodec.TextByteCount(systemId) > 128) throw new ArgumentException("A bounded native system identity is required.", nameof(systemId));
        if (string.IsNullOrWhiteSpace(entranceGateId) || WorldStateCodec.TextByteCount(entranceGateId) > 128) throw new ArgumentException("A bounded native gate identity is required.", nameof(entranceGateId));
        if (string.IsNullOrWhiteSpace(pocketGateId) || WorldStateCodec.TextByteCount(pocketGateId) > 128) throw new ArgumentException("A bounded native gate identity is required.", nameof(pocketGateId));
        Owner = owner; LocalId = localId; PoiKey = poiKey; Revision = revision;
        SystemId = systemId; EntranceGateId = entranceGateId; PocketGateId = pocketGateId; DeclaredOpen = declaredOpen;
    }
    /// <summary>Moves a retained poi up to a validated live definition revision (previous-revision migration).</summary>
    internal void MigrateRevision(int revision)
    {
        if (revision < 1 || revision <= Revision) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
    }
}

/// <summary>Bounded owned-authored-system inventory. A new row KIND alongside the WorldSavedObject framing, encoded with the same conventions.</summary>
internal static class PocketSystemStateCodec
{
    internal const string Owner = "vgmodapi.world-authored-systems";
    /// <summary>
    /// Schema 4 adds kind 4 (combat-site key); schema 3 added kind 3 (owned wormhole pair); schema 2
    /// supports systems, sites and ships; schema 1 rows are all pocket systems.
    /// </summary>
    internal const int SchemaVersion = 4;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int Magic = 0x32534756;

    internal static byte[] Encode(IReadOnlyList<PocketSystemPoi> rows)
        => Encode(rows, Array.Empty<ResourceSitePoi>());

    internal static byte[] Encode(IReadOnlyList<PocketSystemPoi> rows, IReadOnlyList<ResourceSitePoi> sites)
        => Encode(rows, sites, Array.Empty<MooredShipUnit>());

    internal static byte[] Encode(IReadOnlyList<PocketSystemPoi> rows, IReadOnlyList<ResourceSitePoi> sites, IReadOnlyList<MooredShipUnit> ships)
        => Encode(rows, sites, ships, Array.Empty<WormholePairPoi>());

    internal static byte[] Encode(IReadOnlyList<PocketSystemPoi> rows, IReadOnlyList<ResourceSitePoi> sites,
        IReadOnlyList<MooredShipUnit> ships, IReadOnlyList<WormholePairPoi> wormholes)
        => Encode(rows, sites, ships, wormholes, Array.Empty<CombatSiteKeyRow>());

    internal static byte[] Encode(IReadOnlyList<PocketSystemPoi> rows, IReadOnlyList<ResourceSitePoi> sites,
        IReadOnlyList<MooredShipUnit> ships, IReadOnlyList<WormholePairPoi> wormholes, IReadOnlyList<CombatSiteKeyRow> combatKeys)
    {
        if (rows == null || sites == null || ships == null || wormholes == null || combatKeys == null
            || rows.Count + sites.Count + ships.Count + wormholes.Count + combatKeys.Count > WorldSerializationAssociation.MaxObjects)
            throw new InvalidDataException("Invalid authored-system inventory count.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic); writer.Write(SchemaVersion); writer.Write(rows.Count + sites.Count + ships.Count + wormholes.Count + combatKeys.Count);
        var keys = new HashSet<(string, string, string)>();
        foreach (var row in rows)
        {
            if (row == null || !keys.Add((row.Owner, row.LocalId, row.PoiKey)))
                throw new InvalidDataException("Duplicate authored-system poi.");
            writer.Write((byte)0);
            WriteText(writer, row.Owner); WriteText(writer, row.LocalId); WriteText(writer, row.PoiKey);
            writer.Write(row.Revision); WriteText(writer, row.SystemId); WriteText(writer, row.EntranceGateId);
            WriteText(writer, row.PocketGateId); writer.Write(row.DeclaredOpen);
            if (stream.Length > WorldSerializationAssociation.MaxMetadataBytes) throw new InvalidDataException("Resource-system metadata exceeds its quota.");
        }
        foreach (var site in sites)
        {
            if (site == null || !keys.Add((site.Owner, site.LocalId, site.PoiKey)))
                throw new InvalidDataException("Duplicate authored-system poi.");
            writer.Write((byte)1);
            WriteText(writer, site.Owner); WriteText(writer, site.LocalId); WriteText(writer, site.PoiKey);
            writer.Write(site.Revision); writer.Write((byte)site.Kind); WriteText(writer, site.SystemId); WriteText(writer, site.PoiId);
            if (stream.Length > WorldSerializationAssociation.MaxMetadataBytes) throw new InvalidDataException("Resource-system metadata exceeds its quota.");
        }
        foreach (var ship in ships)
        {
            if (ship == null || !keys.Add((ship.Owner, ship.LocalId, ship.UnitKey)))
                throw new InvalidDataException("Duplicate authored-system poi.");
            writer.Write((byte)2);
            WriteText(writer, ship.Owner); WriteText(writer, ship.LocalId); WriteText(writer, ship.UnitKey);
            writer.Write(ship.Revision); WriteText(writer, ship.StationPoiId); WriteText(writer, ship.UnitId);
            if (stream.Length > WorldSerializationAssociation.MaxMetadataBytes) throw new InvalidDataException("Resource-system metadata exceeds its quota.");
        }
        foreach (var pair in wormholes)
        {
            if (pair == null || !keys.Add((pair.Owner, pair.LocalId, pair.PoiKey)))
                throw new InvalidDataException("Duplicate authored-system poi.");
            writer.Write((byte)3);
            WriteText(writer, pair.Owner); WriteText(writer, pair.LocalId); WriteText(writer, pair.PoiKey);
            writer.Write(pair.Revision); WriteText(writer, pair.FirstSystemId); WriteText(writer, pair.SecondSystemId);
            WriteText(writer, pair.FirstPoiId); WriteText(writer, pair.SecondPoiId); writer.Write(pair.DeclaredOpen);
            if (stream.Length > WorldSerializationAssociation.MaxMetadataBytes) throw new InvalidDataException("Resource-system metadata exceeds its quota.");
        }
        foreach (var combat in combatKeys)
        {
            if (combat == null || !keys.Add((combat.Owner, combat.LocalId, combat.PoiKey)))
                throw new InvalidDataException("Duplicate authored-system poi.");
            writer.Write((byte)4);
            WriteText(writer, combat.Owner); WriteText(writer, combat.LocalId); WriteText(writer, combat.PoiKey);
            writer.Write(1); writer.Write(combat.InstanceId.ToByteArray());
            if (stream.Length > WorldSerializationAssociation.MaxMetadataBytes) throw new InvalidDataException("Resource-system metadata exceeds its quota.");
        }
        writer.Flush(); return stream.ToArray();
    }

    internal static PocketSystemPoi[] Decode(byte[] payload)
        => DecodeAll(payload).Systems;

    internal static (PocketSystemPoi[] Systems, ResourceSitePoi[] Sites, MooredShipUnit[] Ships, WormholePairPoi[] Wormholes, CombatSiteKeyRow[] CombatKeys) DecodeAll(byte[] payload)
    {
        if (payload == null || payload.Length < 12 || payload.Length > WorldSerializationAssociation.MaxMetadataBytes)
            throw new InvalidDataException("Missing or oversized authored-system metadata.");
        using var stream = new MemoryStream(payload, false);
        using var reader = new BinaryReader(stream, Utf8, true);
        try
        {
            if (reader.ReadInt32() != Magic) throw new InvalidDataException("Unsupported authored-system metadata format.");
            int version = reader.ReadInt32();
            if (version < 1 || version > SchemaVersion) throw new InvalidDataException("Unsupported authored-system metadata format.");
            int count = reader.ReadInt32();
            if (count < 0 || count > WorldSerializationAssociation.MaxObjects) throw new InvalidDataException("Invalid authored-system inventory count.");
            var rows = new List<PocketSystemPoi>();
            var sites = new List<ResourceSitePoi>();
            var ships = new List<MooredShipUnit>();
            var wormholes = new List<WormholePairPoi>();
            var combatKeys = new List<CombatSiteKeyRow>();
            var keys = new HashSet<(string, string, string)>();
            for (int i = 0; i < count; i++)
            {
                byte kind = version == 1 ? (byte)0 : reader.ReadByte();
                if (kind > 4 || (kind == 3 && version < 3) || (kind == 4 && version < 4)) throw new InvalidDataException("Unknown authored-poi kind.");
                string owner = ReadText(reader, 128), local = ReadText(reader, 128), key = ReadText(reader, 256);
                int revision = reader.ReadInt32();
                if (kind == 0)
                    rows.Add(new PocketSystemPoi(owner, local, key, revision,
                        ReadText(reader, 128), ReadText(reader, 128), ReadText(reader, 128), reader.ReadBoolean()));
                else if (kind == 1)
                {
                    byte siteKind = reader.ReadByte();
                    if (siteKind > (byte)ResourceSiteKind.MiningField) throw new InvalidDataException("Unknown authored-site kind.");
                    sites.Add(new ResourceSitePoi(owner, local, key, revision,
                        (ResourceSiteKind)siteKind, ReadText(reader, 128), ReadText(reader, 128)));
                }
                else if (kind == 2)
                    ships.Add(new MooredShipUnit(owner, local, key, revision, ReadText(reader, 128), ReadText(reader, 128)));
                else if (kind == 3)
                    wormholes.Add(new WormholePairPoi(owner, local, key, revision,
                        ReadText(reader, 128), ReadText(reader, 128), ReadText(reader, 128), ReadText(reader, 128), reader.ReadBoolean()));
                else
                {
                    if (revision != 1) throw new InvalidDataException("Invalid combat-key revision.");
                    var guidBytes = reader.ReadBytes(16);
                    if (guidBytes.Length != 16) throw new InvalidDataException("Truncated combat-key identity.");
                    combatKeys.Add(new CombatSiteKeyRow(owner, local, key, new Guid(guidBytes)));
                }
                if (!keys.Add((owner, local, key))) throw new InvalidDataException("Duplicate authored-system poi.");
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing authored-system metadata.");
            return (rows.ToArray(), sites.ToArray(), ships.ToArray(), wormholes.ToArray(), combatKeys.ToArray());
        }
        catch (Exception error) when (error is ArgumentException || error is EndOfStreamException)
        { throw new InvalidDataException("Malformed authored-system metadata.", error); }
    }

    private static void WriteText(BinaryWriter writer, string value)
    {
        var bytes = Utf8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static string ReadText(BinaryReader reader, int limit)
    {
        int length = reader.ReadInt32();
        if (length < 1 || length > limit) throw new InvalidDataException("Invalid authored-system text length.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidDataException("Truncated authored-system text.");
        return Utf8.GetString(bytes);
    }
}
