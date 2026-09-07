using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Host regressions for the owner-scoped story contract: authenticated provider leases, session
/// availability, the explicitly supported subset, fail-closed registration, distinct occurrence
/// identity, bounded retention and the strict state codec. These prove the pure rules only; driving
/// vanilla registration/reconstruction is separate work and is not claimed here.
/// </summary>
public sealed class StoryContentTests
{
    private const string AnimaPlugin = "com.fank.anima";
    private const string OtherPlugin = "com.other.custommission";

    private static StoryMissionDefinition Definition(string local = "salvage-run",
        StoryRetention retention = StoryRetention.Temporary)
        => new(local, "Salvage run", "Recover the drifting cargo.",
            new[] { new StoryStep("Reach the wreck", new[] { StoryObjective.TravelTo("poi-guid-1", 5) }) },
            new[] { new StoryReward(StoryRewardKind.Credits, 500) }, StoryDifficulty.Normal, retention);

    // --- identity ---------------------------------------------------------------------------

    [Fact]
    public void ContentIdentityIsProviderScopedAndRejectsAliasLikeSegments()
    {
        var first = new StoryContentId("anima", "mission-x");
        var second = new StoryContentId("custommission", "mission-x");
        Assert.NotEqual(first, second);
        Assert.Equal(first, new StoryContentId("anima", "mission-x"));
        Assert.NotEqual(StoryContentPolicy.Identifier(first), StoryContentPolicy.Identifier(second));
        foreach (var bad in new[] { "", "Anima", "1anima", "an ima", "anima/x", "../x", "an.ima", new string('a', 49) })
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
        foreach (var foreign in new[] { "tutorial_intro", "vgmodapi.story.", "vgmodapi.story.anima", "vgmodapi.story.Anima.x", null })
            Assert.False(StoryContentPolicy.TryParseIdentifier(foreign, out _));
    }

    /// <summary>
    /// The provider segment is DERIVED from the host plugin ID, which routinely contains dots and
    /// upper case that the segment charset excludes. The mapping keeps a readable slug and appends a
    /// digest of the exact plugin ID, so slug-identical plugins still differ.
    /// </summary>
    [Fact]
    public void ProviderSegmentsAreDerivedDeterministicallyFromTheHostPluginIdentity()
    {
        var anima = StoryProviderIdentity.Segment(new StoryHostPlugin(AnimaPlugin));
        Assert.Equal(anima, StoryProviderIdentity.Segment(new StoryHostPlugin(AnimaPlugin)));
        Assert.True(StoryContentId.IsValidSegment(anima));
        Assert.NotEqual(anima, StoryProviderIdentity.Segment(new StoryHostPlugin(OtherPlugin)));
        // Plugin IDs that slug to the same readable text keep different segments through the digest.
        Assert.NotEqual(StoryProviderIdentity.Segment(new StoryHostPlugin("com.a.anima")),
            StoryProviderIdentity.Segment(new StoryHostPlugin("com-a-anima")));
        // Identities that would otherwise produce an invalid segment still resolve to a valid one.
        foreach (var odd in new[] { "1", ".", "ÄÖÜ", new string('x', 128) })
            Assert.True(StoryContentId.IsValidSegment(StoryProviderIdentity.Segment(new StoryHostPlugin(odd))));
    }

    /// <summary>
    /// A segment binding outlives the lease that created it: releasing a lease frees the provider's
    /// registrations, never its name. The conflict branch is defensive, since the derivation is
    /// digest-qualified, so it is exercised here directly rather than through a forged collision.
    /// </summary>
    [Fact]
    public void ASegmentStaysBoundToItsHostPluginAndAnotherPluginIsRefused()
    {
        var bindings = new StoryProviderBindings();
        var segment = StoryProviderIdentity.Segment(new StoryHostPlugin(AnimaPlugin));
        Assert.Equal(StoryBindingStatus.Bound, bindings.Bind(segment, AnimaPlugin));
        Assert.Equal(StoryBindingStatus.AlreadyBoundToSelf, bindings.Bind(segment, AnimaPlugin));
        Assert.Equal(StoryBindingStatus.Conflict, bindings.Bind(segment, OtherPlugin));
        Assert.Equal(StoryBindingStatus.Bound, bindings.Bind(StoryProviderIdentity.Segment(new StoryHostPlugin(OtherPlugin)), OtherPlugin));
    }

    // --- supported subset -------------------------------------------------------------------

    [Fact]
    public void OnlyVanillaResolvableObjectivesAndRewardsAreSupported()
    {
        Assert.Equal("TravelToPOI", StoryContentPolicy.ObjectiveTypeName(StoryObjectiveKind.TravelToPoi));
        Assert.Equal("KillEnemies", StoryContentPolicy.ObjectiveTypeName(StoryObjectiveKind.KillEnemies));
        Assert.Equal("CollectCredits", StoryContentPolicy.ObjectiveTypeName(StoryObjectiveKind.CollectCredits));
        Assert.Equal("Credits", StoryContentPolicy.RewardTypeName(StoryRewardKind.Credits));
        Assert.Equal("Experience", StoryContentPolicy.RewardTypeName(StoryRewardKind.Experience));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoryContentPolicy.ObjectiveTypeName((StoryObjectiveKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoryContentPolicy.RewardTypeName((StoryRewardKind)99));
        Assert.Null(StoryContentPolicy.Refuse(new StoryContentId("anima", "salvage-run"), Definition()));
        // A definition whose local ID does not match its resolved identity is refused as invalid.
        Assert.NotNull(StoryContentPolicy.Refuse(new StoryContentId("anima", "other-run"), Definition()));
    }

    [Fact]
    public void DefinitionsAreImmutableAndValidatedAtConstruction()
    {
        var step = new StoryStep("Reach the wreck", new[] { StoryObjective.CollectCredits(10) });
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("Salvage", "t", "d", new[] { step }));
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("salvage", "", "d", new[] { step }));
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("salvage", "t", "d", Array.Empty<StoryStep>()));
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("salvage", "t", "d",
            Enumerable.Range(0, StoryMissionDefinition.MaxSteps + 1).Select(_ => step)));
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("salvage", "t", "d", new[] { step },
            new[] { new StoryReward(StoryRewardKind.Credits, 1), new StoryReward(StoryRewardKind.Credits, 2) }));
        Assert.Throws<ArgumentException>(() => new StoryStep("s", Array.Empty<StoryObjective>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoryObjective.KillEnemies(0));
        Assert.Throws<ArgumentException>(() => StoryObjective.TravelTo(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StoryReward(StoryRewardKind.Credits, 0));
        var objectives = new List<StoryObjective> { StoryObjective.CollectCredits(10) };
        var built = new StoryStep("s", objectives);
        objectives.Add(StoryObjective.KillEnemies(3));
        Assert.Single(built.Objectives);
    }

    // --- registration -----------------------------------------------------------------------

    [Fact]
    public void RegistrationIsFailClosedForInvalidDefinitionsDuplicatesCollisionsAndLimits()
    {
        var registry = new StoryDefinitionRegistry();
        var anima = new StoryContentId("anima", "salvage-run");
        Assert.Equal(StoryRegistrationStatus.Registered, registry.TryRegister(anima, Definition(), out _, out var identifier));
        Assert.Equal(StoryRegistrationStatus.DuplicateLocalId, registry.TryRegister(anima, Definition(), out var duplicate, out _));
        Assert.Contains("already registered local ID", duplicate);
        // A different provider with the same local ID is a different identifier and is accepted.
        var other = new StoryContentId("custommission", "salvage-run");
        Assert.Equal(StoryRegistrationStatus.Registered, registry.TryRegister(other, Definition(), out _, out var otherIdentifier));
        Assert.NotEqual(identifier, otherIdentifier);
        // A policy refusal is about the definition, NOT about someone owning the identifier.
        Assert.Equal(StoryRegistrationStatus.InvalidDefinition,
            registry.TryRegister(new StoryContentId("anima", "mismatch"), Definition(), out var invalid, out _));
        Assert.Contains("does not match its resolved identity", invalid);
        // Identifiers that already exist in the world are never replaced.
        var reserving = new StoryDefinitionRegistry();
        reserving.Reserve(new[] { StoryContentPolicy.Identifier(anima) });
        Assert.Equal(StoryRegistrationStatus.IdentifierInUse, reserving.TryRegister(anima, Definition(), out var taken, out _));
        Assert.Contains("never replaces existing content", taken);
        Assert.Equal(0, reserving.Count);
        var bounded = new StoryDefinitionRegistry();
        for (int index = 0; index < StoryContentPolicy.MaxDefinitions; index++)
            Assert.Equal(StoryRegistrationStatus.Registered,
                bounded.TryRegister(new StoryContentId("anima", "m" + index), Definition("m" + index), out _, out _));
        Assert.Equal(StoryRegistrationStatus.LimitExceeded,
            bounded.TryRegister(new StoryContentId("anima", "overflow"), Definition("overflow"), out var limit, out _));
        Assert.Contains("nothing was dropped", limit);
    }

    // --- provider leases --------------------------------------------------------------------

    [Fact]
    public void AProviderLeaseIsBoundToTheAuthenticatedHostPluginAndCannotBeClaimedByAnother()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var animaPlugin = new object();
        var otherPlugin = new object();
        host.Register(animaPlugin, AnimaPlugin);
        host.Register(otherPlugin, OtherPlugin);
        var anima = service.AcquireProvider(animaPlugin);
        var other = service.AcquireProvider(otherPlugin);
        Assert.True(anima.Succeeded);
        Assert.True(other.Succeeded);
        Assert.NotEqual(anima.Provider!.ProviderId, other.Provider!.ProviderId);
        // An object the host does not know is not a provider: no caller string can substitute for it.
        var unknown = service.AcquireProvider(new object());
        Assert.Equal(StoryProviderStatus.UnknownPlugin, unknown.Status);
        Assert.Null(unknown.Provider);
        // Both mods register the SAME local ID; neither collides and neither can touch the other.
        Assert.True(anima.Provider.Register(Definition()).Succeeded);
        Assert.True(other.Provider.Register(Definition()).Succeeded);
        var animaOffer = anima.Provider.Offer("salvage-run");
        Assert.True(animaOffer.Accepted);
        var stolen = other.Provider.Retire(animaOffer.OccurrenceId, StoryOutcome.Abandoned);
        Assert.Equal(StoryTransitionStatus.ForeignOwner, stolen.Status);
        Assert.DoesNotContain("salvage-run", stolen.Detail);
        // The same plugin asking again gets its own live lease, not a second owner.
        Assert.Same(anima.Provider, service.AcquireProvider(animaPlugin).Provider);
    }

    [Fact]
    public void DisposingALeaseReleasesOnlyThatProviderAndNeverTheModulesPersistenceOwner()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var animaPlugin = new object();
        var otherPlugin = new object();
        host.Register(animaPlugin, AnimaPlugin);
        host.Register(otherPlugin, OtherPlugin);
        var anima = service.AcquireProvider(animaPlugin).Provider!;
        var other = service.AcquireProvider(otherPlugin).Provider!;
        var registration = anima.Register(Definition()).Registration!;
        other.Register(Definition());
        anima.Dispose();
        Assert.False(anima.Active);
        Assert.False(registration.Active);
        Assert.Equal(StoryTransitionStatus.Unavailable, anima.Offer("salvage-run").Status);
        Assert.Equal(StoryKnowledge.Unavailable, anima.Occurrences("salvage-run").Knowledge);
        // The other mod keeps working, and the coordinator owner was never unregistered, so no other
        // mod's saves are paused by a consumer's teardown.
        Assert.True(other.Active);
        Assert.True(other.Offer("salvage-run").Accepted);
        Assert.False(world.Persistence.OwnerDisposed);
        service.Dispose();
        Assert.True(world.Persistence.OwnerDisposed);
    }

    // --- availability -----------------------------------------------------------------------

    /// <summary>
    /// The highest-priority finding: the coordinator never calls restore for a blocked owner, a
    /// failed start or an invalidated session, so a query must report UNAVAILABLE instead of
    /// answering from the previous save.
    /// </summary>
    [Fact]
    public void AnUnrestoredSessionNeverAnswersFromThePreviousSave()
    {
        foreach (var interruption in new Action<FakeWorld>[]
        {
            world => world.StartSession(),                                   // schema-unsupported/corrupt: no restore call
            world => { world.StartSession(); world.Invalidate(); },
            world => world.FailStart(),
            world => { world.StartSession(); world.FailRestore(); }
        })
        {
            var host = new FakeHost();
            var world = new FakeWorld();
            using var service = world.Service(host);
            world.StartAndRestore();
            var plugin = new object();
            host.Register(plugin, AnimaPlugin);
            var provider = service.AcquireProvider(plugin).Provider!;
            provider.Register(Definition(retention: StoryRetention.Campaign));
            var offer = provider.Offer("salvage-run");
            provider.Retire(offer.OccurrenceId, StoryOutcome.Completed);
            Assert.True(provider.IsCompleted("salvage-run").Completed);

            interruption(world);

            var completion = provider.IsCompleted("salvage-run");
            Assert.Equal(StoryKnowledge.Unavailable, completion.Knowledge);
            Assert.Null(completion.Completed);
            Assert.Null(completion.SessionId);
            var occurrences = provider.Occurrences("salvage-run");
            Assert.Equal(StoryKnowledge.Unavailable, occurrences.Knowledge);
            Assert.Empty(occurrences.Records);
            // Content is not accepted while state is unavailable either.
            Assert.Equal(StoryTransitionStatus.Unavailable, provider.Offer("salvage-run").Status);
            // The retained owner bytes are never replaced by an empty capture in that state.
            Assert.False(world.Persistence.MutationAllowed && provider.Offer("salvage-run").Accepted);
        }
    }

    [Fact]
    public void LoadingAnotherSaveAnswersForThatSaveAndAFreshSaveIsKnownEmpty()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        world.StartAndRestore();
        var provider = service.AcquireProvider(plugin).Provider!;
        provider.Register(Definition(retention: StoryRetention.Campaign));
        var offer = provider.Offer("salvage-run");
        provider.Retire(offer.OccurrenceId, StoryOutcome.Completed);
        var saveA = world.Persistence.Provider!.Capture();

        // Save B is unreadable: no restore call, so nothing of save A may be reported.
        world.StartSession();
        Assert.Equal(StoryKnowledge.Unavailable, provider.IsCompleted("salvage-run").Knowledge);
        // A fresh save with no stored generation is KNOWN and empty, not unavailable.
        world.StartAndRestore(null);
        var fresh = provider.IsCompleted("salvage-run");
        Assert.Equal(StoryKnowledge.Known, fresh.Knowledge);
        Assert.False(fresh.Completed);
        Assert.Equal(world.SessionId, fresh.SessionId);
        Assert.Empty(provider.Occurrences("salvage-run").Records);
        // Loading save A again reports save A.
        world.StartAndRestore(saveA);
        var restored = provider.IsCompleted("salvage-run");
        Assert.Equal(StoryKnowledge.Known, restored.Knowledge);
        Assert.True(restored.Completed);
        Assert.Equal(offer.OccurrenceId, Assert.Single(provider.Occurrences("salvage-run").Records).OccurrenceId);
    }

    [Fact]
    public void AModuleConstructedAfterASessionStartedStaysUnavailableUntilTheNextStart()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        world.StartSession();                    // the session began before the module existed
        using var service = world.Service(host);
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        provider.Register(Definition());
        Assert.Equal(StoryKnowledge.Unavailable, provider.IsCompleted("salvage-run").Knowledge);
        Assert.Contains("no session has started", service.ReadinessDetail);
        Assert.Equal(StoryTransitionStatus.Unavailable, provider.Offer("salvage-run").Status);
        world.StartAndRestore();
        Assert.Equal(StoryKnowledge.Known, provider.IsCompleted("salvage-run").Knowledge);
    }

    [Fact]
    public void EveryQueryMutationAndDisposalChecksTheMainThread()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        int checks = 0;
        var service = world.Service(host, () => { checks++; throw new InvalidOperationException("off-thread"); });
        Assert.Throws<InvalidOperationException>(() => service.AcquireProvider(new object()));
        var relaxed = new FakeWorld();
        var relaxedHost = new FakeHost();
        bool guard = false;
        using var guarded = relaxed.Service(relaxedHost, () => { if (guard) throw new InvalidOperationException("off-thread"); });
        relaxed.StartAndRestore();
        var plugin = new object();
        relaxedHost.Register(plugin, AnimaPlugin);
        var provider = guarded.AcquireProvider(plugin).Provider!;
        var registration = provider.Register(Definition()).Registration!;
        guard = true;
        foreach (Action call in new Action[]
        {
            () => provider.Register(Definition("other")),
            () => provider.Offer("salvage-run"),
            () => provider.Activate(Guid.NewGuid()),
            () => provider.Withdraw(Guid.NewGuid()),
            () => provider.Retire(Guid.NewGuid(), StoryOutcome.Completed),
            () => provider.Occurrences("salvage-run"),
            () => provider.IsCompleted("salvage-run"),
            () => { var _ = provider.Active; },
            () => { var _ = registration.Active; },
            () => registration.Dispose(),
            () => provider.Dispose(),
            () => guarded.Dispose()
        }) Assert.Throws<InvalidOperationException>(call);
        Assert.True(checks > 0);
        guard = false; // the guarded module is disposed on the main thread by the using scope
    }

    // --- occurrences ------------------------------------------------------------------------

    private static IStoryProvider Provider(out FakeWorld world, out FakeHost host, out StoryContentService service,
        StoryRetention retention = StoryRetention.Temporary)
    {
        host = new FakeHost();
        world = new FakeWorld();
        service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        Assert.True(provider.Register(Definition(retention: retention)).Succeeded);
        return provider;
    }

    [Fact]
    public void RepeatedOccurrencesKeepSeparateIdentityAndASingleTerminalOutcome()
    {
        var provider = Provider(out _, out _, out _, StoryRetention.Campaign);
        var first = provider.Offer("salvage-run");
        Assert.True(provider.Activate(first.OccurrenceId).Accepted);
        Assert.True(provider.Retire(first.OccurrenceId, StoryOutcome.Completed).Accepted);
        var second = provider.Offer("salvage-run");
        Assert.NotEqual(first.OccurrenceId, second.OccurrenceId);
        Assert.True(provider.Retire(second.OccurrenceId, StoryOutcome.Failed).Accepted);
        var records = provider.Occurrences("salvage-run").Records;
        Assert.Equal(new[] { first.OccurrenceId, second.OccurrenceId }, records.Select(record => record.OccurrenceId).ToArray());
        // A second terminal call is refused rather than rewriting an authoritative outcome.
        var repeated = provider.Retire(first.OccurrenceId, StoryOutcome.Abandoned);
        Assert.Equal(StoryTransitionStatus.InvalidTransition, repeated.Status);
        Assert.Contains("recorded once", repeated.Detail);
        Assert.Equal(StoryOutcome.Completed, provider.Occurrences("salvage-run").Records[0].Outcome);
        Assert.Equal(StoryTransitionStatus.UnknownOccurrence, provider.Retire(Guid.NewGuid(), StoryOutcome.Completed).Status);
        Assert.Equal(StoryTransitionStatus.InvalidTransition, provider.Offer("not-registered").Status);
    }

    /// <summary>
    /// Temporary retention is a real, bounded policy: an offered job can be withdrawn without leaving
    /// a tombstone, and terminal tombstones are kept to a fixed horizon per definition so a
    /// generated-job consumer cannot exhaust the ledger and starve campaign content.
    /// </summary>
    [Fact]
    public void TemporaryRetentionIsBoundedByWithdrawalAndAFixedTombstoneHorizon()
    {
        var provider = Provider(out _, out _, out var service);
        var withdrawn = provider.Offer("salvage-run");
        Assert.True(provider.Withdraw(withdrawn.OccurrenceId).Accepted);
        Assert.Empty(service.Ledger.Entries);
        Assert.Equal(StoryTransitionStatus.UnknownOccurrence, provider.Withdraw(withdrawn.OccurrenceId).Status);
        // An accepted occurrence is still needed to reconstruct live content, so it is not withdrawable.
        var active = provider.Offer("salvage-run");
        provider.Activate(active.OccurrenceId);
        Assert.Equal(StoryTransitionStatus.InvalidTransition, provider.Withdraw(active.OccurrenceId).Status);

        var tokens = new List<Guid>();
        for (int index = 0; index < StoryLedger.TemporaryTombstoneHorizon + 10; index++)
        {
            var offer = provider.Offer("salvage-run");
            Assert.True(offer.Accepted);
            Assert.True(provider.Retire(offer.OccurrenceId, StoryOutcome.Completed).Accepted);
            tokens.Add(offer.OccurrenceId);
        }
        // Only the newest tombstones are retained; the active occurrence is untouched.
        Assert.Equal(StoryLedger.TemporaryTombstoneHorizon, provider.Occurrences("salvage-run").Records.Count);
        Assert.True(service.Ledger.TryGet(active.OccurrenceId, out _));
        // A token past the horizon reports Unknown; it is never re-offered or re-accepted, because
        // occurrence identities are API-generated and never reused.
        Assert.Equal(StoryTransitionStatus.UnknownOccurrence, provider.Retire(tokens[0], StoryOutcome.Failed).Status);
        Assert.DoesNotContain(tokens[0], provider.Occurrences("salvage-run").Records.Select(record => record.OccurrenceId));
    }

    [Fact]
    public void CampaignOutcomesAreNeverPrunedAndTheirBoundRefusesInstead()
    {
        var provider = Provider(out _, out _, out _, StoryRetention.Campaign);
        for (int index = 0; index < StoryLedger.MaxRetainedPerDefinition; index++)
        {
            var offer = provider.Offer("salvage-run");
            Assert.True(provider.Retire(offer.OccurrenceId, StoryOutcome.Completed,
                new Dictionary<string, string> { ["branch"] = "left" }).Accepted);
        }
        var overflow = provider.Offer("salvage-run");
        var refused = provider.Retire(overflow.OccurrenceId, StoryOutcome.Completed);
        Assert.Equal(StoryTransitionStatus.LimitExceeded, refused.Status);
        Assert.Contains("refusing rather than dropping", refused.Detail);
        var records = provider.Occurrences("salvage-run").Records;
        Assert.Equal(StoryLedger.MaxRetainedPerDefinition, records.Count);
        // Declared choices are never silently dropped or truncated.
        Assert.All(records, record => Assert.Equal("left", record.Choices["branch"]));
    }

    /// <summary>Campaign completion is campaign-only: a temporary tombstone is idempotency, not an authoritative outcome.</summary>
    [Fact]
    public void CompletionCountsCampaignOutcomesOnly()
    {
        var temporary = Provider(out _, out _, out _);
        var job = temporary.Offer("salvage-run");
        Assert.True(temporary.Retire(job.OccurrenceId, StoryOutcome.Completed).Accepted);
        var temporaryAnswer = temporary.IsCompleted("salvage-run");
        Assert.Equal(StoryKnowledge.Known, temporaryAnswer.Knowledge);
        Assert.False(temporaryAnswer.Completed);
        // The tombstone still exists for idempotency; it just does not answer completion.
        Assert.Single(temporary.Occurrences("salvage-run").Records);

        var campaign = Provider(out _, out _, out _, StoryRetention.Campaign);
        var act = campaign.Offer("salvage-run");
        Assert.True(campaign.Retire(act.OccurrenceId, StoryOutcome.Completed).Accepted);
        Assert.True(campaign.IsCompleted("salvage-run").Completed);
        // A temporary occurrence may not carry declared choices at all.
        var rejected = temporary.Offer("salvage-run");
        var refusal = temporary.Retire(rejected.OccurrenceId, StoryOutcome.Completed,
            new Dictionary<string, string> { ["branch"] = "left" });
        Assert.Equal(StoryTransitionStatus.LimitExceeded, refusal.Status);
        Assert.Contains("campaign definitions only", refusal.Detail);
    }

    // --- automatic persistence --------------------------------------------------------------

    [Fact]
    public void TheModuleRegistersItsOwnPersistenceProviderAndReconstructsItsState()
    {
        var provider = Provider(out var world, out var host, out var service, StoryRetention.Campaign);
        Assert.Equal(StoryStateCodec.Owner, world.Persistence.Provider!.Owner);
        Assert.Equal(StoryStateCodec.SchemaVersion, world.Persistence.Provider.SchemaVersion);
        var offered = provider.Offer("salvage-run");
        var active = provider.Offer("salvage-run");
        provider.Activate(active.OccurrenceId);
        var done = provider.Offer("salvage-run");
        provider.Retire(done.OccurrenceId, StoryOutcome.Completed, new Dictionary<string, string> { ["branch"] = "left" });
        var bytes = world.Persistence.Provider.Capture();
        Assert.True(world.Persistence.Provider.Validate(bytes));

        world.StartAndRestore(bytes);
        Assert.Equal(3, service.Ledger.Count);
        Assert.True(service.Ledger.TryGet(offered.OccurrenceId, out var restoredOffer));
        Assert.Equal(StoryOccurrenceState.Offered, restoredOffer.State);
        Assert.True(service.Ledger.TryGet(active.OccurrenceId, out var restoredActive));
        Assert.Equal(StoryOccurrenceState.Active, restoredActive.State);
        Assert.Equal("left", Assert.Single(provider.Occurrences("salvage-run").Records).Choices["branch"]);
        Assert.True(provider.IsCompleted("salvage-run").Completed);
        Assert.False(provider.IsCompleted("other-run").Completed);
    }

    [Fact]
    public void RestoringAnOlderSaveReplacesStateInsteadOfMergingNewerCompletion()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var early = provider.Offer("salvage-run");
        var older = world.Persistence.Provider!.Capture();
        provider.Retire(early.OccurrenceId, StoryOutcome.Completed);
        Assert.True(provider.IsCompleted("salvage-run").Completed);
        world.StartAndRestore(older);
        Assert.False(provider.IsCompleted("salvage-run").Completed);
        Assert.Equal(StoryOccurrenceState.Offered, service.Ledger.Entries.Single().State);
        world.StartAndRestore(null);
        Assert.Empty(service.Ledger.Entries);
    }

    // --- codec ------------------------------------------------------------------------------

    [Fact]
    public void TheStateCodecIsBoundedStrictAndRefusesMalformedPayloads()
    {
        var entry = new StoryOccurrenceEntry(new StoryContentId("anima", "salvage-run"), Guid.NewGuid(),
            StoryRetention.Campaign, 1, StoryOccurrenceState.Retired, StoryOutcome.Completed,
            new[] { new KeyValuePair<string, string>("branch", "left") });
        var bytes = StoryStateCodec.Encode(new[] { entry });
        var decoded = Assert.Single(StoryStateCodec.Decode(bytes));
        Assert.Equal(entry.OccurrenceId, decoded.OccurrenceId);
        Assert.Equal(StoryOutcome.Completed, decoded.Outcome);
        Assert.Equal("left", decoded.Choices["branch"]);
        Assert.False(StoryStateCodec.Validate(bytes.Take(bytes.Length - 1).ToArray()));
        Assert.False(StoryStateCodec.Validate(bytes.Concat(new byte[] { 0 }).ToArray()));
        var newer = (byte[])bytes.Clone();
        newer[4] = 2;
        Assert.False(StoryStateCodec.Validate(newer));
        Assert.False(StoryStateCodec.Validate(new byte[] { 1, 2, 3 }));
        Assert.False(StoryStateCodec.Validate(Array.Empty<byte>()));
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Decode(newer));
        var temporary = new StoryOccurrenceEntry(new StoryContentId("anima", "salvage-run"), Guid.NewGuid(),
            StoryRetention.Temporary, 2, StoryOccurrenceState.Retired, StoryOutcome.Completed,
            new[] { new KeyValuePair<string, string>("branch", "left") });
        Assert.Empty(Assert.Single(StoryStateCodec.Decode(StoryStateCodec.Encode(new[] { temporary }))).Choices);
        Assert.True(StoryStateCodec.MaxBytes <= 1024 * 1024);
    }

    [Fact]
    public void TheCodecValidatesTheOccurrenceTimelineAndRefusesInvalidText()
    {
        var id = new StoryContentId("anima", "salvage-run");
        // Non-positive and duplicate sequences are refused on encode: the sequence is the timeline.
        foreach (var bad in new[] { 0L, -1L })
            Assert.Throws<InvalidDataException>(() => StoryStateCodec.Encode(
                new[] { new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Temporary, bad) }));
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Encode(new[]
        {
            new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Temporary, 5),
            new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Temporary, 5)
        }));
        // A payload whose stored sequence is zero or not increasing is refused on decode.
        var bytes = StoryStateCodec.Encode(new[]
        {
            new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Temporary, 1),
            new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Temporary, 2)
        });
        // header(12) + provider("anima" with its length byte) + local("salvage-run") + occurrence GUID(16)
        int sequenceOffset = 12 + (1 + 5) + (1 + 11) + 16;
        var zeroed = (byte[])bytes.Clone();
        for (int index = 0; index < 8; index++) zeroed[sequenceOffset + index] = 0;
        Assert.False(StoryStateCodec.Validate(zeroed));
        var maxed = (byte[])bytes.Clone();
        for (int index = 0; index < 8; index++) maxed[sequenceOffset + index] = index == 7 ? (byte)0x7f : (byte)0xff;
        // The first row now claims long.MaxValue, so the second row cannot be greater: refused.
        Assert.False(StoryStateCodec.Validate(maxed));
        // Invalid UTF-8 in a choice value is refused instead of decoding to replacement characters.
        var campaign = StoryStateCodec.Encode(new[]
        {
            new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Campaign, 1, StoryOccurrenceState.Retired,
                StoryOutcome.Completed, new[] { new KeyValuePair<string, string>("branch", "left") })
        });
        var index0 = IndexOf(campaign, Encoding.ASCII.GetBytes("left"));
        Assert.True(index0 > 0);
        var invalid = (byte[])campaign.Clone();
        invalid[index0] = 0xff;
        Assert.False(StoryStateCodec.Validate(invalid));
        // An unpaired surrogate cannot be encoded either.
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Encode(new[]
        {
            new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Campaign, 1, StoryOccurrenceState.Retired,
                StoryOutcome.Completed, new[] { new KeyValuePair<string, string>("branch", "\ud800") })
        }));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int index = 0; index + needle.Length <= haystack.Length; index++)
        {
            bool match = true;
            for (int offset = 0; offset < needle.Length && match; offset++) match = haystack[index + offset] == needle[offset];
            if (match) return index;
        }
        return -1;
    }

    // --- fakes ------------------------------------------------------------------------------

    /// <summary>Stands in for the host adapter that resolves a caller object to its loaded plugin.</summary>
    private sealed class FakeHost
    {
        private readonly Dictionary<object, string> _plugins = new();
        internal void Register(object instance, string pluginId) => _plugins[instance] = pluginId;
        internal StoryHostPlugin? Authenticate(object instance)
            => _plugins.TryGetValue(instance, out var pluginId) ? new StoryHostPlugin(pluginId) : null;
    }

    private sealed class FakeWorld
    {
        internal readonly FakePersistence Persistence = new();
        internal readonly FakeLifecycle Lifecycle = new();
        internal Guid SessionId => Lifecycle.CurrentSession?.Id ?? Guid.Empty;

        internal StoryContentService Service(FakeHost host, Action? checkThread = null)
            => new(Persistence, Lifecycle, instance => Segment(host, instance), null, checkThread);

        private static StoryHostPlugin? Segment(FakeHost host, object instance) => host.Authenticate(instance);

        internal void StartSession()
        {
            Lifecycle.Set(new SessionSnapshot(Guid.NewGuid(), SessionPhase.Starting, SessionOrigin.SaveLoad, "slot.save"));
            Lifecycle.Publish(LifecycleEventKind.SessionStarting);
            Lifecycle.Set(new SessionSnapshot(Lifecycle.CurrentSession!.Id, SessionPhase.GameplayInitialized, SessionOrigin.SaveLoad, "slot.save"));
        }
        internal void StartAndRestore(byte[]? bytes = null)
        {
            StartSession();
            Persistence.Provider!.Restore(Lifecycle.CurrentSession!, bytes);
        }
        internal void Invalidate()
        {
            Lifecycle.Set(new SessionSnapshot(Lifecycle.CurrentSession!.Id, SessionPhase.Invalidated, SessionOrigin.SaveLoad, "slot.save"));
            Lifecycle.Publish(LifecycleEventKind.SessionInvalidated);
        }
        internal void FailStart()
        {
            Lifecycle.Set(new SessionSnapshot(Guid.NewGuid(), SessionPhase.Failed, SessionOrigin.SaveLoad, "slot.save"));
            Lifecycle.Publish(LifecycleEventKind.SessionStartFailed);
        }
        /// <summary>Models an owner whose restore threw: the coordinator marks it restore-failed and never retries.</summary>
        internal void FailRestore()
        {
            try { Persistence.Provider!.Restore(Lifecycle.CurrentSession!, new byte[] { 1, 2, 3 }); }
            catch (InvalidDataException) { }
            Persistence.MutationAllowed = false;
        }
    }

    private sealed class FakeLifecycle : ILifecycleApi
    {
        private readonly List<Action<LifecycleEvent>> _subscribers = new();
        public SessionSnapshot? CurrentSession { get; private set; }
        public IReadOnlyList<CapabilityStatus> Capabilities => Array.Empty<CapabilityStatus>();
        public IDisposable Subscribe(string owner, Action<LifecycleEvent> callback)
        {
            _subscribers.Add(callback);
            return new Subscription(() => _subscribers.Remove(callback));
        }
        internal void Set(SessionSnapshot snapshot) => CurrentSession = snapshot;
        internal void Publish(LifecycleEventKind kind)
        {
            foreach (var subscriber in _subscribers.ToArray()) subscriber(new LifecycleEvent(kind, CurrentSession));
        }
        private sealed class Subscription : IDisposable
        {
            private readonly Action _dispose;
            internal Subscription(Action dispose) => _dispose = dispose;
            public void Dispose() => _dispose();
        }
    }

    private sealed class FakePersistence : IPersistenceApi, IPersistenceRegistration
    {
        internal PersistenceProvider? Provider;
        internal bool MutationAllowed = true;
        internal bool OwnerDisposed;
        public IPersistenceRegistration Register(PersistenceProvider provider)
        {
            Assert.Null(Provider);
            Provider = provider;
            return this;
        }
        bool IPersistenceRegistration.MutationAllowed => !OwnerDisposed && MutationAllowed;
        string IPersistenceRegistration.Status => OwnerDisposed ? "inactive" : MutationAllowed ? "ready" : "paused";
        public void Dispose() => OwnerDisposed = true;
    }
}
