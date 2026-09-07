using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Host regressions for the owner-scoped story contract: identity, the explicitly supported subset,
/// fail-closed registration, distinct occurrence identity, retention policy and the bounded state
/// codec. These prove the pure rules only; driving vanilla registration/reconstruction is separate
/// work and is not claimed here.
/// </summary>
public sealed class StoryContentTests
{
    private static StoryMissionDefinition Definition(string provider = "anima", string local = "salvage-run",
        StoryRetention retention = StoryRetention.Temporary)
        => new(new StoryContentId(provider, local), "Salvage run", "Recover the drifting cargo.",
            new[] { new StoryStep("Reach the wreck", new[] { StoryObjective.TravelTo("poi-guid-1", 5) }) },
            new[] { new StoryReward(StoryRewardKind.Credits, 500) }, StoryDifficulty.Normal, retention);

    // --- identity ---------------------------------------------------------------------------

    [Fact]
    public void ContentIdentityIsProviderScopedAndRejectsAliasLikeSegments()
    {
        var first = new StoryContentId("anima", "mission-x");
        var second = new StoryContentId("custommission", "mission-x");
        // Two independently loaded mods may both use the same local ID.
        Assert.NotEqual(first, second);
        Assert.Equal(first, new StoryContentId("anima", "mission-x"));
        Assert.NotEqual(StoryContentPolicy.Identifier(first), StoryContentPolicy.Identifier(second));
        foreach (var bad in new[] { "", "Anima", "1anima", "an ima", "anima/x", "../x", new string('a', 49) })
            Assert.Throws<ArgumentException>(() => new StoryContentId(bad, "mission-x"));
    }

    [Fact]
    public void TheNamespacedIdentifierRoundTripsAndForeignIdentifiersAreNotOurs()
    {
        var id = new StoryContentId("anima", "mission-x");
        var identifier = StoryContentPolicy.Identifier(id);
        Assert.StartsWith(StoryContentPolicy.IdentifierPrefix, identifier);
        Assert.True(StoryContentPolicy.TryParseIdentifier(identifier, out var parsed));
        Assert.Equal(id, parsed);
        // Vanilla and other-mod identifiers are never reinterpreted as ours.
        foreach (var foreign in new[] { "tutorial_intro", "vgmodapi.story.", "vgmodapi.story.anima", "vgmodapi.story.Anima.x", null })
            Assert.False(StoryContentPolicy.TryParseIdentifier(foreign, out _));
    }

    // --- supported subset -------------------------------------------------------------------

    [Fact]
    public void OnlyVanillaResolvableObjectivesAndRewardsAreSupported()
    {
        // Vanilla resolves objective/reward types by unqualified name from its own assembly, so the
        // supported kinds map 1:1 onto those type names and nothing else may be declared.
        Assert.Equal("TravelToPOI", StoryContentPolicy.ObjectiveTypeName(StoryObjectiveKind.TravelToPoi));
        Assert.Equal("KillEnemies", StoryContentPolicy.ObjectiveTypeName(StoryObjectiveKind.KillEnemies));
        Assert.Equal("CollectCredits", StoryContentPolicy.ObjectiveTypeName(StoryObjectiveKind.CollectCredits));
        Assert.Equal("Credits", StoryContentPolicy.RewardTypeName(StoryRewardKind.Credits));
        Assert.Equal("Experience", StoryContentPolicy.RewardTypeName(StoryRewardKind.Experience));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoryContentPolicy.ObjectiveTypeName((StoryObjectiveKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoryContentPolicy.RewardTypeName((StoryRewardKind)99));
        Assert.Null(StoryContentPolicy.Refuse(Definition()));
    }

    [Fact]
    public void DefinitionsAreImmutableAndValidatedAtConstruction()
    {
        var id = new StoryContentId("anima", "salvage-run");
        var step = new StoryStep("Reach the wreck", new[] { StoryObjective.CollectCredits(10) });
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition(id, "", "d", new[] { step }));
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition(id, "t", "d", Array.Empty<StoryStep>()));
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition(id, "t", "d",
            Enumerable.Range(0, StoryMissionDefinition.MaxSteps + 1).Select(_ => step)));
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition(id, "t", "d", new[] { step },
            new[] { new StoryReward(StoryRewardKind.Credits, 1), new StoryReward(StoryRewardKind.Credits, 2) }));
        Assert.Throws<ArgumentException>(() => new StoryStep("s", Array.Empty<StoryObjective>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoryObjective.KillEnemies(0));
        Assert.Throws<ArgumentException>(() => StoryObjective.TravelTo(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StoryReward(StoryRewardKind.Credits, 0));
        // The declared collections are copies, so a later mutation of the caller's list cannot change them.
        var objectives = new List<StoryObjective> { StoryObjective.CollectCredits(10) };
        var built = new StoryStep("s", objectives);
        objectives.Add(StoryObjective.KillEnemies(3));
        Assert.Single(built.Objectives);
    }

    // --- registration -----------------------------------------------------------------------

    [Fact]
    public void RegistrationIsFailClosedForDuplicatesCollisionsAndLimits()
    {
        var registry = new StoryDefinitionRegistry();
        Assert.Equal(StoryRegistrationStatus.Registered, registry.TryRegister(Definition(), out _, out var identifier));
        // The same provider repeating its own local ID is diagnosed, not merged.
        Assert.Equal(StoryRegistrationStatus.DuplicateLocalId, registry.TryRegister(Definition(), out var duplicate, out _));
        Assert.Contains("already registered local ID", duplicate);
        // A different provider with the same local ID is a different identifier and is accepted.
        Assert.Equal(StoryRegistrationStatus.Registered, registry.TryRegister(Definition("custommission"), out _, out var other));
        Assert.NotEqual(identifier, other);
        // Identifiers that already exist in the world are never replaced: vanilla's own Add would
        // silently overwrite them.
        var reserving = new StoryDefinitionRegistry();
        reserving.Reserve(new[] { StoryContentPolicy.Identifier(new StoryContentId("anima", "salvage-run")) });
        Assert.Equal(StoryRegistrationStatus.IdentifierInUse, reserving.TryRegister(Definition(), out var taken, out _));
        Assert.Contains("never replaces existing content", taken);
        Assert.Equal(0, reserving.Count);
        var bounded = new StoryDefinitionRegistry();
        for (int index = 0; index < StoryContentPolicy.MaxDefinitions; index++)
            Assert.Equal(StoryRegistrationStatus.Registered, bounded.TryRegister(Definition("anima", "m" + index), out _, out _));
        Assert.Equal(StoryRegistrationStatus.LimitExceeded, bounded.TryRegister(Definition("anima", "overflow"), out var limit, out _));
        Assert.Contains("nothing was dropped", limit);
    }

    // --- occurrences ------------------------------------------------------------------------

    private static StoryContentService Service(out FakePersistence persistence, StoryRetention retention = StoryRetention.Temporary,
        params StoryMissionDefinition[] definitions)
    {
        persistence = new FakePersistence();
        var service = new StoryContentService(persistence);
        foreach (var definition in definitions.Length > 0 ? definitions : new[] { Definition(retention: retention) })
            Assert.True(service.Register(definition).Succeeded);
        return service;
    }

    [Fact]
    public void RepeatedOccurrencesKeepSeparateIdentityAndOnlyTheirOwnerMutatesThem()
    {
        var service = Service(out _, StoryRetention.Campaign);
        var id = new StoryContentId("anima", "salvage-run");
        Assert.Equal(StoryLedgerStatus.Accepted, service.Offer(id, out var first, out _));
        Assert.Equal(StoryLedgerStatus.Accepted, service.Activate(id, first, out _));
        Assert.Equal(StoryLedgerStatus.Accepted, service.Retire(id, first, StoryOutcome.Completed, null, out _));
        // A repeated run of the same definition is a NEW occurrence, never the earlier one revived.
        Assert.Equal(StoryLedgerStatus.Accepted, service.Offer(id, out var second, out _));
        Assert.NotEqual(first, second);
        Assert.Equal(StoryLedgerStatus.Accepted, service.Activate(id, second, out _));
        Assert.Equal(StoryLedgerStatus.Accepted, service.Retire(id, second, StoryOutcome.Failed, null, out _));
        var records = service.Occurrences(id);
        Assert.Equal(new[] { first, second }, records.Select(record => record.OccurrenceId).ToArray());
        Assert.Equal(new StoryOutcome?[] { StoryOutcome.Completed, StoryOutcome.Failed }, records.Select(record => record.Outcome).ToArray());
        // Another provider cannot update or delete them, even knowing the occurrence identity.
        var foreign = new StoryContentId("custommission", "salvage-run");
        Assert.Equal(StoryLedgerStatus.InvalidTransition, service.Retire(foreign, first, StoryOutcome.Abandoned, null, out var unknown));
        Assert.Contains("not registered", unknown);
        var ledger = service.Ledger;
        Assert.Equal(StoryLedgerStatus.ForeignOwner, ledger.Retire(foreign, first, StoryOutcome.Abandoned, null, out var foreignDiagnostic));
        Assert.Contains("another provider", foreignDiagnostic);
        Assert.Equal(StoryOutcome.Completed, service.Occurrences(id)[0].Outcome);
    }

    [Fact]
    public void AnOutcomeIsRecordedOnceAndUnknownOccurrencesAreRefused()
    {
        var service = Service(out _, StoryRetention.Campaign);
        var id = new StoryContentId("anima", "salvage-run");
        service.Offer(id, out var occurrence, out _);
        Assert.Equal(StoryLedgerStatus.Accepted, service.Retire(id, occurrence, StoryOutcome.Completed, null, out _));
        Assert.Equal(StoryLedgerStatus.InvalidTransition, service.Retire(id, occurrence, StoryOutcome.Failed, null, out var repeated));
        Assert.Contains("recorded once", repeated);
        Assert.Equal(StoryLedgerStatus.UnknownOccurrence, service.Retire(id, Guid.NewGuid(), StoryOutcome.Completed, null, out _));
        Assert.Equal(StoryLedgerStatus.InvalidTransition, service.Activate(id, occurrence, out var late));
        Assert.Contains("offered occurrence", late);
        Assert.True(service.IsCompleted(id));
    }

    [Fact]
    public void RetentionSeparatesBoundedTombstonesFromCampaignOutcomesAndRefusesOverflow()
    {
        var temporary = Service(out _);
        var temporaryId = new StoryContentId("anima", "salvage-run");
        temporary.Offer(temporaryId, out var job, out _);
        // Declared choices are campaign state; a temporary job may not smuggle them in.
        Assert.Equal(StoryLedgerStatus.LimitExceeded, temporary.Retire(temporaryId, job, StoryOutcome.Completed,
            new Dictionary<string, string> { ["branch"] = "left" }, out var refused));
        Assert.Contains("campaign definitions only", refused);
        Assert.Equal(StoryLedgerStatus.Accepted, temporary.Retire(temporaryId, job, StoryOutcome.Completed, null, out _));
        var tombstone = Assert.Single(temporary.Occurrences(temporaryId));
        Assert.Empty(tombstone.Choices);

        var campaign = Service(out _, StoryRetention.Campaign);
        var campaignId = new StoryContentId("anima", "salvage-run");
        campaign.Offer(campaignId, out var act, out _);
        Assert.Equal(StoryLedgerStatus.Accepted, campaign.Retire(campaignId, act, StoryOutcome.Completed,
            new Dictionary<string, string> { ["branch"] = "left" }, out _));
        Assert.Equal("left", Assert.Single(campaign.Occurrences(campaignId)).Choices["branch"]);
        // Bounded retention refuses instead of truncating campaign progression.
        for (int index = 1; index < StoryLedger.MaxRetainedPerDefinition; index++)
        {
            campaign.Offer(campaignId, out var extra, out _);
            Assert.Equal(StoryLedgerStatus.Accepted, campaign.Retire(campaignId, extra, StoryOutcome.Completed, null, out _));
        }
        campaign.Offer(campaignId, out var overflow, out _);
        Assert.Equal(StoryLedgerStatus.LimitExceeded, campaign.Retire(campaignId, overflow, StoryOutcome.Completed, null, out var limit));
        Assert.Contains("refusing rather than dropping", limit);
        Assert.Equal(StoryLedger.MaxRetainedPerDefinition, campaign.Occurrences(campaignId).Count);
    }

    [Fact]
    public void UnregisteredDefinitionsAndUnavailablePersistenceRefuseNewContent()
    {
        var persistence = new FakePersistence();
        var service = new StoryContentService(persistence);
        var id = new StoryContentId("anima", "salvage-run");
        Assert.Equal(StoryLedgerStatus.InvalidTransition, service.Offer(id, out _, out var missing));
        Assert.Contains("not registered", missing);
        var registration = service.Register(Definition());
        Assert.True(registration.Succeeded);
        Assert.Equal(StoryLedgerStatus.Accepted, service.Offer(id, out _, out _));
        // Refusing unavailable persistence instead of silently accepting an unsaved persistent mission.
        persistence.MutationAllowed = false;
        Assert.Equal(StoryLedgerStatus.InvalidTransition, service.Offer(id, out _, out var unavailable));
        Assert.Contains("persistence is unavailable", unavailable);
        persistence.MutationAllowed = true;
        // Disposing the handle stops new content without rewriting saved occurrences.
        registration.Registration!.Dispose();
        Assert.False(registration.Registration.Active);
        Assert.Equal(StoryLedgerStatus.InvalidTransition, service.Offer(id, out _, out var gone));
        Assert.Contains("not registered", gone);
        Assert.Single(service.Ledger.Entries);
    }

    // --- automatic persistence --------------------------------------------------------------

    [Fact]
    public void TheModuleRegistersItsOwnPersistenceProviderAndReconstructsItsState()
    {
        var service = Service(out var persistence, StoryRetention.Campaign);
        // The API owns the codec: no consumer callback is involved anywhere in this path.
        Assert.Equal(StoryStateCodec.Owner, persistence.Provider!.Owner);
        Assert.Equal(StoryStateCodec.SchemaVersion, persistence.Provider.SchemaVersion);
        var id = new StoryContentId("anima", "salvage-run");
        service.Offer(id, out var offered, out _);
        service.Offer(id, out var active, out _);
        service.Activate(id, active, out _);
        service.Offer(id, out var done, out _);
        service.Retire(id, done, StoryOutcome.Completed, new Dictionary<string, string> { ["branch"] = "left" }, out _);
        var bytes = persistence.Provider.Capture();
        Assert.True(persistence.Provider.Validate(bytes));

        var reloaded = new StoryContentService(new FakePersistence());
        Assert.True(reloaded.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
        reloaded.Ledger.Restore(StoryStateCodec.Decode(bytes));
        Assert.Equal(3, reloaded.Ledger.Count);
        Assert.True(reloaded.Ledger.TryGet(offered, out var restoredOffer));
        Assert.Equal(StoryOccurrenceState.Offered, restoredOffer.State);
        Assert.True(reloaded.Ledger.TryGet(active, out var restoredActive));
        Assert.Equal(StoryOccurrenceState.Active, restoredActive.State);
        Assert.Equal("left", Assert.Single(reloaded.Occurrences(id)).Choices["branch"]);
        Assert.True(reloaded.IsCompleted(id));
        // A campaign query needs no journal mod and no provider history file.
        Assert.False(reloaded.IsCompleted(new StoryContentId("anima", "other-run")));
    }

    [Fact]
    public void RestoringAnOlderSaveReplacesStateInsteadOfMergingNewerCompletion()
    {
        var service = Service(out var persistence, StoryRetention.Campaign);
        var id = new StoryContentId("anima", "salvage-run");
        service.Offer(id, out var early, out _);
        var older = persistence.Provider!.Capture();
        service.Retire(id, early, StoryOutcome.Completed, null, out _);
        Assert.True(service.IsCompleted(id));
        // Loading the older generation must not leak the newer completion into it.
        persistence.Provider.Restore(null!, older);
        Assert.False(service.IsCompleted(id));
        Assert.Equal(StoryOccurrenceState.Offered, service.Ledger.Entries.Single().State);
        // Absence of a generation is empty state, not fabricated progress; unreadable data never
        // reaches Restore because the coordinator blocks that owner instead.
        persistence.Provider.Restore(null!, null);
        Assert.Empty(service.Ledger.Entries);
    }

    // --- codec ------------------------------------------------------------------------------

    [Fact]
    public void TheStateCodecIsBoundedAndRefusesMalformedOrForeignVersions()
    {
        var entry = new StoryOccurrenceEntry(new StoryContentId("anima", "salvage-run"), Guid.NewGuid(),
            StoryRetention.Campaign, 1, StoryOccurrenceState.Retired, StoryOutcome.Completed,
            new[] { new KeyValuePair<string, string>("branch", "left") });
        var bytes = StoryStateCodec.Encode(new[] { entry });
        var decoded = Assert.Single(StoryStateCodec.Decode(bytes));
        Assert.Equal(entry.OccurrenceId, decoded.OccurrenceId);
        Assert.Equal(StoryOutcome.Completed, decoded.Outcome);
        Assert.Equal("left", decoded.Choices["branch"]);
        // Truncated, extended and version-shifted payloads are refused, never partially accepted.
        Assert.False(StoryStateCodec.Validate(bytes.Take(bytes.Length - 1).ToArray()));
        Assert.False(StoryStateCodec.Validate(bytes.Concat(new byte[] { 0 }).ToArray()));
        var newer = (byte[])bytes.Clone();
        newer[4] = 2;
        Assert.False(StoryStateCodec.Validate(newer));
        Assert.False(StoryStateCodec.Validate(new byte[] { 1, 2, 3 }));
        Assert.False(StoryStateCodec.Validate(Array.Empty<byte>()));
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Decode(newer));
        // A temporary occurrence never carries declared choices through the codec.
        var temporary = new StoryOccurrenceEntry(new StoryContentId("anima", "salvage-run"), Guid.NewGuid(),
            StoryRetention.Temporary, 2, StoryOccurrenceState.Retired, StoryOutcome.Completed,
            new[] { new KeyValuePair<string, string>("branch", "left") });
        Assert.Empty(Assert.Single(StoryStateCodec.Decode(StoryStateCodec.Encode(new[] { temporary }))).Choices);
        Assert.True(StoryStateCodec.MaxBytes <= 1024 * 1024);
    }

    private sealed class FakePersistence : IPersistenceApi, IPersistenceRegistration
    {
        internal PersistenceProvider? Provider;
        internal bool MutationAllowed = true;
        private bool _disposed;
        public IPersistenceRegistration Register(PersistenceProvider provider)
        {
            Assert.Null(Provider);
            Provider = provider;
            return this;
        }
        bool IPersistenceRegistration.MutationAllowed => !_disposed && MutationAllowed;
        string IPersistenceRegistration.Status => _disposed ? "inactive" : MutationAllowed ? "ready" : "paused";
        public void Dispose() => _disposed = true;
    }
}
