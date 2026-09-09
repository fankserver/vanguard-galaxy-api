using VGModAPI.Core;

namespace VGModAPI.Tests;

internal static class WorldTestDefinitions
{
    internal static byte[] Envelope(WorldObjectIdentity identity, int revision = 1) =>
        new OwnerSchemaCodec(WorldDefinitionCodec.Owner, 1, _ => true).Encode(WorldDefinitionCodec.Encode(new[]
        {
            new WorldSavedDefinition(identity.Owner, new WorldCombatDefinition(identity.LocalId, revision, "Site", "player", 1))
        }));
}
