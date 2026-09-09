using System;
using System.Linq;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private void CheckDungeonProviderReacquisition(IDungeonProvider provider, DungeonOccurrenceSnapshot occurrence, DungeonDefinition definition)
    {
        WriteAtomic("dungeon-provider.txt", new[] { "INCOMPLETE" });
        Require(provider.Choose(occurrence.Id, "verify", "ack").Status == DungeonContentStatus.MissingDefinition, "Unregistered behavior was not refused.");
        int incompatibleCalls = 0, matchingCalls = 0;
        var newer = new DungeonDefinition(2, definition.Name, definition.Layout, definition.FactionId, definition.Events, definition.AllowHazards, definition.AllowScheduledReinforcements);
        using (provider.Register(occurrence.DefinitionId.LocalId, newer, _ => { incompatibleCalls++; return true; }))
        {
            Require(provider.Choose(occurrence.Id, "verify", "ack").Status == DungeonContentStatus.VersionMismatch, "New version silently migrated a retained occurrence.");
            Require(incompatibleCalls == 0 && provider.GetOccurrences().Single().DefinitionVersion == occurrence.DefinitionVersion, "Incompatible behavior ran or rewrote version.");
        }
        using (provider.Register(occurrence.DefinitionId.LocalId, definition, _ => { matchingCalls++; return true; }))
        {
            Require(provider.Choose(occurrence.Id, "verify", "ack").Status == DungeonContentStatus.ChoiceApplied, "Matching behavior did not resume.");
            Require(provider.Choose(occurrence.Id, "verify", "ack").Status == DungeonContentStatus.AlreadyChosen, "Restored choice replay was admitted.");
            Require(matchingCalls == 1 && provider.GetOccurrences().Single().Choices.TryGetValue("verify", out var choice) && choice == "ack", "Matching behavior or choice accounting mismatch.");
        }
        WriteAtomic("dungeon-provider.txt", new[] { "PASS", "dungeon-provider-v1", "absent-registration-version-refusal-matching-behavior-once" });
    }
}
