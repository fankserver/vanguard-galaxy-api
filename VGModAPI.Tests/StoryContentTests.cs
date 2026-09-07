using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
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
/// <summary>
/// Convenience wrappers that read the CURRENT session from the API itself, so the tests below stay
/// about the behaviour under test. The session-token contract itself is exercised with the real
/// signatures in <see cref="StoryContentTests.AMutationForAnotherSessionIsRefusedBeforeAnythingIsRead"/>.
/// </summary>
internal static class StoryProviderCallExtensions
{
    internal static Guid CurrentSession(this IStoryProvider provider)
        => provider.Occurrences("session-probe").SessionId ?? Guid.Empty;
    internal static StoryTransitionResult Offer(this IStoryProvider provider, string localId)
        => provider.Offer(provider.CurrentSession(), localId);
    internal static StoryTransitionResult Activate(this IStoryProvider provider, Guid occurrenceId)
        => provider.Activate(provider.CurrentSession(), occurrenceId);
    internal static StoryTransitionResult Withdraw(this IStoryProvider provider, Guid occurrenceId)
        => provider.Withdraw(provider.CurrentSession(), occurrenceId);
    internal static StoryTransitionResult Retire(this IStoryProvider provider, Guid occurrenceId, StoryOutcome outcome,
        IReadOnlyDictionary<string, string>? choices = null)
        => provider.Retire(provider.CurrentSession(), occurrenceId, outcome, choices);
}

public sealed class StoryContentTests
{
    private const string AnimaPlugin = "com.fank.anima";
    private const string OtherPlugin = "com.other.custommission";

    private static StoryMissionDefinition Definition(string local = "salvage-run",
        StoryRetention retention = StoryRetention.Temporary, IEnumerable<string>? choiceKeys = null)
        => new(local, "Salvage run", "Recover the drifting cargo.",
            new[] { new StoryStep("Reach the wreck", new[] { StoryObjective.TravelTo("poi-guid-1", 5) }) },
            new[] { new StoryReward(StoryRewardKind.Credits, 500) }, StoryDifficulty.Normal, retention,
            choiceKeys: choiceKeys ?? (retention == StoryRetention.Campaign ? new[] { "branch" } : null));

    /// <summary>A campaign definition declaring the largest supported choice payload.</summary>
    private static StoryMissionDefinition WorstDefinition(string local = "salvage-run")
        => Definition(local, StoryRetention.Campaign,
            Enumerable.Range(0, StoryMissionDefinition.MaxChoiceKeys).Select(index => "k" + index + new string('x', StoryMissionDefinition.MaxChoiceKeyBytes - 2)));

    private static Dictionary<string, string> WorstChoices(StoryMissionDefinition definition)
        => definition.ChoiceKeys.ToDictionary(key => key, _ => new string('v', StoryMissionDefinition.MaxChoiceValueBytes), StringComparer.Ordinal);

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
        var anima = StoryProviderIdentity.Segment(new StoryHostPlugin(AnimaPlugin, typeof(StoryContentTests).Assembly));
        Assert.Equal(anima, StoryProviderIdentity.Segment(new StoryHostPlugin(AnimaPlugin, typeof(StoryContentTests).Assembly)));
        Assert.True(StoryContentId.IsValidSegment(anima));
        Assert.NotEqual(anima, StoryProviderIdentity.Segment(new StoryHostPlugin(OtherPlugin, typeof(StoryContentTests).Assembly)));
        // Plugin IDs that slug to the same readable text keep different segments through the digest.
        Assert.NotEqual(StoryProviderIdentity.Segment(new StoryHostPlugin("com.a.anima", typeof(StoryContentTests).Assembly)),
            StoryProviderIdentity.Segment(new StoryHostPlugin("com-a-anima", typeof(StoryContentTests).Assembly)));
        // Identities that would otherwise produce an invalid segment still resolve to a valid one.
        foreach (var odd in new[] { "1", ".", "ÄÖÜ", new string('x', 128) })
            Assert.True(StoryContentId.IsValidSegment(StoryProviderIdentity.Segment(new StoryHostPlugin(odd, typeof(StoryContentTests).Assembly))));
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
        var segment = StoryProviderIdentity.Segment(new StoryHostPlugin(AnimaPlugin, typeof(StoryContentTests).Assembly));
        Assert.Equal(StoryBindingStatus.Bound, bindings.Bind(segment, AnimaPlugin));
        Assert.Equal(StoryBindingStatus.AlreadyBoundToSelf, bindings.Bind(segment, AnimaPlugin));
        Assert.Equal(StoryBindingStatus.Conflict, bindings.Bind(segment, OtherPlugin));
        Assert.Equal(StoryBindingStatus.Bound, bindings.Bind(StoryProviderIdentity.Segment(new StoryHostPlugin(OtherPlugin, typeof(StoryContentTests).Assembly)), OtherPlugin));
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
        // A live lease is never handed out twice, so one holder's Dispose cannot revoke another's.
        var again = service.AcquireProvider(animaPlugin);
        Assert.Equal(StoryProviderStatus.AlreadyAcquired, again.Status);
        Assert.Null(again.Provider);
        Assert.True(anima.Provider.Active);
    }

    /// <summary>
    /// A lease is not reference counted. Re-acquisition while one is live is refused outright, and
    /// after disposal a NEW lease is issued while the old handle stays dead; persisted occurrences
    /// survive, because releasing a lease never deletes saved data.
    /// </summary>
    [Fact]
    public void ALeaseIsRefusedWhileLiveAndReissuedAfterDisposalWithoutLosingOccurrences()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var first = service.AcquireProvider(plugin).Provider!;
        Assert.True(first.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
        var occurrence = first.Offer("salvage-run");
        Assert.True(first.Retire(occurrence.OccurrenceId, StoryOutcome.Completed).Accepted);
        Assert.Equal(StoryProviderStatus.AlreadyAcquired, service.AcquireProvider(plugin).Status);

        first.Dispose();
        var second = service.AcquireProvider(plugin);
        Assert.Equal(StoryProviderStatus.Acquired, second.Status);
        Assert.NotSame(first, second.Provider);
        Assert.False(first.Active);
        Assert.Equal(StoryTransitionStatus.Unavailable, first.Offer("salvage-run").Status);
        // The ledger kept the occurrence; only the registration was released with the lease.
        Assert.Equal(first.ProviderId, second.Provider!.ProviderId);
        Assert.Single(service.Ledger.Entries);
        Assert.True(second.Provider.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
        Assert.True(second.Provider.IsCompleted("salvage-run").Completed);
    }

    /// <summary>
    /// The provider identity is associated with the assembly that ACTUALLY called, captured at the
    /// non-inlined public entry point rather than taken from an argument. Passing another plugin's
    /// instance from a different assembly is therefore refused. This is an ordinary-use boundary:
    /// code inside the plugin's own assembly, or reflection, is explicitly not covered.
    /// </summary>
    [Fact]
    public void AcquiringWithAnotherPluginsInstanceFromAnotherAssemblyIsRefused()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var animaPlugin = new object();
        host.Register(animaPlugin, AnimaPlugin);
        // Control: the plugin's own assembly calls directly and is accepted.
        var owned = service.AcquireProvider(animaPlugin);
        Assert.Equal(StoryProviderStatus.Acquired, owned.Status);

        // An ordinary API call made from a DIFFERENT assembly, passing that same instance.
        var foreign = ForeignAssemblyCaller();
        var spoofed = foreign(service, animaPlugin);
        Assert.Equal(StoryProviderStatus.CallerMismatch, spoofed.Status);
        Assert.Null(spoofed.Provider);
        Assert.DoesNotContain(owned.Provider!.ProviderId, spoofed.Diagnostic);

        // A plugin the host loaded from another assembly is refused even for its own caller.
        var elsewhere = new object();
        host.Register(elsewhere, OtherPlugin, typeof(string).Assembly);
        Assert.Equal(StoryProviderStatus.CallerMismatch, service.AcquireProvider(elsewhere).Status);
        // The refusal changed nothing: the legitimate lease still works.
        Assert.True(owned.Provider.Register(Definition()).Succeeded);
    }

    /// <summary>Emits a real method in a separate dynamic assembly so the captured caller is genuinely foreign.</summary>
    private static Func<IStoryApi, object, StoryProviderResult> ForeignAssemblyCaller()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("VGModAPI.Tests.ForeignCaller"), AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("main").DefineType("Caller", TypeAttributes.Public);
        var method = type.DefineMethod("Call", MethodAttributes.Public | MethodAttributes.Static,
            typeof(StoryProviderResult), new[] { typeof(IStoryApi), typeof(object) });
        method.SetImplementationFlags(MethodImplAttributes.NoInlining);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(IStoryApi).GetMethod(nameof(IStoryApi.AcquireProvider))!);
        il.Emit(OpCodes.Ret);
        return (Func<IStoryApi, object, StoryProviderResult>)type.CreateType()!
            .GetMethod("Call")!.CreateDelegate(typeof(Func<IStoryApi, object, StoryProviderResult>));
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

    /// <summary>
    /// A registration handle from a RELEASED lease is stale. Disposing it in an ordinary teardown
    /// must not remove the live registration a re-acquired lease made for the same local ID.
    /// </summary>
    [Fact]
    public void AStaleRegistrationHandleCannotUnregisterALiveOne()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var first = service.AcquireProvider(plugin).Provider!;
        var stale = first.Register(Definition(retention: StoryRetention.Campaign)).Registration!;
        first.Dispose();

        var second = service.AcquireProvider(plugin).Provider!;
        var live = second.Register(Definition(retention: StoryRetention.Campaign)).Registration!;
        stale.Dispose();                                  // ordinary teardown of the old handle

        Assert.True(live.Active);
        var occurrence = second.Offer("salvage-run");
        Assert.True(occurrence.Accepted);
        Assert.True(second.Retire(occurrence.OccurrenceId, StoryOutcome.Completed,
            new Dictionary<string, string> { ["branch"] = "left" }).Accepted);
        Assert.True(second.IsCompleted("salvage-run").Completed);
        // The live registration is still the one that can be released by its OWN handle.
        live.Dispose();
        Assert.False(live.Active);
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

    /// <summary>
    /// A save block that appears AFTER a valid restore still makes answers unavailable: the ledger
    /// then holds accepted state that will not reach disk, which is exactly what the module refuses
    /// to report as this save's known history. The same rule already refuses mutations.
    /// </summary>
    [Fact]
    public void AnOwnerBlockedAfterAValidRestoreStopsAnsweringUntilItRecovers()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        provider.Register(Definition(retention: StoryRetention.Campaign));
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Retire(occurrence.OccurrenceId, StoryOutcome.Completed).Accepted);
        Assert.True(provider.IsCompleted("salvage-run").Completed);

        world.Persistence.MutationAllowed = false;      // the owner is paused or blocked mid-session
        var completion = provider.IsCompleted("salvage-run");
        Assert.Equal(StoryKnowledge.Unavailable, completion.Knowledge);
        Assert.Null(completion.Completed);
        var occurrences = provider.Occurrences("salvage-run");
        Assert.Equal(StoryKnowledge.Unavailable, occurrences.Knowledge);
        Assert.Empty(occurrences.Records);
        Assert.Equal(StoryTransitionStatus.Unavailable, provider.Offer("salvage-run").Status);

        // Recovery answers for the SAME session again, with the history that was recorded in it.
        world.Persistence.MutationAllowed = true;
        var resumed = provider.IsCompleted("salvage-run");
        Assert.Equal(StoryKnowledge.Known, resumed.Knowledge);
        Assert.True(resumed.Completed);
        Assert.Equal(world.SessionId, resumed.SessionId);
        Assert.Single(provider.Occurrences("salvage-run").Records);
    }

    /// <summary>The same rule against the REAL coordinator, blocked by an owner unregistering mid-session.</summary>
    [Fact]
    public void TheRealCoordinatorBlockingASessionMakesStoryAnswersUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "vg-story-" + Guid.NewGuid().ToString("N"));
        using var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected subscriber fault", error));
        try
        {
            using var persistence = new PersistenceService(hub, new GenerationStore(root), path => path, _ => new string('a', 64));
            var control = persistence.Register(new PersistenceProvider("vgmodapi.tests.control", 1,
                capture: () => new byte[] { 1 }, restore: (_, _) => { }, validate: bytes => bytes.Length == 1));
            var host = new FakeHost();
            using var service = new StoryContentService(persistence, hub, host.Authenticate, null, hub.CheckThread);
            var session = hub.Begin(SessionOrigin.NewGame, null);
            hub.PlayerReady(session);
            hub.GameplayInitialized(session);

            var plugin = new object();
            host.Register(plugin, AnimaPlugin);
            var provider = service.AcquireProvider(plugin).Provider!;
            Assert.True(provider.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
            var occurrence = provider.Offer("salvage-run");
            Assert.True(occurrence.Accepted);
            Assert.True(provider.Retire(occurrence.OccurrenceId, StoryOutcome.Completed).Accepted);
            Assert.Equal(StoryKnowledge.Known, provider.IsCompleted("salvage-run").Knowledge);

            // Another owner unregistering mid-session is a real coordinator load block.
            control.Dispose();
            Assert.Equal("load-blocked", service.PersistenceStatus);
            var blocked = provider.IsCompleted("salvage-run");
            Assert.Equal(StoryKnowledge.Unavailable, blocked.Knowledge);
            Assert.Null(blocked.Completed);
            Assert.Empty(provider.Occurrences("salvage-run").Records);
            Assert.Equal(StoryTransitionStatus.Unavailable, provider.Offer("salvage-run").Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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

    /// <summary>
    /// The module is API-root scoped and must exist before any session. The REAL coordinator refuses
    /// a new owner once a session is live, so the module refuses at construction rather than
    /// promising an availability state it could not deliver, and it refuses BEFORE registering, so
    /// no owner, subscription or save pause is left behind for anyone else.
    /// </summary>
    [Fact]
    public void ConstructingTheModuleMidSessionThrowsAndLeavesTheRealCoordinatorUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "vg-story-" + Guid.NewGuid().ToString("N"));
        using var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected subscriber fault", error));
        try
        {
            using var persistence = new PersistenceService(hub, new GenerationStore(root), path => path, _ => new string('a', 64));
            byte[]? restored = null;
            using var control = persistence.Register(new PersistenceProvider("vgmodapi.tests.control", 1,
                capture: () => new byte[] { 1 }, restore: (_, bytes) => restored = bytes, validate: bytes => bytes.Length == 1));
            var session = hub.Begin(SessionOrigin.NewGame, null);
            hub.PlayerReady(session);
            hub.GameplayInitialized(session);

            var host = new FakeHost();
            var failure = Assert.Throws<InvalidOperationException>(
                () => new StoryContentService(persistence, hub, host.Authenticate, null, hub.CheckThread));
            Assert.Contains("before a session begins", failure.Message);
            // No story owner exists, and the other registered owner is neither paused nor faulted.
            Assert.True(control.MutationAllowed);
            Assert.Equal("ready", control.Status);
            Assert.Null(restored);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>World reservations describe the loaded save, so they end with it.</summary>
    [Fact]
    public void WorldReservationsAreDroppedAtASessionBoundaryButNotByProviderTeardown()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        var reserved = StoryContentPolicy.Identifier(new StoryContentId(provider.ProviderId, "salvage-run"));
        service.ReserveExistingIdentifiers(new[] { reserved });
        var refused = provider.Register(Definition());
        Assert.Equal(StoryRegistrationStatus.IdentifierInUse, refused.Status);

        // Within the SAME session the reservation survives provider teardown: the world still owns it.
        provider.Dispose();
        var again = service.AcquireProvider(plugin).Provider!;
        Assert.Equal(StoryRegistrationStatus.IdentifierInUse, again.Register(Definition()).Status);

        // The next session is a different world; the reservation does not follow it.
        world.StartAndRestore();
        Assert.Equal(0, service.Registry.ReservedCount);
        Assert.True(again.Register(Definition()).Succeeded);
    }

    [Fact]
    public void EveryQueryMutationAndDisposalChecksTheMainThread()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        int checks = 0;
        // Construction is main-thread-only too, so the guard fires before anything is registered.
        Assert.Throws<InvalidOperationException>(
            () => world.Service(host, () => { checks++; throw new InvalidOperationException("off-thread"); }));
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

    /// <summary>
    /// Campaign outcomes are never pruned, so the per-definition bound has to be spent at OFFER time:
    /// an occurrence is refused before it exists rather than admitted and then stranded active with
    /// an outcome it could never record. The bound counts retired and unresolved occurrences alike.
    /// </summary>
    [Fact]
    public void TheCampaignBoundRefusesTheOfferSoNoAdmittedOccurrenceIsStranded()
    {
        var provider = Provider(out _, out _, out var service, StoryRetention.Campaign);
        for (int index = 0; index < StoryLedger.MaxRetainedPerDefinition; index++)
        {
            var offer = provider.Offer("salvage-run");
            Assert.True(offer.Accepted);
            Assert.True(provider.Retire(offer.OccurrenceId, StoryOutcome.Completed,
                new Dictionary<string, string> { ["branch"] = "left" }).Accepted);
        }
        int before = service.Ledger.Count;
        var overflow = provider.Offer("salvage-run");
        Assert.Equal(StoryTransitionStatus.LimitExceeded, overflow.Status);
        Assert.Equal(Guid.Empty, overflow.OccurrenceId);
        Assert.Contains("could never retire", overflow.Detail);
        Assert.Equal(before, service.Ledger.Count);
        var records = provider.Occurrences("salvage-run").Records;
        Assert.Equal(StoryLedger.MaxRetainedPerDefinition, records.Count);
        // Declared choices are never silently dropped or truncated.
        Assert.All(records, record => Assert.Equal("left", record.Choices["branch"]));
    }

    /// <summary>
    /// The same bound counts occurrences that are merely OFFERED: 48 concurrent offers are admitted,
    /// the 49th is refused before any of them retires, and every admitted one still retires.
    /// </summary>
    [Fact]
    public void UnresolvedCampaignOccurrencesConsumeTheirDefinitionsBoundAndAllRemainRetirable()
    {
        var provider = Provider(out _, out _, out var service, StoryRetention.Campaign);
        var admitted = new List<Guid>();
        for (int index = 0; index < StoryLedger.MaxRetainedPerDefinition; index++)
        {
            var offer = provider.Offer("salvage-run");
            Assert.True(offer.Accepted);
            admitted.Add(offer.OccurrenceId);
        }
        int before = service.Ledger.Count;
        var refused = provider.Offer("salvage-run");
        Assert.Equal(StoryTransitionStatus.LimitExceeded, refused.Status);
        Assert.Equal(before, service.Ledger.Count);
        // Everything that was admitted can still record its outcome; none is stranded.
        foreach (var occurrence in admitted)
            Assert.True(provider.Retire(occurrence, StoryOutcome.Completed,
                new Dictionary<string, string> { ["branch"] = "left" }).Accepted);
        Assert.Equal(StoryLedger.MaxRetainedPerDefinition, provider.Occurrences("salvage-run").Records.Count);
        // A crafted payload that exceeds the same sum is refused by the shared bounds, not restored.
        var rows = Enumerable.Range(1, StoryLedger.MaxRetainedPerDefinition + 1)
            .Select(index => new StoryOccurrenceEntry(new StoryContentId("anima", "salvage-run"), Guid.NewGuid(),
                StoryRetention.Campaign, index))
            .ToArray();
        Assert.Contains("campaign occurrence cap", StoryLedger.RefuseBounds(rows));
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Encode(rows));
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
        Assert.Equal(StoryTransitionStatus.InvalidTransition, refusal.Status);
        Assert.Contains("not declared by this definition", refusal.Detail);
        // The ledger refuses the same thing on its own, without the definition in hand.
        Assert.Equal(StoryLedgerStatus.LimitExceeded, TemporaryLedgerChoiceRefusal(out var ledgerDetail));
        Assert.Contains("campaign definitions only", ledgerDetail);
    }

    private static StoryLedgerStatus TemporaryLedgerChoiceRefusal(out string diagnostic)
    {
        var id = new StoryContentId("anima", "salvage-run");
        var ledger = new StoryLedger();
        var occurrence = Guid.NewGuid();
        ledger.Offer(id, StoryRetention.Temporary, occurrence, 0, out _);
        return ledger.Retire(id, occurrence, StoryOutcome.Completed,
            new Dictionary<string, string> { ["branch"] = "left" }, out diagnostic);
    }

    /// <summary>
    /// The choices path of a retirement answers exactly like the choice-free path: availability,
    /// lease and session come first, then ownership by the ledger's own rules, and only then the
    /// definition. A foreign retirement never reveals the owner's local ID either way.
    /// </summary>
    [Fact]
    public void RetiringWithChoicesChecksAvailabilityAndOwnershipBeforeAnyDefinitionLookup()
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
        Assert.True(anima.Register(Definition("secret-arc", StoryRetention.Campaign)).Succeeded);
        var occurrence = anima.Offer("secret-arc");
        Assert.True(occurrence.Accepted);

        var choices = new Dictionary<string, string> { ["branch"] = "left" };
        var stolen = other.Retire(occurrence.OccurrenceId, StoryOutcome.Completed, choices);
        Assert.Equal(StoryTransitionStatus.ForeignOwner, stolen.Status);
        Assert.DoesNotContain("secret-arc", stolen.Detail);
        // Identical to the choice-free path.
        var stolenWithout = other.Retire(occurrence.OccurrenceId, StoryOutcome.Completed);
        Assert.Equal(StoryTransitionStatus.ForeignOwner, stolenWithout.Status);
        Assert.Equal(stolenWithout.Detail, stolen.Detail);

        // A blocked owner and an inactive lease report Unavailable, not a lookup result.
        world.Persistence.MutationAllowed = false;
        Assert.Equal(StoryTransitionStatus.Unavailable, anima.Retire(occurrence.OccurrenceId, StoryOutcome.Completed, choices).Status);
        world.Persistence.MutationAllowed = true;
        var lease = service.AcquireProvider(otherPlugin).Provider;
        Assert.Null(lease);                                   // still held; use the live one
        other.Dispose();
        Assert.Equal(StoryTransitionStatus.Unavailable, other.Retire(occurrence.OccurrenceId, StoryOutcome.Completed, choices).Status);
        // Nothing was mutated by any refusal: the owner can still record its outcome.
        Assert.True(anima.Retire(occurrence.OccurrenceId, StoryOutcome.Completed, choices).Accepted);
    }

    /// <summary>
    /// Only a terminal record carries choices, on both sides of the codec, and a recorded outcome
    /// REPLACES whatever the entry held. Otherwise a crafted payload could restore an unresolved row
    /// with choices, and a legitimate retirement would merge past the bound and fail every owner's
    /// next capture.
    /// </summary>
    [Fact]
    public void UnresolvedOccurrencesCarryNoChoicesAndAnOutcomeReplacesThemRatherThanMerging()
    {
        var id = new StoryContentId("anima", "salvage-run");
        var crafted = new[]
        {
            new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Campaign, 1, StoryOccurrenceState.Offered, null,
                new[] { new KeyValuePair<string, string>("ghost", "value") })
        };
        Assert.Contains("records no declared choices", StoryLedger.RefuseBounds(crafted));
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Encode(crafted));
        // The same state crafted at the byte level, by demoting a terminal row that carries choices.
        var payload = StoryStateCodec.Encode(new[]
        {
            new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Campaign, 1, StoryOccurrenceState.Retired,
                StoryOutcome.Completed, new[] { new KeyValuePair<string, string>("ghost", "value") })
        });
        int stateOffset = 12 + (1 + 5) + (1 + 11) + 16 + 8;   // header, provider, local, identity, sequence
        Assert.True(StoryStateCodec.Validate(payload));
        payload[stateOffset] = (byte)StoryOccurrenceState.Offered;
        payload[stateOffset + 1] = 0;                          // no outcome, as an unresolved row has none
        Assert.False(StoryStateCodec.Validate(payload));
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Decode(payload));

        // A retirement replaces the recorded choices; nothing accumulates across the transition.
        var ledger = new StoryLedger();
        var occurrence = Guid.NewGuid();
        Assert.Equal(StoryLedgerStatus.Accepted, ledger.Offer(id, StoryRetention.Campaign, occurrence, 512, out _));
        Assert.Equal(StoryLedgerStatus.Accepted, ledger.Retire(id, occurrence, StoryOutcome.Completed,
            new Dictionary<string, string> { ["branch"] = "left" }, out _));
        Assert.True(ledger.TryGet(occurrence, out var retired));
        Assert.Equal(new[] { "branch" }, retired.Choices.Keys.ToArray());
        Assert.True(StoryStateCodec.Validate(StoryStateCodec.Encode(ledger.Entries)));
    }

    /// <summary>
    /// A provider finds its unresolved occurrences again after a reload without having stored a
    /// single identity itself: that is what "the API owns this state" has to mean in practice.
    /// </summary>
    [Fact]
    public void UnresolvedOccurrencesAreDiscoverableAfterAReloadWithoutProviderSideStorage()
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
        Assert.True(anima.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
        Assert.True(other.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
        var offered = anima.Offer("salvage-run");
        var active = anima.Offer("salvage-run");
        Assert.True(anima.Activate(active.OccurrenceId).Accepted);
        var done = anima.Offer("salvage-run");
        Assert.True(anima.Retire(done.OccurrenceId, StoryOutcome.Completed).Accepted);
        var foreign = other.Offer("salvage-run");
        var bytes = world.Persistence.Provider!.Capture();

        world.StartAndRestore(bytes);
        var unresolved = anima.Unresolved("salvage-run");
        Assert.Equal(StoryKnowledge.Known, unresolved.Knowledge);
        Assert.Equal(world.SessionId, unresolved.SessionId);
        Assert.Equal(new[] { offered.OccurrenceId, active.OccurrenceId },
            unresolved.Occurrences.Select(item => item.OccurrenceId).ToArray());
        Assert.Equal(new[] { StoryOccurrenceStage.Offered, StoryOccurrenceStage.Active },
            unresolved.Occurrences.Select(item => item.Stage).ToArray());
        Assert.All(unresolved.Occurrences, item =>
        {
            Assert.Equal(StoryRetention.Campaign, item.Retention);
            Assert.Null(item.Outcome);
            Assert.Empty(item.Choices);
            Assert.Equal(anima.ProviderId, item.Id.Provider);
        });
        // Retired occurrences stay in the retained query, not in this one.
        Assert.DoesNotContain(done.OccurrenceId, unresolved.Occurrences.Select(item => item.OccurrenceId));
        Assert.Single(anima.Occurrences("salvage-run").Records);
        // Another provider's unresolved content is not listed here.
        Assert.DoesNotContain(foreign.OccurrenceId, unresolved.Occurrences.Select(item => item.OccurrenceId));
        Assert.Equal(foreign.OccurrenceId, Assert.Single(other.Unresolved("salvage-run").Occurrences).OccurrenceId);

        // The listed identities are usable: they are what a provider transitions after a reload.
        var session = unresolved.SessionId!.Value;
        Assert.True(anima.Activate(session, offered.OccurrenceId).Accepted);
        Assert.True(anima.Retire(session, active.OccurrenceId, StoryOutcome.Failed).Accepted);
        Assert.Single(anima.Unresolved("salvage-run").Occurrences);
        // Unavailable answers carry no snapshots and no session.
        world.Persistence.MutationAllowed = false;
        var blocked = anima.Unresolved("salvage-run");
        Assert.Equal(StoryKnowledge.Unavailable, blocked.Knowledge);
        Assert.Null(blocked.SessionId);
        Assert.Empty(blocked.Occurrences);
    }

    /// <summary>
    /// Occurrence identities survive a reload unchanged, so the identity alone cannot tell a delayed
    /// caller that the world it observed is gone. Every mutation therefore states the session it
    /// believes it is acting in, and a mismatch is refused before anything is read or written.
    /// </summary>
    [Fact]
    public void AMutationForAnotherSessionIsRefusedBeforeAnythingIsRead()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        Assert.True(provider.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
        var first = world.SessionId;
        var occurrence = provider.Offer(first, "salvage-run");
        Assert.True(occurrence.Accepted);
        var bytes = world.Persistence.Provider!.Capture();

        // The SAME save is reloaded: identities are identical, the session is not.
        world.StartAndRestore(bytes);
        var reloaded = world.SessionId;
        Assert.NotEqual(first, reloaded);
        int before = service.Ledger.Count;
        foreach (var stale in new[]
        {
            provider.Offer(first, "salvage-run"),
            provider.Activate(first, occurrence.OccurrenceId),
            provider.Withdraw(first, occurrence.OccurrenceId),
            provider.Retire(first, occurrence.OccurrenceId, StoryOutcome.Completed),
            provider.Retire(first, occurrence.OccurrenceId, StoryOutcome.Completed, new Dictionary<string, string> { ["branch"] = "left" }),
            provider.Retire(Guid.NewGuid(), occurrence.OccurrenceId, StoryOutcome.Completed)
        })
        {
            Assert.Equal(StoryTransitionStatus.StaleSession, stale.Status);
            Assert.DoesNotContain(reloaded.ToString(), stale.Detail);
        }
        Assert.Equal(before, service.Ledger.Count);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var untouched));
        Assert.Equal(StoryOccurrenceState.Offered, untouched.State);

        // Re-reading the current session is all a provider needs to continue.
        var current = provider.Unresolved("salvage-run").SessionId!.Value;
        Assert.Equal(reloaded, current);
        Assert.True(provider.Activate(current, occurrence.OccurrenceId).Accepted);
        // An unavailable module still answers Unavailable rather than StaleSession.
        world.Persistence.MutationAllowed = false;
        Assert.Equal(StoryTransitionStatus.Unavailable, provider.Offer(current, "salvage-run").Status);
    }

    /// <summary>
    /// Declared choices are declared UP FRONT, because their worst-case persisted size is reserved
    /// when the occurrence is offered. An undeclared key, an oversized value or a definition whose
    /// declared choices would not fit the per-occurrence bound is refused, never accepted into space
    /// that was never held for it.
    /// </summary>
    [Fact]
    public void OnlyDeclaredChoicesWithinTheReservedBoundAreAccepted()
    {
        var provider = Provider(out _, out _, out _, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        var undeclared = provider.Retire(occurrence.OccurrenceId, StoryOutcome.Completed,
            new Dictionary<string, string> { ["ending"] = "left" });
        Assert.Equal(StoryTransitionStatus.InvalidTransition, undeclared.Status);
        Assert.Contains("'ending' is not declared", undeclared.Detail);
        var oversized = provider.Retire(occurrence.OccurrenceId, StoryOutcome.Completed,
            new Dictionary<string, string> { ["branch"] = new string('v', StoryMissionDefinition.MaxChoiceValueBytes + 1) });
        Assert.Equal(StoryTransitionStatus.LimitExceeded, oversized.Status);
        Assert.Contains("encoded bytes", oversized.Detail);
        // Both refusals changed nothing, so the declared outcome can still be recorded.
        Assert.True(provider.Retire(occurrence.OccurrenceId, StoryOutcome.Completed,
            new Dictionary<string, string> { ["branch"] = "left" }).Accepted);

        // A definition whose declared choices exceed the per-occurrence bound cannot exist at all.
        var tooMany = Enumerable.Range(0, StoryMissionDefinition.MaxChoiceKeys + 1).Select(index => "k" + index);
        Assert.Throws<ArgumentException>(() => Definition("big", StoryRetention.Campaign, tooMany));
        Assert.Throws<ArgumentException>(() => Definition("big", StoryRetention.Campaign, new[] { new string('k', StoryMissionDefinition.MaxChoiceKeyBytes + 1) }));
        // Temporary definitions retain no choices, so they may not declare any.
        Assert.Throws<ArgumentException>(() => Definition("job", StoryRetention.Temporary, new[] { "branch" }));
        Assert.Equal(StoryMissionDefinition.MaxChoiceKeys * (2 + StoryMissionDefinition.MaxChoiceKeyBytes + 2 + StoryMissionDefinition.MaxChoiceValueBytes),
            WorstDefinition().ReservedChoiceBytes);
        Assert.True(WorstDefinition().ReservedChoiceBytes <= StoryMissionDefinition.MaxChoiceBytesPerOccurrence);
    }

    /// <summary>
    /// The provider budget covers the OUTCOME as well as the row: offering reserves the worst-case
    /// declared-choice payload, so an admitted occurrence can always be retired even after every
    /// other provider has filled its own budget. An offer that cannot reserve that space is refused
    /// before anything is recorded, rather than stranding an occurrence that can never be finished.
    /// </summary>
    [Fact]
    public void AnAdmittedOccurrenceCanAlwaysRecordItsWorstCaseOutcome()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var leases = new List<IStoryProvider>();
        for (int index = 0; index < StoryProviderBindings.MaxProviders; index++)
        {
            var plugin = new object();
            host.Register(plugin, "com.test.plugin" + index);
            var lease = service.AcquireProvider(plugin).Provider!;
            Assert.True(lease.Register(WorstDefinition()).Succeeded);
            leases.Add(lease);
        }
        var reserved = new List<Guid>();
        foreach (var lease in leases)
        {
            var first = lease.Offer("salvage-run");
            Assert.True(first.Accepted);
            reserved.Add(first.OccurrenceId);
            // Fill the rest of this provider's budget.
            while (lease.Offer("salvage-run").Accepted) { }
            int before = service.Ledger.Count;
            var refusal = lease.Offer("salvage-run");
            Assert.Equal(StoryTransitionStatus.LimitExceeded, refusal.Status);
            Assert.Contains("payload budget", refusal.Detail);
            Assert.Equal(Guid.Empty, refusal.OccurrenceId);
            // Refused BEFORE mutating: the ledger is byte-for-byte what it was.
            Assert.Equal(before, service.Ledger.Count);
        }
        // Every provider is full, and every reserved outcome is still recordable at full size.
        var worstPayload = WorstChoices(WorstDefinition());
        for (int index = 0; index < leases.Count; index++)
            Assert.True(leases[index].Retire(reserved[index], StoryOutcome.Completed, worstPayload).Accepted);
        // The whole ledger still captures, so no capture failure can block every owner's saves.
        var bytes = world.Persistence.Provider!.Capture();
        Assert.True(bytes.Length <= StoryStateCodec.MaxBytes);
        Assert.True(StoryStateCodec.Validate(bytes));
    }

    /// <summary>The reservation is persisted, so a reload leaves exactly the same outcome capacity.</summary>
    [Fact]
    public void ReservedOutcomeCapacitySurvivesASaveAndReload()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        var definition = WorstDefinition();
        Assert.True(provider.Register(definition).Succeeded);
        var pending = provider.Offer("salvage-run");
        var modest = provider.Offer("salvage-run");
        while (provider.Offer("salvage-run").Accepted) { }
        Assert.Equal(StoryTransitionStatus.LimitExceeded, provider.Offer("salvage-run").Status);
        int offered = service.Ledger.Count;
        var bytes = world.Persistence.Provider!.Capture();

        world.StartAndRestore(bytes);
        Assert.Equal(offered, service.Ledger.Count);
        Assert.True(service.Ledger.TryGet(pending.OccurrenceId, out var restored));
        Assert.Equal(definition.ReservedChoiceBytes, restored.ChoiceReservation);
        // Same budget after the reload: still full, and the pending outcome is still recordable.
        Assert.Equal(StoryTransitionStatus.LimitExceeded, provider.Offer("salvage-run").Status);
        Assert.True(provider.Retire(pending.OccurrenceId, StoryOutcome.Completed, WorstChoices(definition)).Accepted);
        // A worst-case outcome spends exactly what was reserved for it, so it frees nothing.
        Assert.Equal(StoryTransitionStatus.LimitExceeded, provider.Offer("salvage-run").Status);
        // A smaller outcome releases the reservation it did not use, so the provider can offer again.
        Assert.True(provider.Retire(modest.OccurrenceId, StoryOutcome.Failed,
            new Dictionary<string, string> { [definition.ChoiceKeys[0]] = "v" }).Accepted);
        Assert.True(provider.Offer("salvage-run").Accepted);
    }

    /// <summary>
    /// The global 2048 cap is a backstop, not the rule a provider lives under: each bound provider
    /// owns 64 occurrences outright, so a generated-job consumer cannot make another mod's offers
    /// fail. The provider count is bounded for the same reason.
    /// </summary>
    [Fact]
    public void OneProvidersOccurrencesCannotConsumeAnotherProvidersShare()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var greedyPlugin = new object();
        var quietPlugin = new object();
        host.Register(greedyPlugin, AnimaPlugin);
        host.Register(quietPlugin, OtherPlugin);
        var greedy = service.AcquireProvider(greedyPlugin).Provider!;
        var quiet = service.AcquireProvider(quietPlugin).Provider!;
        // Many definitions of ONE owner still share that owner's quota.
        for (int index = 0; index < 4; index++) Assert.True(greedy.Register(Definition("job-" + index)).Succeeded);
        Assert.True(quiet.Register(Definition()).Succeeded);
        int accepted = 0;
        for (int index = 0; index < StoryLedger.MaxOccurrencesPerProvider + 8; index++)
            if (greedy.Offer("job-" + (index % 4)).Accepted) accepted++;
        Assert.Equal(StoryLedger.MaxOccurrencesPerProvider, accepted);
        var refused = greedy.Offer("job-0");
        Assert.Equal(StoryTransitionStatus.LimitExceeded, refused.Status);
        Assert.Contains("no other provider is affected", refused.Detail);
        // The other provider's share is untouched.
        Assert.True(quiet.Offer("salvage-run").Accepted);
        Assert.True(StoryLedger.MaxOccurrencesPerProvider * StoryProviderBindings.MaxProviders <= StoryLedger.MaxOccurrences);
    }

    [Fact]
    public void TheBoundedProviderCountRefusesRatherThanTakingABoundProvidersShare()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        for (int index = 0; index < StoryProviderBindings.MaxProviders; index++)
        {
            var plugin = new object();
            host.Register(plugin, "com.test.plugin" + index);
            Assert.Equal(StoryProviderStatus.Acquired, service.AcquireProvider(plugin).Status);
        }
        var overflow = new object();
        host.Register(overflow, "com.test.overflow");
        var refused = service.AcquireProvider(overflow);
        Assert.Equal(StoryProviderStatus.LimitExceeded, refused.Status);
        Assert.Null(refused.Provider);
        Assert.Contains("never taken away", refused.Diagnostic);
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
        // The encoder refuses the retired-implies-one-outcome invariant the decoder enforces, so a
        // capture can never produce a payload that its own load would reject.
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Encode(new[]
        {
            new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Campaign, 1, StoryOccurrenceState.Retired)
        }));
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Encode(new[]
        {
            new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Campaign, 1, StoryOccurrenceState.Active, StoryOutcome.Completed)
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

    /// <summary>
    /// The codec is canonical: a payload can never restore a ledger the ledger's own operations would
    /// refuse, and the ledger can never reach a state its capture would refuse. Both matter because a
    /// capture failure is escalated by the coordinator into a save block for EVERY registered owner.
    /// </summary>
    [Fact]
    public void DecodeEnforcesEveryLedgerBoundAndTheSequenceCannotWrap()
    {
        var id = new StoryContentId("anima", "salvage-run");
        var ledger = new StoryLedger();
        ledger.Restore(new[] { new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Temporary, StoryLedger.MaxSequence) });
        var refused = ledger.Offer(id, StoryRetention.Temporary, Guid.NewGuid(), 0, out var diagnostic);
        Assert.Equal(StoryLedgerStatus.LimitExceeded, refused);
        Assert.Contains("wrapping the timeline", diagnostic);
        // Nothing mutated, so the state stays capturable instead of overflowing into a save block.
        Assert.Single(ledger.Entries);
        Assert.True(StoryStateCodec.Validate(StoryStateCodec.Encode(ledger.Entries)));
        // A stored sequence beyond the bound leaves no headroom and is refused on decode.
        var beyond = StoryStateCodec.Encode(new[] { new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Temporary, 1) });
        int sequenceOffset = 12 + (1 + 5) + (1 + 11) + 16;
        Array.Copy(BitConverter.GetBytes(long.MaxValue), 0, beyond, sequenceOffset, 8);
        Assert.False(StoryStateCodec.Validate(beyond));

        // Every ledger bound is enforced by BOTH sides, so neither cap is more permissive.
        var overQuota = Rows(StoryLedger.MaxOccurrencesPerProvider + 1, "anima", "salvage-run", StoryRetention.Temporary, retired: false);
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Encode(overQuota));
        Assert.False(StoryStateCodec.Validate(Craft(overQuota)));
        var overHorizon = Rows(StoryLedger.TemporaryTombstoneHorizon + 1, "anima", "salvage-run", StoryRetention.Temporary, retired: true);
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Encode(overHorizon));
        Assert.False(StoryStateCodec.Validate(Craft(overHorizon)));
        var overRetained = Rows(StoryLedger.MaxRetainedPerDefinition + 1, "anima", "salvage-run", StoryRetention.Campaign, retired: true);
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Encode(overRetained));
        Assert.False(StoryStateCodec.Validate(Craft(overRetained)));
        // A refused payload leaves the owner blocked and its retained bytes intact; nothing is pruned.
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Decode(Craft(overRetained)));
    }

    private static StoryOccurrenceEntry[] Rows(int count, string provider, string local, StoryRetention retention, bool retired)
        => Enumerable.Range(1, count).Select(index => new StoryOccurrenceEntry(new StoryContentId(provider, local),
            Guid.NewGuid(), retention, index, retired ? StoryOccurrenceState.Retired : StoryOccurrenceState.Offered,
            retired ? StoryOutcome.Completed : null)).ToArray();

    /// <summary>Builds a structurally valid payload that violates a ledger bound, by encoding rows separately.</summary>
    private static byte[] Craft(IReadOnlyList<StoryOccurrenceEntry> rows)
    {
        var bodies = rows.Select(row => StoryStateCodec.Encode(new[] { row }).Skip(12).ToArray()).ToArray();
        var header = StoryStateCodec.Encode(new[] { rows[0] }).Take(8).Concat(BitConverter.GetBytes(rows.Count));
        return header.Concat(bodies.SelectMany(body => body)).ToArray();
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

    /// <summary>
    /// Stands in for the host adapter that resolves a caller object to its loaded plugin. Like the
    /// real host it resolves the ARGUMENT and reports the assembly it loaded that plugin from; the
    /// module is what compares that assembly with the assembly that actually called.
    /// </summary>
    private sealed class FakeHost
    {
        private readonly Dictionary<object, (string PluginId, Assembly Assembly)> _plugins = new();
        internal void Register(object instance, string pluginId, Assembly? assembly = null)
            => _plugins[instance] = (pluginId, assembly ?? typeof(StoryContentTests).Assembly);
        internal StoryHostPlugin? Authenticate(object instance, Assembly caller)
            => _plugins.TryGetValue(instance, out var plugin) ? new StoryHostPlugin(plugin.PluginId, plugin.Assembly) : null;
    }

    private sealed class FakeWorld
    {
        internal readonly FakePersistence Persistence = new();
        internal readonly FakeLifecycle Lifecycle = new();
        internal Guid SessionId => Lifecycle.CurrentSession?.Id ?? Guid.Empty;

        internal StoryContentService Service(FakeHost host, Action? checkThread = null)
            => new(Persistence, Lifecycle, host.Authenticate, null, checkThread);

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
