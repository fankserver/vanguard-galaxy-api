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
/// Internal engine regression helpers. Public authoring uses game/mission objects; these tests
/// exercise the engine's session and ownership guards directly.
/// </summary>
internal static class StoryProviderCallExtensions
{
    internal static bool InternalActive(this IStoryProvider provider) => ((StoryContentService.Lease)provider).Active;
    internal static bool InternalActive(this IStoryDefinition definition) => ((StoryContentService.Registration)definition).Active;
    internal static StoryTransitionResult Offer(this IStoryProvider provider, Guid session, string localId) => ((StoryContentService.Lease)provider).Offer(session, localId);
    internal static StoryTransitionResult Activate(this IStoryProvider provider, Guid session, Guid occurrence) => ((StoryContentService.Lease)provider).Activate(session, occurrence);
    internal static StoryTransitionResult Withdraw(this IStoryProvider provider, Guid session, Guid occurrence) => ((StoryContentService.Lease)provider).Withdraw(session, occurrence);
    internal static StoryTransitionResult Retire(this IStoryProvider provider, Guid session, Guid occurrence, StoryOutcome outcome, IReadOnlyDictionary<string, string>? choices = null) => ((StoryContentService.Lease)provider).Retire(session, occurrence, outcome, choices);
    internal static StoryTransitionResult DeclareChoices(this IStoryProvider provider, Guid session, Guid occurrence, IReadOnlyDictionary<string, string> choices) => ((StoryContentService.Lease)provider).DeclareChoices(session, occurrence, choices);
    internal static StoryOccurrenceQuery Occurrences(this IStoryProvider provider, string localId) => ((StoryContentService.Lease)provider).Occurrences(localId);
    internal static StoryOccurrenceSnapshotQuery Unresolved(this IStoryProvider provider, string localId) => ((StoryContentService.Lease)provider).Unresolved(localId);
    internal static StoryCompletionQuery IsCompleted(this IStoryProvider provider, string localId) => ((StoryContentService.Lease)provider).IsCompleted(localId);

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
    internal static StoryTransitionResult DeclareChoices(this IStoryProvider provider, Guid occurrenceId,
        IReadOnlyDictionary<string, string> choices)
        => provider.DeclareChoices(provider.CurrentSession(), occurrenceId, choices);
}

public sealed partial class StoryContentTests
{
    [Fact]
    public void TypedUnavailableStoryDoesNotAuthenticateOrRegisterSaveData()
    {
        using var hub = new LifecycleHub((_, _) => { });
        hub.SetCapability("owned-story", false, "Disabled.", ServiceUnavailableReason.Disabled);
        var authentications = 0;
        using var engine = new StoryContentService(hub.Services, null, hub, (_, _) => { authentications++; return null; }, checkThread: hub.CheckThread);
        IStoryService service = engine;
        Assert.Equal(StoryProviderStatus.Unavailable, service.AcquireProvider(new object()).Status);
        Assert.Equal(0, authentications);
        Assert.Equal(ServiceUnavailableReason.Disabled, service.Availability.Reason);
        engine.Dispose(); Assert.Equal(ServiceUnavailableReason.Disabled, service.Availability.Reason);
        Assert.Null(typeof(ModApi).GetProperty("Story"));
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IStoryApi"));
    }
    [Fact]
    public void TypedStoryHealthReadsProtectionFaultWithoutPumpingCallbacks()
    {
        using var hub = new LifecycleHub((_, _) => { });
        hub.SetCapability("owned-story", true, "Bound.");
        var protection = true;
        using var service = new StoryContentService(hub.Services, null, hub, (_, _) => null, checkThread: hub.CheckThread, protectionHealthy: () => protection);
        var changes = 0; service.AvailabilityChanged += _ => changes++;
        protection = false;
        Assert.Equal(ServiceUnavailableReason.ObserverFault, service.Availability.Reason);
        Assert.Equal(0, changes);
        hub.Services.Refresh(); Assert.Equal(1, changes);
    }

    private const string AnimaPlugin = "com.fank.anima";
    private const string OtherPlugin = "com.other.custommission";

    private static readonly StoryFactionId Faction = new("TradingGuild");

    private static StoryMissionDefinition Definition(string local = "salvage-run",
        StoryRetention retention = StoryRetention.Temporary, IEnumerable<string>? choiceKeys = null)
        => new(local, "Salvage run", "Recover the drifting cargo.", Faction,
            new[] { new StoryStep("Reach the wreck", new[] { StoryObjective.TravelTo("poi-guid-1", requireNewVisit: true) }) },
            new[] { StoryReward.Credits(500) }, StoryDifficulty.Normal, retention,
            choiceKeys: choiceKeys ?? (retention == StoryRetention.Campaign ? new[] { "branch" } : null));

    /// <summary>A campaign definition declaring the largest supported choice payload.</summary>
    private static StoryMissionDefinition WorstDefinition(string local = "salvage-run")
        => Definition(local, StoryRetention.Campaign,
            Enumerable.Range(0, StoryMissionDefinition.MaxChoiceKeys).Select(index => "k" + index + new string('x', StoryMissionDefinition.MaxChoiceKeyBytes - 2)));

    private static Dictionary<string, string> WorstChoices(StoryMissionDefinition definition)
        => definition.ChoiceKeys.ToDictionary(key => key, _ => new string('v', StoryMissionDefinition.MaxChoiceValueBytes), StringComparer.Ordinal);

    [Fact]
    public void OwnedTravelTargetsRequireSameOwnerWorldDependencyAdmission()
    {
        var host = new FakeHost(); var world = new FakeWorld(); bool? ready = null;
        var identity = new WorldObjectIdentity(new ContentDeclaration(AnimaPlugin, "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
        string? seenOwner = null, seenTarget = null;
        using var service = world.Service(host, worldReferences: (owner, target) =>
        { seenOwner = owner; seenTarget = target; return ready; });
        world.StartAndRestore(); var plugin = new object(); host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        var definition = new StoryMissionDefinition("world-trip", "Visit", "Visit the site", Faction,
            new[] { new StoryStep("Travel", new[] { StoryObjective.TravelTo(identity.NativeId) }) },
            new[] { StoryReward.Credits(1) });
        var registered = provider.Register(definition); Assert.True(registered.Succeeded, registered.Diagnostic);
        Assert.False(provider.Offer(definition.LocalId).Accepted);
        ready = false; Assert.False(provider.Offer(definition.LocalId).Accepted);
        ready = true; var offered = provider.Offer(definition.LocalId); Assert.True(offered.Accepted, offered.Detail);
        Assert.Equal(AnimaPlugin, seenOwner); Assert.Equal(identity.NativeId, seenTarget);
        Assert.True(provider.Register(Definition()).Succeeded);
        var ordinary = provider.Offer("salvage-run"); Assert.True(ordinary.Accepted);
        ready = false; service.RefreshWorldDependencies();
        Assert.True(world.Protection.IsQuarantined(FakeWorld.Native(provider, definition.LocalId, offered.OccurrenceId)));
        Assert.False(world.Protection.IsQuarantined(FakeWorld.Native(provider, "salvage-run", ordinary.OccurrenceId)));
    }

    [Fact]
    public void BarDependenciesRequireLiveDefinitionExactOccurrenceAndCurrentAdmissions()
    {
        var host = new FakeHost(); var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore(); var plugin = new object(); host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        var definition = Definition(); var registration = provider.Register(definition).Definition!;
        var offered = provider.Offer(definition.LocalId);
        Assert.True(offered.Accepted);
        var id = new StoryContentId(provider.ProviderId, definition.LocalId);
        Assert.True(service.IsBarMissionReady(world.SessionId, id, offered.OccurrenceId));
        Assert.False(service.IsBarMissionReady(Guid.NewGuid(), id, offered.OccurrenceId));
        Assert.False(service.IsBarMissionReady(world.SessionId, id, Guid.NewGuid()));
        var stamp = service.BarDependencyStamp();
        Assert.Same(stamp, service.BarDependencyStamp());
        registration.Dispose();
        Assert.False(service.IsBarMissionReady(world.SessionId, id, offered.OccurrenceId));
        Assert.True(provider.Register(definition).Succeeded);
        Assert.NotSame(stamp, service.BarDependencyStamp());
        Assert.True(service.IsBarMissionReady(world.SessionId, id, offered.OccurrenceId));
        stamp = service.BarDependencyStamp();
        world.DegradeProtection("scan failed");
        Assert.False(service.IsBarMissionReady(world.SessionId, id, offered.OccurrenceId));
        Assert.NotSame(stamp, service.BarDependencyStamp());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TentativeRegistrationCannotAdmitLinkedPatronsEvenInsideNativeCallbacks(bool throws)
    {
        var host = new FakeHost(); var world = new FakeWorld(); using var service = world.Service(host);
        world.StartAndRestore(); var plugin = new object(); host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        var definition = Definition(); var registration = provider.Register(definition).Definition!;
        var offered = provider.Offer(definition.LocalId);
        var id = new StoryContentId(provider.ProviderId, definition.LocalId);
        Assert.True(service.IsBarMissionReady(world.SessionId, id, offered.OccurrenceId));
        registration.Dispose();
        var stamp = service.BarDependencyStamp(); int callbacks = 0;
        world.World.DuringInstall = () =>
        {
            callbacks++;
            Assert.False(service.IsBarMissionReady(world.SessionId, id, offered.OccurrenceId));
            Assert.NotSame(stamp, service.BarDependencyStamp());
            if (throws) throw new InvalidOperationException("installation interrupted");
            world.World.Unavailable = true;
        };
        if (throws) Assert.Throws<InvalidOperationException>(() => provider.Register(definition));
        else Assert.False(provider.Register(definition).Succeeded);
        Assert.Equal(1, callbacks);
        Assert.False(service.IsBarMissionReady(world.SessionId, id, offered.OccurrenceId));
        Assert.NotSame(stamp, service.BarDependencyStamp());
    }

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
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("Salvage", "t", "d", Faction, new[] { step }));
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("salvage", "", "d", Faction, new[] { step }));
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("salvage", "t", "d", Faction, Array.Empty<StoryStep>()));
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("salvage", "t", "d", Faction,
            Enumerable.Range(0, StoryMissionDefinition.MaxSteps + 1).Select(_ => step)));
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("salvage", "t", "d", Faction, new[] { step },
            new[] { StoryReward.Credits(1), StoryReward.Credits(2) }));
        Assert.Throws<ArgumentException>(() => new StoryStep("s", Array.Empty<StoryObjective>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoryObjective.KillEnemies(0, new StoryFactionId("TradingGuild")));
        Assert.Throws<ArgumentException>(() => StoryObjective.KillEnemies(1, default));
        Assert.Throws<ArgumentException>(() => StoryObjective.TravelTo(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoryReward.Credits(0));
        var objectives = new List<StoryObjective> { StoryObjective.CollectCredits(10) };
        var built = new StoryStep("s", objectives);
        objectives.Add(StoryObjective.KillEnemies(3, new StoryFactionId("TradingGuild")));
        Assert.Single(built.Objectives);
    }

    // --- registration -----------------------------------------------------------------------

    [Fact]
    public void RegistrationIsFailClosedForInvalidDefinitionsDuplicatesCollisionsAndLimits()
    {
        var registry = new StoryDefinitionRegistry();
        var anima = new StoryContentId("anima", "salvage-run");
        Assert.Equal(StoryRegistrationStatus.Registered, registry.TryRegister(anima, Definition(), out _, out var identifier, out var animaEntry));
        Assert.Equal(StoryRegistrationStatus.DuplicateLocalId, registry.TryRegister(anima, Definition(), out var duplicate, out _, out _));
        Assert.Contains("already registered local ID", duplicate);
        // A different provider with the same local ID is a different identifier and is accepted.
        var other = new StoryContentId("custommission", "salvage-run");
        Assert.Equal(StoryRegistrationStatus.Registered, registry.TryRegister(other, Definition(), out _, out var otherIdentifier, out _));
        Assert.NotEqual(identifier, otherIdentifier);
        // A policy refusal is about the definition, NOT about someone owning the identifier.
        Assert.Equal(StoryRegistrationStatus.InvalidDefinition,
            registry.TryRegister(new StoryContentId("anima", "mismatch"), Definition(), out var invalid, out _, out _));
        Assert.Contains("does not match its resolved identity", invalid);
        // Identifiers that already exist in the world are never replaced.
        var reserving = new StoryDefinitionRegistry();
        reserving.Reserve(new[] { StoryContentPolicy.Identifier(anima) });
        Assert.Equal(StoryRegistrationStatus.IdentifierInUse, reserving.TryRegister(anima, Definition(), out var taken, out _, out _));
        Assert.Contains("never replaces existing content", taken);
        Assert.Equal(0, reserving.Count);
        var bounded = new StoryDefinitionRegistry();
        for (int index = 0; index < StoryContentPolicy.MaxDefinitions; index++)
            Assert.Equal(StoryRegistrationStatus.Registered,
                bounded.TryRegister(new StoryContentId("anima", "m" + index), Definition("m" + index), out _, out _, out _));
        Assert.Equal(StoryRegistrationStatus.LimitExceeded,
            bounded.TryRegister(new StoryContentId("anima", "overflow"), Definition("overflow"), out var limit, out _, out _));
        Assert.Contains("nothing was dropped", limit);
        // A registration has its OWN identity, distinct from the definition object: unregistering and
        // registering the very same immutable definition mints a new entry.
        Assert.Equal(animaEntry, registry.EntryOf(anima));
        var sameDefinition = Definition();
        var fresh = new StoryDefinitionRegistry();
        Assert.Equal(StoryRegistrationStatus.Registered, fresh.TryRegister(anima, sameDefinition, out _, out _, out var firstEntry));
        Assert.True(fresh.Unregister(anima));
        Assert.Equal(StoryRegistrationStatus.Registered, fresh.TryRegister(anima, sameDefinition, out _, out _, out var secondEntry));
        Assert.NotEqual(firstEntry, secondEntry);
        // The superseded entry removes nothing; only the entry that owns the identifier can release it.
        Assert.False(fresh.RemoveIfMatches(anima, firstEntry));
        Assert.True(fresh.Contains(anima));
        Assert.True(fresh.RemoveIfMatches(anima, secondEntry));
        Assert.False(fresh.Contains(anima));
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
        Assert.True(anima.Provider.InternalActive());
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
        Assert.True(first.Retire(occurrence.OccurrenceId, StoryOutcome.Failed).Accepted);
        Assert.Equal(StoryProviderStatus.AlreadyAcquired, service.AcquireProvider(plugin).Status);

        first.Dispose();
        var second = service.AcquireProvider(plugin);
        Assert.Equal(StoryProviderStatus.Acquired, second.Status);
        Assert.NotSame(first, second.Provider);
        Assert.False(first.InternalActive());
        Assert.Equal(StoryTransitionStatus.Unavailable, first.Offer("salvage-run").Status);
        // The ledger kept the occurrence; only the registration was released with the lease.
        Assert.Equal(first.ProviderId, second.Provider!.ProviderId);
        Assert.Single(service.Ledger.Entries);
        Assert.True(second.Provider.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
        Assert.Equal(StoryOutcome.Failed, Assert.Single(second.Provider.Occurrences("salvage-run").Records).Outcome);
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
    private static Func<IStoryService, object, StoryProviderResult> ForeignAssemblyCaller()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("VGModAPI.Tests.ForeignCaller"), AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("main").DefineType("Caller", TypeAttributes.Public);
        var method = type.DefineMethod("Call", MethodAttributes.Public | MethodAttributes.Static,
            typeof(StoryProviderResult), new[] { typeof(IStoryService), typeof(object) });
        method.SetImplementationFlags(MethodImplAttributes.NoInlining);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, typeof(IStoryService).GetMethod(nameof(IStoryService.AcquireProvider))!);
        il.Emit(OpCodes.Ret);
        return (Func<IStoryService, object, StoryProviderResult>)type.CreateType()!
            .GetMethod("Call")!.CreateDelegate(typeof(Func<IStoryService, object, StoryProviderResult>));
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
        var registration = anima.Register(Definition()).Definition!;
        other.Register(Definition());
        anima.Dispose();
        Assert.False(anima.InternalActive());
        Assert.False(registration.InternalActive());
        Assert.Equal(StoryTransitionStatus.Unavailable, anima.Offer("salvage-run").Status);
        Assert.Equal(StoryKnowledge.Unavailable, anima.Occurrences("salvage-run").Knowledge);
        // The other mod keeps working, and the coordinator owner was never unregistered, so no other
        // mod's saves are paused by a consumer's teardown.
        Assert.True(other.InternalActive());
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
        var stale = first.Register(Definition(retention: StoryRetention.Campaign)).Definition!;
        first.Dispose();

        var second = service.AcquireProvider(plugin).Provider!;
        var live = second.Register(Definition(retention: StoryRetention.Campaign)).Definition!;
        stale.Dispose();                                  // ordinary teardown of the old handle

        Assert.True(live.InternalActive());
        var occurrence = second.Offer("salvage-run");
        Assert.True(occurrence.Accepted);
        Assert.True(second.DeclareChoices(occurrence.OccurrenceId, new Dictionary<string, string> { ["branch"] = "left" }).Accepted);
        Assert.True(second.Activate(occurrence.OccurrenceId).Accepted);
        world.CompleteInGame(second, "salvage-run", occurrence.OccurrenceId);
        Assert.True(second.IsCompleted("salvage-run").Completed);
        // The live registration is still the one that can be released by its OWN handle.
        live.Dispose();
        Assert.False(live.InternalActive());
    }

    /// <summary>
    /// A registration handle releases only the registration IT made. The identifier can be released
    /// and taken again under the same live lease - with the very same immutable definition object -
    /// and the superseded handle must not remove the live one. The public surface offers no
    /// unregister other than disposing a handle, so this is reached here through the internal
    /// registry entry point that the native adapter will use; the guard is what makes that future
    /// integration safe rather than order-dependent.
    /// </summary>
    [Fact]
    public void AHandleWhoseRegistrationWasSupersededDoesNotRemoveTheLiveOne()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        var definition = Definition(retention: StoryRetention.Campaign);
        var superseded = provider.Register(definition).Definition!;
        var id = superseded.Id;

        // The internal path a native adapter would use to release and reinstall content.
        Assert.True(service.Registry.Unregister(id));
        Assert.False(superseded.InternalActive());
        var live = provider.Register(definition).Definition!;
        Assert.True(live.InternalActive());
        Assert.NotEqual(0, service.Registry.EntryOf(id));

        superseded.Dispose();

        Assert.True(live.InternalActive());
        Assert.True(service.Registry.Contains(id));
        var occurrence = provider.Offer("salvage-run");
        Assert.True(occurrence.Accepted);
        Assert.True(provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed,
            new Dictionary<string, string> { ["branch"] = "left" }).Accepted);
        // Only its own handle releases the live registration, and history is untouched by either.
        live.Dispose();
        Assert.False(live.InternalActive());
        Assert.False(service.Registry.Contains(id));
        Assert.Single(provider.Occurrences("salvage-run").Records);
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
            Assert.True(provider.Activate(offer.OccurrenceId).Accepted);
            world.CompleteInGame(provider, "salvage-run", offer.OccurrenceId);
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
            Assert.False(world.Persistence.StateReady && provider.Offer("salvage-run").Accepted);
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
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        world.CompleteInGame(provider, "salvage-run", occurrence.OccurrenceId);
        Assert.True(provider.IsCompleted("salvage-run").Completed);

        // A transient pause is NOT a block: reads stay known, only mutations are refused, temporarily.
        world.Persistence.MutationsPaused = true;
        var paused = provider.IsCompleted("salvage-run");
        Assert.Equal(StoryKnowledge.Known, paused.Knowledge);
        Assert.True(paused.Completed);
        var busy = provider.Offer("salvage-run");
        Assert.Equal(StoryTransitionStatus.Busy, busy.Status);
        Assert.Contains("mutations are paused", busy.Detail);
        world.Persistence.MutationsPaused = false;

        world.Persistence.StateReady = false;      // the owner is blocked or unreadable mid-session
        var completion = provider.IsCompleted("salvage-run");
        Assert.Equal(StoryKnowledge.Unavailable, completion.Knowledge);
        Assert.Null(completion.Completed);
        var occurrences = provider.Occurrences("salvage-run");
        Assert.Equal(StoryKnowledge.Unavailable, occurrences.Knowledge);
        Assert.Empty(occurrences.Records);
        Assert.Equal(StoryTransitionStatus.Unavailable, provider.Offer("salvage-run").Status);

        // Recovery answers for the SAME session again, with the history that was recorded in it.
        world.Persistence.StateReady = true;
        var resumed = provider.IsCompleted("salvage-run");
        Assert.Equal(StoryKnowledge.Known, resumed.Knowledge);
        Assert.True(resumed.Completed);
        Assert.Equal(world.SessionId, resumed.SessionId);
        Assert.Single(provider.Occurrences("salvage-run").Records);
    }

    [Fact]
    public void BlockedProviderStateMakesEveryAnswerUnavailable()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        Assert.True(provider.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
        world.Persistence.StateReady = false;
        Assert.False(world.Persistence.CanRead);

        var completion = provider.IsCompleted("salvage-run");
        Assert.Equal(StoryKnowledge.Unavailable, completion.Knowledge);
        Assert.Null(completion.Completed);
        Assert.Equal(StoryKnowledge.Unavailable, provider.Occurrences("salvage-run").Knowledge);
        Assert.Equal(StoryKnowledge.Unavailable, provider.Unresolved("salvage-run").Knowledge);
        var refused = provider.Offer("salvage-run");
        Assert.Equal(StoryTransitionStatus.Unavailable, refused.Status);
        Assert.Contains("Blocked", refused.Detail);
        Assert.Empty(service.Ledger.Entries);
    }

    /// <summary>
    /// Registration statuses are exposed now, so their numbers are pinned as they already are, gap
    /// included: closing the gap would change a value rather than preserve it.
    /// </summary>
    [Fact]
    public void TheRegistrationStatusNumbersAreStable()
    {
        Assert.Equal(0, (int)StoryRegistrationStatus.Registered);
        Assert.Equal(1, (int)StoryRegistrationStatus.InvalidDefinition);
        Assert.Equal(2, (int)StoryRegistrationStatus.DuplicateLocalId);
        Assert.Equal(3, (int)StoryRegistrationStatus.IdentifierInUse);
        Assert.Equal(4, (int)StoryRegistrationStatus.LimitExceeded);
        Assert.Equal(7, (int)StoryRegistrationStatus.Unavailable);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 7 },
            Enum.GetValues(typeof(StoryRegistrationStatus)).Cast<int>().OrderBy(value => value).ToArray());
    }

    /// <summary>The refusal vocabulary is a contract: members keep their numbers, new ones are appended.</summary>
    [Fact]
    public void TheTransitionStatusNumbersAreStable()
    {
        Assert.Equal(0, (int)StoryTransitionStatus.Accepted);
        Assert.Equal(1, (int)StoryTransitionStatus.UnknownOccurrence);
        Assert.Equal(2, (int)StoryTransitionStatus.ForeignOwner);
        Assert.Equal(3, (int)StoryTransitionStatus.InvalidTransition);
        Assert.Equal(4, (int)StoryTransitionStatus.LimitExceeded);
        Assert.Equal(5, (int)StoryTransitionStatus.StaleSession);
        Assert.Equal(6, (int)StoryTransitionStatus.Busy);
        Assert.Equal(7, (int)StoryTransitionStatus.Unavailable);
        // Nothing else exists, so an inserted member cannot renumber these unnoticed.
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7 },
            Enum.GetValues(typeof(StoryTransitionStatus)).Cast<int>().OrderBy(value => value).ToArray());
        Assert.Equal(8, Enum.GetNames(typeof(StoryTransitionStatus)).Length);
    }

    /// <summary>The same rule against the REAL coordinator, blocked by an owner unregistering mid-session.</summary>
    [Fact]
    public void TheRealCoordinatorBlockingASessionMakesStoryAnswersUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "vg-story-" + Guid.NewGuid().ToString("N"));
        using var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected subscriber fault", error));
        try
        {
            hub.SetCapability("session-lifecycle", true, "Bound."); hub.SetCapability("save-outcomes", true, "Bound.");
            using var persistence = new PersistenceService(hub, new GenerationStore(root), path => path, _ => new string('a', 64));
            var control = persistence.Register(new PersistenceProvider("vgmodapi.tests.control", 1,
                capture: () => new byte[] { 1 }, restore: (_, _) => { }, validate: bytes => bytes.Length == 1)).Registration!;
            Assert.Equal(SaveDataStateKind.Inactive, control.State.Kind);
            var host = new FakeHost();
            hub.SetCapability("owned-story", true, "Test bindings.");
            using var service = new StoryContentService(hub.Services, persistence, hub, host.Authenticate, null, hub.CheckThread);
            var session = hub.Begin(SessionOrigin.NewGame, null);
            hub.PlayerReady(session);
            hub.GameplayInitialized(session);

            var plugin = new object();
            host.Register(plugin, AnimaPlugin);
            var provider = service.AcquireProvider(plugin).Provider!;
            Assert.True(provider.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
            var occurrence = provider.Offer("salvage-run");
            Assert.True(occurrence.Accepted);
            Assert.True(provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed).Accepted);
            Assert.Equal(StoryKnowledge.Known, provider.IsCompleted("salvage-run").Knowledge);

            // Another owner unregistering mid-session is a real coordinator load block.
            control.Dispose();
            Assert.Equal("Blocked", service.PersistenceStatus);
            var blocked = provider.IsCompleted("salvage-run");
            Assert.Equal(StoryKnowledge.Unavailable, blocked.Knowledge);
            Assert.Null(blocked.Completed);
            Assert.Empty(provider.Occurrences("salvage-run").Records);
            Assert.Equal(StoryTransitionStatus.Unavailable, provider.Offer("salvage-run").Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>
    /// Rediscovering content from a lifecycle callback is the documented way to use this API, and a
    /// save being written is not a reason to stop answering. Both are moments where the REAL
    /// coordinator forbids mutation while the restored state is perfectly readable, so queries stay
    /// Known and only mutations are refused - as temporarily busy, with the real reason.
    /// </summary>
    [Fact]
    public void QueriesAnswerInsideLifecycleCallbacksAndDuringASaveWhileMutationsReportBusy()
    {
        var root = Path.Combine(Path.GetTempPath(), "vg-story-" + Guid.NewGuid().ToString("N"));
        using var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected subscriber fault", error));
        try
        {
            hub.SetCapability("session-lifecycle", true, "Bound."); hub.SetCapability("save-outcomes", true, "Bound.");
            using var persistence = new PersistenceService(hub, new GenerationStore(root), path => path, _ => new string('a', 64));
            var host = new FakeHost();
            hub.SetCapability("owned-story", true, "Test bindings.");
            using var service = new StoryContentService(hub.Services, persistence, hub, host.Authenticate, null, hub.CheckThread);
            var session = hub.Begin(SessionOrigin.NewGame, null);
            hub.PlayerReady(session);

            var plugin = new object();
            host.Register(plugin, AnimaPlugin);
            var provider = service.AcquireProvider(plugin).Provider!;
            Assert.True(provider.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);

            // A provider rediscovering its content from the GameplayInitialized callback.
            StoryOccurrenceSnapshotQuery? unresolvedInCallback = null;
            StoryOccurrenceQuery? retainedInCallback = null;
            StoryTransitionResult mutationInCallback = default!;
            Guid admitted = Guid.Empty;
            using var subscription = hub.Subscribe("vgmodapi.tests.consumer", e =>
            {
                if (e.Kind != LifecycleEventKind.GameplayInitialized) return;
                unresolvedInCallback = provider.Unresolved("salvage-run");
                retainedInCallback = provider.Occurrences("salvage-run");
                mutationInCallback = provider.Offer(unresolvedInCallback.SessionId ?? Guid.Empty, "salvage-run");
            });
            hub.GameplayInitialized(session);

            Assert.Equal(StoryKnowledge.Known, unresolvedInCallback!.Knowledge);
            Assert.Equal(session, unresolvedInCallback.SessionId);
            Assert.Empty(unresolvedInCallback.Occurrences);
            Assert.Equal(StoryKnowledge.Known, retainedInCallback!.Knowledge);
            // Mutating from inside a dispatch is refused as BUSY, naming the real reason.
            Assert.Equal(StoryTransitionStatus.Busy, mutationInCallback.Status);
            Assert.Contains("mutations are paused", mutationInCallback.Detail);
            Assert.DoesNotContain("unavailable", mutationInCallback.Detail);
            Assert.Equal("Ready", service.PersistenceStatus);

            // Outside the dispatch the same call is accepted, and the state it records is queryable.
            var offer = provider.Offer("salvage-run");
            Assert.True(offer.Accepted);
            admitted = offer.OccurrenceId;
            Assert.Equal(admitted, Assert.Single(provider.Unresolved("salvage-run").Occurrences).OccurrenceId);

            // With a save in flight, reads stay Known and mutations report the same busy refusal.
            var operation = Guid.NewGuid();
            hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, operation, "slot"));
            var duringSave = provider.Unresolved("salvage-run");
            Assert.Equal(StoryKnowledge.Known, duringSave.Knowledge);
            Assert.Equal(admitted, Assert.Single(duringSave.Occurrences).OccurrenceId);
            Assert.Equal(StoryKnowledge.Known, provider.IsCompleted("salvage-run").Knowledge);
            var busy = provider.Retire(admitted, StoryOutcome.Failed);
            Assert.Equal(StoryTransitionStatus.Busy, busy.Status);
            Assert.Contains("save is in flight", busy.Detail);
            // The refusal changed nothing, and the outcome is recordable once the save completes.
            hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSucceeded, hub.CurrentSession, operation, "slot"));
            Assert.True(provider.Retire(admitted, StoryOutcome.Failed).Accepted);
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
        Assert.True(provider.Activate(offer.OccurrenceId).Accepted);
        world.CompleteInGame(provider, "salvage-run", offer.OccurrenceId);
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
            hub.SetCapability("session-lifecycle", true, "Bound."); hub.SetCapability("save-outcomes", true, "Bound.");
            using var persistence = new PersistenceService(hub, new GenerationStore(root), path => path, _ => new string('a', 64));
            byte[]? restored = null;
            using var control = persistence.Register(new PersistenceProvider("vgmodapi.tests.control", 1,
                capture: () => new byte[] { 1 }, restore: (_, bytes) => restored = bytes, validate: bytes => bytes.Length == 1)).Registration!;
            var session = hub.Begin(SessionOrigin.NewGame, null);
            hub.PlayerReady(session);
            hub.GameplayInitialized(session);

            var host = new FakeHost();
            var failure = Assert.Throws<InvalidOperationException>(
                () => new StoryContentService(hub.Services, persistence, hub, host.Authenticate, null, hub.CheckThread));
            Assert.Contains("before a session begins", failure.Message);
            // No story owner exists, and the other registered owner is neither paused nor faulted.
            Assert.True(control.CanMutate);
            Assert.Equal(SaveDataStateKind.Ready, control.State.Kind);
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
        var registration = provider.Register(Definition()).Definition!;
        guard = true;
        foreach (Action call in new Action[]
        {
            () => provider.Register(Definition("other")),
            () => provider.Offer("salvage-run"),
            () => provider.Activate(Guid.NewGuid()),
            () => provider.Withdraw(Guid.NewGuid()),
            () => provider.Retire(Guid.NewGuid(), StoryOutcome.Failed),
            () => provider.Occurrences("salvage-run"),
            () => provider.IsCompleted("salvage-run"),
            () => { var _ = provider.InternalActive(); },
            () => { var _ = registration.InternalActive(); },
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
        var provider = Provider(out var world, out _, out _, StoryRetention.Campaign);
        var first = provider.Offer("salvage-run");
        Assert.True(provider.Activate(first.OccurrenceId).Accepted);
        // The game ends the mission, and that is what records the completion.
        world.CompleteInGame(provider, "salvage-run", first.OccurrenceId);
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
        Assert.Equal(StoryTransitionStatus.UnknownOccurrence, provider.Retire(Guid.NewGuid(), StoryOutcome.Failed).Status);
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
            Assert.True(provider.Retire(offer.OccurrenceId, StoryOutcome.Failed).Accepted);
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
            Assert.True(provider.Retire(offer.OccurrenceId, StoryOutcome.Failed,
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
        // The CAMPAIGN bound is what refuses here; a payload-budget or quota refusal must not be
        // able to pass for it.
        Assert.Contains("campaign occurrences including unresolved ones", refused.Detail);
        Assert.Contains("could never retire", refused.Detail);
        Assert.Equal(before, service.Ledger.Count);
        // Everything that was admitted can still record its outcome; none is stranded.
        foreach (var occurrence in admitted)
            Assert.True(provider.Retire(occurrence, StoryOutcome.Failed,
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
        Assert.True(temporary.Retire(job.OccurrenceId, StoryOutcome.Failed).Accepted);
        var temporaryAnswer = temporary.IsCompleted("salvage-run");
        Assert.Equal(StoryKnowledge.Known, temporaryAnswer.Knowledge);
        Assert.False(temporaryAnswer.Completed);
        // The tombstone still exists for idempotency; it just does not answer completion.
        Assert.Single(temporary.Occurrences("salvage-run").Records);

        var campaign = Provider(out var campaignWorld, out _, out _, StoryRetention.Campaign);
        var act = campaign.Offer("salvage-run");
        Assert.True(campaign.Activate(act.OccurrenceId).Accepted);
        campaignWorld.CompleteInGame(campaign, "salvage-run", act.OccurrenceId);
        Assert.True(campaign.IsCompleted("salvage-run").Completed);
        // A temporary occurrence may not carry declared choices at all.
        var rejected = temporary.Offer("salvage-run");
        var refusal = temporary.Retire(rejected.OccurrenceId, StoryOutcome.Failed,
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
        var stolen = other.Retire(occurrence.OccurrenceId, StoryOutcome.Failed, choices);
        Assert.Equal(StoryTransitionStatus.ForeignOwner, stolen.Status);
        Assert.DoesNotContain("secret-arc", stolen.Detail);
        // Identical to the choice-free path.
        var stolenWithout = other.Retire(occurrence.OccurrenceId, StoryOutcome.Failed);
        Assert.Equal(StoryTransitionStatus.ForeignOwner, stolenWithout.Status);
        Assert.Equal(stolenWithout.Detail, stolen.Detail);

        // A blocked owner and an inactive lease report Unavailable, not a lookup result.
        world.Persistence.StateReady = false;
        Assert.Equal(StoryTransitionStatus.Unavailable, anima.Retire(occurrence.OccurrenceId, StoryOutcome.Failed, choices).Status);
        world.Persistence.StateReady = true;
        var lease = service.AcquireProvider(otherPlugin).Provider;
        Assert.Null(lease);                                   // still held; use the live one
        other.Dispose();
        Assert.Equal(StoryTransitionStatus.Unavailable, other.Retire(occurrence.OccurrenceId, StoryOutcome.Failed, choices).Status);
        // Nothing was mutated by any refusal: the owner can still record its outcome.
        Assert.True(anima.Retire(occurrence.OccurrenceId, StoryOutcome.Failed, choices).Accepted);
    }

    /// <summary>
    /// The choices a caller supplies are external input. They are copied ONCE, after authorisation
    /// and before any validation, so a collection that answers differently on a second read cannot
    /// get past validation and into the stored record - which would make every later capture throw
    /// and block coordinated saves for every registered mod.
    /// </summary>
    [Fact]
    public void SuppliedChoicesAreSnapshotOnceSoValidationAndStorageSeeTheSameData()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var first = provider.Offer("salvage-run");
        // Reads valid data once, then would hand out an oversized value, invalid UTF-8 and an
        // undeclared key on every later read.
        var shifting = new ShiftingChoices(
            new Dictionary<string, string> { ["branch"] = "left" },
            new Dictionary<string, string>
            {
                ["branch"] = new string('v', StoryMissionDefinition.MaxChoiceValueBytes + 32),
                ["ending"] = "\ud800",
            });
        Assert.True(provider.Retire(first.OccurrenceId, StoryOutcome.Failed, shifting).Accepted);
        // Exactly ONE read of the caller's collection: what was validated is what was stored.
        Assert.Equal(1, shifting.Reads);
        var stored = Assert.Single(provider.Occurrences("salvage-run").Records);
        Assert.Equal(new[] { "branch" }, stored.Choices.Keys.ToArray());
        Assert.Equal("left", stored.Choices["branch"]);
        // The state the module holds is exactly what it validated, so it still captures.
        Assert.True(StoryStateCodec.Validate(world.Persistence.Provider!.Capture()));

        // A Count that disagrees with what the collection yields decides nothing.
        var second = provider.Offer("salvage-run");
        var lying = new ShiftingChoices(new Dictionary<string, string> { ["branch"] = "right" },
            new Dictionary<string, string> { ["branch"] = "right" }, reportedCount: 0);
        Assert.True(provider.Retire(second.OccurrenceId, StoryOutcome.Failed, lying).Accepted);
        Assert.Equal("right", provider.Occurrences("salvage-run").Records[1].Choices["branch"]);
        Assert.True(StoryStateCodec.Validate(world.Persistence.Provider.Capture()));
        Assert.Equal(2, service.Ledger.Count);
    }

    /// <summary>
    /// The caller's collection is external CODE, not just external data: reading it re-enters the
    /// game's thread while a retirement is in flight. Whatever that code does — disposing the lease,
    /// retiring the same occurrence itself, or reloading the save — the outer call must land on the
    /// refusal that state deserves and must not apply anything on top of it. This pins the
    /// re-checking of lease, availability, session and occurrence state AFTER the snapshot; a
    /// refactor that trusted the pre-snapshot resolution would break it.
    /// </summary>
    [Theory]
    [InlineData(ReentrancyPoint.GetEnumerator)]
    [InlineData(ReentrancyPoint.MoveNext)]
    [InlineData(ReentrancyPoint.Dispose)]
    public void DisposingTheLeaseWhileTheSuppliedChoicesAreReadRefusesTheRetirement(ReentrancyPoint point)
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        var reentrant = new ReentrantChoices(point, () => provider.Dispose(),
            new Dictionary<string, string> { ["branch"] = "left" });
        var result = provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed, reentrant);

        Assert.Equal(StoryTransitionStatus.Unavailable, result.Status);
        Assert.True(reentrant.Ran);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var untouched));
        Assert.Equal(StoryOccurrenceState.Offered, untouched.State);
        Assert.Null(untouched.Outcome);
        Assert.Empty(untouched.Choices);
        Assert.True(StoryStateCodec.Validate(world.Persistence.Provider!.Capture()));
    }

    /// <summary>
    /// A nested retirement of the SAME occurrence from inside the collection wins, exactly once, and
    /// the outer call is refused as the second terminal outcome it now is.
    /// </summary>
    [Theory]
    [InlineData(ReentrancyPoint.GetEnumerator)]
    [InlineData(ReentrancyPoint.MoveNext)]
    [InlineData(ReentrancyPoint.Dispose)]
    public void ANestedRetirementOfTheSameOccurrenceIsRecordedOnceAndTheOuterCallIsRefused(ReentrancyPoint point)
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        StoryTransitionResult nested = default!;
        var reentrant = new ReentrantChoices(point,
            () => nested = provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed,
                new Dictionary<string, string> { ["branch"] = "nested" }),
            new Dictionary<string, string> { ["branch"] = "outer" });
        var outer = provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed, reentrant);

        Assert.True(nested.Accepted);
        Assert.Equal(StoryTransitionStatus.InvalidTransition, outer.Status);
        Assert.Contains("recorded once", outer.Detail);
        // The intended retirement is retained, once, with its own outcome and choices.
        var record = Assert.Single(provider.Occurrences("salvage-run").Records);
        Assert.Equal(occurrence.OccurrenceId, record.OccurrenceId);
        Assert.Equal(StoryOutcome.Failed, record.Outcome);
        Assert.Equal("nested", record.Choices["branch"]);
        Assert.Equal(1, service.Ledger.Count);
        Assert.True(StoryStateCodec.Validate(world.Persistence.Provider!.Capture()));
    }

    /// <summary>
    /// A reload while the collection is being read means the outer call belongs to a world that is
    /// gone. It is refused as stale, and the restored save is exactly what was captured.
    /// </summary>
    [Theory]
    [InlineData(ReentrancyPoint.GetEnumerator)]
    [InlineData(ReentrancyPoint.MoveNext)]
    [InlineData(ReentrancyPoint.Dispose)]
    public void ReloadingTheSaveWhileTheSuppliedChoicesAreReadRefusesTheRetirementAsStale(ReentrancyPoint point)
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        var saved = world.Persistence.Provider!.Capture();
        var beforeSession = world.SessionId;
        var reentrant = new ReentrantChoices(point, () => world.StartAndRestore(saved),
            new Dictionary<string, string> { ["branch"] = "left" });
        var result = provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed, reentrant);

        Assert.Equal(StoryTransitionStatus.StaleSession, result.Status);
        Assert.NotEqual(beforeSession, world.SessionId);
        // The reloaded save holds the occurrence exactly as it was persisted: still unresolved.
        Assert.Equal(1, service.Ledger.Count);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var restored));
        Assert.Equal(StoryOccurrenceState.Offered, restored.State);
        Assert.Empty(provider.Occurrences("salvage-run").Records);
        Assert.Equal(occurrence.OccurrenceId, Assert.Single(provider.Unresolved("salvage-run").Occurrences).OccurrenceId);
        Assert.True(StoryStateCodec.Validate(world.Persistence.Provider!.Capture()));
        // The occurrence is still retirable in the session that is actually loaded.
        Assert.True(provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed,
            new Dictionary<string, string> { ["branch"] = "left" }).Accepted);
    }

    /// <summary>
    /// A collection the module cannot read safely is a refusal, never a partial record and never an
    /// exception out of a method contracted to return a result. The module and every other owner stay
    /// healthy afterwards.
    /// </summary>
    [Fact]
    public void UnreadableSuppliedChoicesAreRefusedWithoutMutatingAnything()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        foreach (var hostile in new IReadOnlyDictionary<string, string>[]
        {
            ThrowingChoices.OnMoveNext(new InvalidOperationException("Collection was modified.")),
            ThrowingChoices.OnMoveNext(new NullReferenceException()),
            ThrowingChoices.OnDispose(new InvalidOperationException("teardown")),
            new EndlessChoices(),
            new DuplicateKeyChoices("branch", "left", "right"),
            new NullEntryChoices()
        })
        {
            var refused = provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed, hostile);
            Assert.Equal(StoryTransitionStatus.InvalidTransition, refused.Status);
            Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var untouched));
            Assert.Equal(StoryOccurrenceState.Offered, untouched.State);
            Assert.Null(untouched.Outcome);
            Assert.Empty(untouched.Choices);
            // Capture is unaffected, so no other owner's saves are put at risk by a bad caller.
            Assert.True(StoryStateCodec.Validate(world.Persistence.Provider!.Capture()));
        }
        // An endless collection is read at most one item past the supported maximum.
        var endless = new EndlessChoices();
        Assert.Equal(StoryTransitionStatus.InvalidTransition,
            provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed, endless).Status);
        Assert.Equal(StoryMissionDefinition.MaxChoiceKeys + 1, endless.Yielded);
        // An unavailable caller is refused BEFORE its collection is touched at all.
        var counted = new EndlessChoices();
        world.Persistence.StateReady = false;
        Assert.Equal(StoryTransitionStatus.Unavailable,
            provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed, counted).Status);
        Assert.Equal(0, counted.Yielded);
        world.Persistence.StateReady = true;
        // The occurrence is still perfectly retirable with a well-behaved collection.
        Assert.True(provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed,
            new Dictionary<string, string> { ["branch"] = "left" }).Accepted);
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
        Assert.True(anima.Retire(done.OccurrenceId, StoryOutcome.Failed).Accepted);
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
            Assert.Equal(anima.ProviderId, item.Id.Provider);
        });
        // A retired occurrence, its outcome and its choices belong to the retained query, and this
        // snapshot type has no place to carry them.
        Assert.DoesNotContain(done.OccurrenceId, unresolved.Occurrences.Select(item => item.OccurrenceId));
        var record = Assert.Single(anima.Occurrences("salvage-run").Records);
        Assert.Equal(done.OccurrenceId, record.OccurrenceId);
        Assert.Equal(StoryOutcome.Failed, record.Outcome);
        Assert.Equal(new[] { StoryOccurrenceStage.Offered, StoryOccurrenceStage.Active },
            Enum.GetValues(typeof(StoryOccurrenceStage)).Cast<StoryOccurrenceStage>().ToArray());
        // Another provider's unresolved content is not listed here.
        Assert.DoesNotContain(foreign.OccurrenceId, unresolved.Occurrences.Select(item => item.OccurrenceId));
        Assert.Equal(foreign.OccurrenceId, Assert.Single(other.Unresolved("salvage-run").Occurrences).OccurrenceId);

        // The listed identities are usable: they are what a provider transitions after a reload, and
        // each occurrence carries its own catalog entry, so a second one of the same definition is
        // still acceptable rather than blocked by the first.
        var session = unresolved.SessionId!.Value;
        Assert.True(anima.Activate(session, offered.OccurrenceId).Accepted);
        Assert.True(anima.Retire(session, active.OccurrenceId, StoryOutcome.Failed).Accepted);
        Assert.Single(anima.Unresolved("salvage-run").Occurrences);
        // Unavailable answers carry no snapshots and no session.
        world.Persistence.StateReady = false;
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
            provider.Retire(first, occurrence.OccurrenceId, StoryOutcome.Failed),
            provider.Retire(first, occurrence.OccurrenceId, StoryOutcome.Failed, new Dictionary<string, string> { ["branch"] = "left" }),
            provider.Retire(Guid.NewGuid(), occurrence.OccurrenceId, StoryOutcome.Failed)
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
        world.Persistence.StateReady = false;
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
        var undeclared = provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed,
            new Dictionary<string, string> { ["ending"] = "left" });
        Assert.Equal(StoryTransitionStatus.InvalidTransition, undeclared.Status);
        Assert.Contains("'ending' is not declared", undeclared.Detail);
        var oversized = provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed,
            new Dictionary<string, string> { ["branch"] = new string('v', StoryMissionDefinition.MaxChoiceValueBytes + 1) });
        Assert.Equal(StoryTransitionStatus.LimitExceeded, oversized.Status);
        Assert.Contains("encoded bytes", oversized.Detail);
        // Both refusals changed nothing, so the declared outcome can still be recorded.
        Assert.True(provider.Retire(occurrence.OccurrenceId, StoryOutcome.Failed,
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
    /// The per-provider shares only add up while the ledger holds at most as many provider namespaces
    /// as can be bound at once. A restored save may hold rows from providers that are no longer
    /// loaded: they are never pruned and hold no lease, so without a global backstop later offers
    /// could push a capture past the payload bound and block every mod's saves. This is exercised on
    /// the ledger itself, because a save holding unaccountable owned content suspends the module.
    /// </summary>
    [Fact]
    public void HistoricalProvidersCountAgainstTheGlobalPayloadSoAnOfferIsRefusedBeforeMutating()
    {
        var ledger = new StoryLedger();
        ledger.Restore(HistoricalRows(30, 19));
        int restored = ledger.Count;
        var reservation = WorstDefinition().ReservedChoiceBytes;
        string refusal = "";
        for (int provider = 0; provider < 4 && refusal.Length == 0; provider++)
        {
            var id = new StoryContentId("fresh" + provider, "salvage-run");
            while (true)
            {
                int before = ledger.Count;
                var status = ledger.Offer(id, StoryRetention.Campaign, Guid.NewGuid(), reservation, out var detail);
                if (status == StoryLedgerStatus.Accepted) continue;
                Assert.Equal(StoryLedgerStatus.LimitExceeded, status);
                // Refused BEFORE mutating, whichever bound spoke.
                Assert.Equal(before, ledger.Count);
                if (detail.Contains("would exceed its " + StoryLedger.LedgerPayloadBudget + "-byte payload")) refusal = detail;
                break;
            }
        }
        // The GLOBAL bound is what eventually refuses, not only the per-provider share.
        Assert.Contains("space reserved to record outcomes for occurrences already admitted", refusal);
        Assert.True(ledger.Count > restored);
        // Everything admitted can still record its outcome, and the result still captures.
        foreach (var entry in ledger.Entries.Where(row => row.Id.Provider!.StartsWith("fresh", StringComparison.Ordinal)).ToArray())
            Assert.Equal(StoryLedgerStatus.Accepted,
                ledger.Retire(entry.Id, entry.OccurrenceId, StoryOutcome.Completed, WorstChoices(WorstDefinition()), out _));
        var bytes = StoryStateCodec.Encode(ledger.Entries);
        Assert.True(bytes.Length <= StoryStateCodec.MaxBytes);
        Assert.True(StoryStateCodec.Validate(bytes));
    }

    /// <summary>
    /// The same sum on the decode side: a payload can be small TODAY and still reserve more than the
    /// bounded payload for outcomes it has not recorded yet. Restoring it would admit content that
    /// could never be finished, so it is refused by both sides of the codec.
    /// </summary>
    [Fact]
    public void APayloadThatFitsTodayButReservesTooMuchIsRefusedOnBothSidesOfTheCodec()
    {
        var rows = HistoricalRows(33, 19);
        int encoded = StoryStateCodec.HeaderBytes + rows.Sum(StoryStateCodec.EncodedSize);
        int reserved = StoryStateCodec.HeaderBytes + rows.Sum(StoryLedger.Footprint);
        Assert.True(encoded <= StoryStateCodec.MaxBytes);
        Assert.True(reserved > StoryLedger.LedgerPayloadBudget);
        Assert.Contains("reserves more than its bounded payload", StoryLedger.RefuseBounds(rows));
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Encode(rows));
        Assert.False(StoryStateCodec.Validate(Craft(rows)));
        // A ledger just inside the bound is accepted, so the refusal is the bound and not the shape.
        var fitting = HistoricalRows(32, 19);
        Assert.Null(StoryLedger.RefuseBounds(fitting));
        Assert.True(StoryStateCodec.Validate(StoryStateCodec.Encode(fitting)));
    }

    /// <summary>Unresolved campaign rows of providers that are not loaded in this session.</summary>
    private static StoryOccurrenceEntry[] HistoricalRows(int providers, int perProvider)
    {
        var rows = new List<StoryOccurrenceEntry>();
        long sequence = 0;
        for (int provider = 0; provider < providers; provider++)
            for (int index = 0; index < perProvider; index++)
                rows.Add(new StoryOccurrenceEntry(new StoryContentId("historic" + provider, "salvage-run"),
                    Guid.NewGuid(), StoryRetention.Campaign, ++sequence,
                    choiceReservation: WorstDefinition().ReservedChoiceBytes));
        return rows.ToArray();
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
            Assert.True(leases[index].Retire(reserved[index], StoryOutcome.Failed, worstPayload).Accepted);
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
        Assert.True(provider.Retire(pending.OccurrenceId, StoryOutcome.Failed, WorstChoices(definition)).Accepted);
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

    // --- native world ------------------------------------------------------------------------

    /// <summary>
    /// Registration is what installs content into the game, because vanilla resolves a saved story
    /// payload out of its catalog while it deserializes. An identifier the world already holds is
    /// never replaced, and a refused installation leaves nothing registered either.
    /// </summary>
    [Fact]
    public void RegistrationInstallsIntoTheWorldAndNeverReplacesWhatIsAlreadyThere()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        var identifier = StoryContentPolicy.Identifier(new StoryContentId(provider.ProviderId, "salvage-run"));

        world.World.AddForeign(identifier);
        var refused = provider.Register(Definition());
        Assert.Equal(StoryRegistrationStatus.IdentifierInUse, refused.Status);
        Assert.Contains("never replaces existing content", refused.Diagnostic);
        // The registry was rolled back with the world: nothing half-registered survives.
        Assert.False(service.Registry.Contains(new StoryContentId(provider.ProviderId, "salvage-run")));
        Assert.Equal(StoryTransitionStatus.InvalidTransition, provider.Offer("salvage-run").Status);

        // The same definition installs once the world no longer holds that identifier.
        var clean = new FakeWorld();
        using var second = clean.Service(host);
        clean.StartAndRestore();
        var owner = second.AcquireProvider(plugin).Provider!;
        Assert.True(owner.Register(Definition()).Succeeded);
        Assert.True(clean.World.IsInstalled(StoryContentPolicy.Identifier(new StoryContentId(owner.ProviderId, "salvage-run"))));

        // A world that cannot install anything registers nothing either.
        var blocked = new FakeWorld();
        blocked.World.Unavailable = true;
        using var third = blocked.Service(host);
        blocked.StartAndRestore();
        var refusedOwner = third.AcquireProvider(plugin).Provider!;
        var unavailable = refusedOwner.Register(Definition());
        Assert.Equal(StoryRegistrationStatus.Unavailable, unavailable.Status);
        Assert.False(third.Registry.Contains(new StoryContentId(refusedOwner.ProviderId, "salvage-run")));
    }

    /// <summary>
    /// An activation is an acceptance IN THE GAME first. If the world refuses or cannot be reached,
    /// nothing is recorded, so a caller can never hold an activation the game never made.
    /// </summary>
    [Fact]
    public void ActivationIsRecordedOnlyAfterTheWorldAcceptsTheMission()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(occurrence.Accepted);
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);

        world.World.RefuseAccept = true;
        var refused = provider.Activate(occurrence.OccurrenceId);
        Assert.Equal(StoryTransitionStatus.InvalidTransition, refused.Status);
        Assert.Contains("did not accept", refused.Detail);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var untouched));
        Assert.Equal(StoryOccurrenceState.Offered, untouched.State);

        world.World.RefuseAccept = false;
        world.World.Unavailable = true;
        Assert.Equal(StoryTransitionStatus.Unavailable, provider.Activate(occurrence.OccurrenceId).Status);
        Assert.Equal(StoryOccurrenceState.Offered, untouched.State);

        world.World.Unavailable = false;
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        Assert.True(world.World.IsActive(identifier));
        Assert.Equal(StoryOccurrenceState.Active, untouched.State);
        // A second occurrence has its own identifier, so the game's duplicate refusal does not apply
        // to it; the world accepts it as its own mission.
        var again = provider.Offer("salvage-run");
        Assert.True(provider.Activate(again.OccurrenceId).Accepted);
        Assert.True(world.World.IsActive(FakeWorld.Native(provider, "salvage-run", again.OccurrenceId)));
    }

    /// <summary>
    /// The outcomes a caller owns end the mission in the GAME first and are recorded only once the
    /// game no longer holds it. A completion is not one of them: it is recorded when the game is
    /// observed completing the mission, with the choices declared while it was still live.
    /// </summary>
    [Fact]
    public void CallerOutcomesEndTheMissionFirstAndCompletionsComeFromTheGame()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var first = provider.Offer("salvage-run");
        var identifier = FakeWorld.Native(provider, "salvage-run", first.OccurrenceId);
        Assert.True(provider.Activate(first.OccurrenceId).Accepted);

        var declared = provider.Retire(first.OccurrenceId, StoryOutcome.Completed);
        Assert.Equal(StoryTransitionStatus.InvalidTransition, declared.Status);
        Assert.Contains("observed completion", declared.Detail);
        Assert.True(service.Ledger.TryGet(first.OccurrenceId, out var pending));
        Assert.Equal(StoryOccurrenceState.Active, pending.State);
        Assert.True(world.World.IsActive(identifier));

        Assert.True(provider.DeclareChoices(first.OccurrenceId, new Dictionary<string, string> { ["branch"] = "left" }).Accepted);
        world.CompleteInGame(provider, "salvage-run", first.OccurrenceId);
        Assert.True(provider.IsCompleted("salvage-run").Completed);
        Assert.Equal("left", Assert.Single(provider.Occurrences("salvage-run").Records).Choices["branch"]);

        // Abandonment: the mission is removed from the world before the outcome is recorded.
        var job = provider.Offer("salvage-run");
        var jobIdentifier = FakeWorld.Native(provider, "salvage-run", job.OccurrenceId);
        Assert.True(provider.Activate(job.OccurrenceId).Accepted);
        Assert.True(world.World.IsActive(jobIdentifier));
        Assert.True(provider.Retire(job.OccurrenceId, StoryOutcome.Abandoned).Accepted);
        Assert.False(world.World.IsActive(jobIdentifier));
        Assert.Equal(StoryOutcome.Abandoned, provider.Occurrences("salvage-run").Records[1].Outcome);
    }

    /// <summary>
    /// The game persists accepted missions itself, so a reload can disagree with the ledger. The
    /// module correlates the two by occurrence identifier and REPORTS the disagreement; it repairs
    /// nothing, invents no acceptance and adopts no mission whose occurrence it never minted.
    /// </summary>
    [Fact]
    public void ReloadCorrelatesTheLedgerWithTheWorldWithoutInventingOrAdoptingState()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var bytes = world.Persistence.Provider!.Capture();

        // Agreement: the world still holds it, so there is nothing to report.
        world.StartAndRestore(bytes);
        Assert.Empty(service.Reconciliation);
        Assert.Null(service.SuspendedReason);
        Assert.Equal(occurrence.OccurrenceId, Assert.Single(provider.Unresolved("salvage-run").Occurrences).OccurrenceId);
        // The occurrence's catalog entry is reinstalled by the reload, without the provider doing it.
        Assert.True(world.World.IsInstalled(identifier));

        // The world ended it while this module was not the one observing: reported, never rewritten.
        world.World.CompleteInWorld(identifier);
        world.StartAndRestore(bytes);
        var reported = Assert.Single(service.Reconciliation);
        Assert.Contains(identifier, reported);
        Assert.Contains("holds no such mission", reported);
        Assert.Contains("archived", reported);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var kept));
        Assert.Equal(StoryOccurrenceState.Active, kept.State);
    }

    /// <summary>Releasing a registration or a provider removes ITS world entries and nothing else.</summary>
    [Fact]
    public void ReleasingRegistrationsUninstallsOnlyTheirOwnWorldEntries()
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
        var registration = anima.Register(Definition()).Definition!;
        Assert.True(other.Register(Definition()).Succeeded);
        var animaIdentifier = StoryContentPolicy.Identifier(new StoryContentId(anima.ProviderId, "salvage-run"));
        var otherIdentifier = StoryContentPolicy.Identifier(new StoryContentId(other.ProviderId, "salvage-run"));
        Assert.True(world.World.IsInstalled(animaIdentifier));
        Assert.True(world.World.IsInstalled(otherIdentifier));

        registration.Dispose();
        Assert.False(world.World.IsInstalled(animaIdentifier));
        Assert.True(world.World.IsInstalled(otherIdentifier));

        var again = anima.Register(Definition()).Definition!;
        Assert.True(world.World.IsInstalled(animaIdentifier));
        other.Dispose();
        Assert.False(world.World.IsInstalled(otherIdentifier));
        Assert.True(world.World.IsInstalled(animaIdentifier));
        Assert.True(again.InternalActive());
        service.Dispose();
        Assert.False(world.World.IsInstalled(animaIdentifier));
    }

    /// <summary>
    /// Every occurrence is installed under its OWN identifier. The game archives a completed story
    /// identifier and refuses a duplicate of it forever, so a shared identifier could be accepted
    /// exactly once per save. Each occurrence therefore needs a distinct identifier.
    /// </summary>
    [Fact]
    public void EachOccurrenceGetsItsOwnCatalogEntrySoARepeatCanStillBeAccepted()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var first = provider.Offer("salvage-run");
        var firstIdentifier = FakeWorld.Native(provider, "salvage-run", first.OccurrenceId);
        Assert.True(world.World.IsInstalled(firstIdentifier));
        Assert.True(provider.Activate(first.OccurrenceId).Accepted);
        world.CompleteInGame(provider, "salvage-run", first.OccurrenceId);
        Assert.True(provider.IsCompleted("salvage-run").Completed);

        var second = provider.Offer("salvage-run");
        var secondIdentifier = FakeWorld.Native(provider, "salvage-run", second.OccurrenceId);
        Assert.NotEqual(firstIdentifier, secondIdentifier);
        // The archived first identifier does not block the second occurrence.
        Assert.True(provider.Activate(second.OccurrenceId).Accepted);
        Assert.True(world.World.IsActive(secondIdentifier));
        // A retired occurrence releases its catalog entry; the live one keeps its own.
        Assert.False(world.World.IsInstalled(firstIdentifier));
        Assert.True(world.World.IsInstalled(secondIdentifier));
    }

    /// <summary>
    /// The game's mission observers run consumer code INSIDE the native acceptance. If that code
    /// invalidates the operation, the acceptance is undone: the world must never be left holding a
    /// mission this ledger does not record.
    /// </summary>
    [Fact]
    public void AnAcceptanceInvalidatedByReentrantConsumerCodeIsUndoneInTheWorld()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        world.World.DuringAccept = () => provider.Dispose();       // the consumer tears itself down
        var refused = provider.Activate(occurrence.OccurrenceId);

        Assert.Equal(StoryTransitionStatus.Unavailable, refused.Status);
        Assert.Contains("undone", refused.Detail);
        Assert.Equal(1, world.World.Rollbacks);
        Assert.False(world.World.IsActive(identifier));
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var entry));
        Assert.Equal(StoryOccurrenceState.Offered, entry.State);
        Assert.Null(service.FaultReason);
    }

    /// <summary>A rollback that cannot be performed blocks the module rather than leaving the two disagreeing.</summary>
    [Fact]
    public void AFailedRollbackBlocksTheModuleForTheSession()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        world.World.RefuseRollback = true;
        world.World.DuringAccept = () => provider.Dispose();
        var refused = provider.Activate(occurrence.OccurrenceId);

        Assert.Equal(StoryTransitionStatus.Unavailable, refused.Status);
        Assert.Contains("blocked", refused.Detail);
        Assert.NotNull(service.FaultReason);
        Assert.Contains("could not be undone", service.FaultReason!);
        Assert.Contains(world.Reports, report => report.Contains("blocked"));
    }

    /// <summary>A reentrant story mutation from inside a native call is refused as busy, never interleaved.</summary>
    [Fact]
    public void AStoryMutationFromInsideANativeCallIsRefusedAsBusy()
    {
        var provider = Provider(out var world, out _, out _, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        StoryTransitionResult reentrant = default!;
        world.World.DuringAccept = () => reentrant = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        Assert.Equal(StoryTransitionStatus.Busy, reentrant.Status);
    }

    /// <summary>
    /// A completion is the GAME's: it is recorded from the observed native completion, and refused
    /// when a caller tries to declare one for content that was never accepted or was abandoned.
    /// </summary>
    [Fact]
    public void CompletionsAreObservedFromTheGameAndNeverDeclaredByACaller()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var neverAccepted = provider.Offer("salvage-run");
        var declared = provider.Retire(neverAccepted.OccurrenceId, StoryOutcome.Completed);
        Assert.Equal(StoryTransitionStatus.InvalidTransition, declared.Status);
        Assert.Contains("observed completion", declared.Detail);
        Assert.False(provider.IsCompleted("salvage-run").Completed);
        // An undefined outcome is a refusal, before any native call.
        int releases = world.World.Releases;
        Assert.Equal(StoryTransitionStatus.InvalidTransition,
            provider.Retire(neverAccepted.OccurrenceId, (StoryOutcome)99).Status);
        Assert.Equal(releases, world.World.Releases);

        Assert.True(provider.Activate(neverAccepted.OccurrenceId).Accepted);
        world.CompleteInGame(provider, "salvage-run", neverAccepted.OccurrenceId);
        var record = Assert.Single(provider.Occurrences("salvage-run").Records);
        Assert.Equal(StoryOutcome.Completed, record.Outcome);
        Assert.True(provider.IsCompleted("salvage-run").Completed);

        // A neutral removal says nothing about why it ended: the occurrence stays unresolved.
        var second = provider.Offer("salvage-run");
        Assert.True(provider.Activate(second.OccurrenceId).Accepted);
        var secondIdentifier = FakeWorld.Native(provider, "salvage-run", second.OccurrenceId);
        world.Missions.Publish(MissionTransitionKind.Removed, secondIdentifier);
        Assert.True(service.Ledger.TryGet(second.OccurrenceId, out var stillActive));
        Assert.Equal(StoryOccurrenceState.Active, stillActive.State);
        // A reported failure is a FACT about a mission the game still holds, not its outcome: the
        // occurrence stays live and owned, and its catalog entry stays installed for the retry.
        world.Missions.Publish(MissionTransitionKind.Failed, secondIdentifier);
        Assert.Equal(StoryOccurrenceState.Active, stillActive.State);
        Assert.True(stillActive.FailureObserved);
        Assert.True(world.World.IsInstalled(secondIdentifier));
        Assert.Single(provider.Occurrences("salvage-run").Records);
        // The removal that follows the failure is what settles it.
        world.World.CompleteInWorld(secondIdentifier);
        world.Missions.Publish(MissionTransitionKind.Removed, secondIdentifier);
        Assert.Equal(StoryOutcome.Failed, provider.Occurrences("salvage-run").Records[1].Outcome);
        Assert.False(world.World.IsInstalled(secondIdentifier));
    }

    /// <summary>
    /// An owned mission the game still holds keeps its catalog entry after the provider is gone, so
    /// the outcome the game produces is still recorded. Nothing of another provider is touched.
    /// </summary>
    [Fact]
    public void OwnershipOfAHeldMissionOutlivesTheProviderLease()
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
        var occurrence = anima.Offer("salvage-run");
        Assert.True(anima.Activate(occurrence.OccurrenceId).Accepted);
        var identifier = FakeWorld.Native(anima, "salvage-run", occurrence.OccurrenceId);
        var otherIdentifier = StoryContentPolicy.Identifier(new StoryContentId(other.ProviderId, "salvage-run"));

        anima.Dispose();
        // The base definition goes with the lease; the held occurrence keeps its own entry.
        Assert.False(world.World.IsInstalled(StoryContentPolicy.Identifier(new StoryContentId(anima.ProviderId, "salvage-run"))));
        Assert.True(world.World.IsInstalled(identifier));
        Assert.True(world.World.IsInstalled(otherIdentifier));

        // The game completes it anyway, and the module still records that.
        world.CompleteInGame(anima, "salvage-run", occurrence.OccurrenceId);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var recorded));
        Assert.Equal(StoryOutcome.Completed, recorded.Outcome);
        Assert.False(world.World.IsInstalled(identifier));
        Assert.True(world.World.IsInstalled(otherIdentifier));
    }

    /// <summary>
    /// A save holding owned content this module cannot account for suspends it, fail-closed. Nothing
    /// native is removed and no persisted byte is rewritten: the content stays as the save holds it.
    /// </summary>
    [Fact]
    public void OwnedContentThisModuleCannotAccountForSuspendsItWithoutTouchingTheSave()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        Assert.True(provider.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
        var orphan = FakeWorld.Native(provider, "salvage-run", Guid.NewGuid());
        world.World.AdoptInWorld(orphan);

        world.StartAndRestore();
        Assert.NotNull(service.SuspendedReason);
        Assert.Contains("cannot account for", service.SuspendedReason!);
        Assert.Contains(world.Reports, report => report.Contains("suspended"));
        // Fail-closed: nothing is accepted or recorded on top of a world it does not understand.
        Assert.Equal(StoryTransitionStatus.Unavailable, provider.Offer("salvage-run").Status);
        // The orphan is still exactly where the save had it.
        Assert.True(world.World.IsActive(orphan));
        Assert.True(StoryStateCodec.Validate(world.Persistence.Provider!.Capture()));
    }

    /// <summary>An unregistered provider for restored content is the same fail-closed suspension.</summary>
    [Fact]
    public void RestoredContentWhoseProviderIsMissingSuspendsInsteadOfSubstitutingAnything()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        Assert.True(provider.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var bytes = world.Persistence.Provider!.Capture();

        // A fresh module for the same save, with nobody registering that definition.
        var reloaded = new FakeWorld();
        using var without = reloaded.Service(new FakeHost());
        reloaded.StartAndRestore(bytes);
        Assert.NotNull(without.SuspendedReason);
        Assert.Contains("provider is not registered", without.SuspendedReason!);
        Assert.Equal(1, without.Ledger.Count);          // the record is kept, not deleted
        Assert.Empty(reloaded.World.InstalledIdentifiers());
    }

    /// <summary>The unsupported objective kind is refused at registration rather than installed unsafely.</summary>
    [Fact]
    public void ObjectivesTheGameCouldNotSerializeAreRefusedAtRegistration()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        // A kill objective naming a faction the game does not know is refused like every other
        // serialized faction identity; the kind itself is now installable.
        var unsupported = new StoryMissionDefinition("hunt", "Hunt", "Clear the raiders.", Faction,
            new[] { new StoryStep("Destroy them", new[] { StoryObjective.KillEnemies(5, new StoryFactionId("NoSuchClan")) }) });
        var refused = provider.Register(unsupported);
        Assert.Equal(StoryRegistrationStatus.InvalidDefinition, refused.Status);
        Assert.Contains("does not know", refused.Diagnostic);
        Assert.Empty(world.World.InstalledIdentifiers());

        // A faction the game does not know is refused for the same reason: the save would break.
        world.World.ForgetFaction("TradingGuild");
        var unknownFaction = provider.Register(Definition());
        Assert.Equal(StoryRegistrationStatus.InvalidDefinition, unknownFaction.Status);
        Assert.Contains("does not know source faction", unknownFaction.Diagnostic);
    }

    /// <summary>
    /// A completion can arrive in a later session, so the choices declared for it are part of the
    /// occurrence's PERSISTED state, not process memory. They survive a reload into a new module
    /// instance and are written with the outcome the game eventually produces.
    /// </summary>
    [Fact]
    public void DeclaredChoicesArePersistedWithTheOccurrenceAndSurviveAReload()
    {
        var provider = Provider(out var world, out var host, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        Assert.True(provider.DeclareChoices(occurrence.OccurrenceId, new Dictionary<string, string> { ["branch"] = "left" }).Accepted);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var declared));
        Assert.Equal("left", declared.PendingChoices["branch"]);
        var bytes = world.Persistence.Provider!.Capture();

        // A brand new module for the same save: nothing of the old process is left.
        var reloaded = new FakeWorld();
        using var later = reloaded.Service(host);
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        reloaded.StartAndRestore();
        var owner = later.AcquireProvider(plugin).Provider!;
        Assert.True(owner.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
        reloaded.StartAndRestore(bytes);
        Assert.True(later.Ledger.TryGet(occurrence.OccurrenceId, out var restored));
        Assert.Equal("left", restored.PendingChoices["branch"]);

        reloaded.CompleteInGame(owner, "salvage-run", occurrence.OccurrenceId);
        var record = Assert.Single(owner.Occurrences("salvage-run").Records);
        Assert.Equal(StoryOutcome.Completed, record.Outcome);
        Assert.Equal("left", record.Choices["branch"]);
        // The declaration is transferred, not kept beside the record.
        Assert.True(later.Ledger.TryGet(occurrence.OccurrenceId, out var terminal));
        Assert.Empty(terminal.PendingChoices);
        Assert.True(StoryStateCodec.Validate(reloaded.Persistence.Provider!.Capture()));
    }

    /// <summary>
    /// A declaration belongs to the save it was made in. Rolling back to an older generation, where
    /// the occurrence exists but nothing was declared yet, must not apply a newer session's choices.
    /// </summary>
    [Fact]
    public void ADeclarationDoesNotLeakIntoASaveThatWasRolledBackBeforeItWasMade()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var older = world.Persistence.Provider!.Capture();          // before any declaration
        Assert.True(provider.DeclareChoices(occurrence.OccurrenceId, new Dictionary<string, string> { ["branch"] = "left" }).Accepted);

        world.StartAndRestore(older);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var restored));
        Assert.Empty(restored.PendingChoices);
        world.CompleteInGame(provider, "salvage-run", occurrence.OccurrenceId);
        Assert.Empty(Assert.Single(provider.Occurrences("salvage-run").Records).Choices);
    }

    /// <summary>
    /// Older stored state is read as what it meant: schema 1 rows carry no declaration and no
    /// observed failure. A NEWER schema is not guessed at; the owner keeps its bytes instead.
    /// </summary>
    [Fact]
    public void TheCodecReadsTheOlderSchemaAndRefusesANewerOne()
    {
        var id = new StoryContentId("anima", "salvage-run");
        var rows = new[] { new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Campaign, 1) };
        var current = StoryStateCodec.Encode(rows);
        Assert.Equal(StoryStateCodec.SchemaVersion, BitConverter.ToInt32(current, 4));

        // A schema 1 payload: the same row without the pending block and flags byte.
        var legacy = current.Take(current.Length - 2).ToArray();
        Array.Copy(BitConverter.GetBytes(StoryStateCodec.FirstSchemaVersion), 0, legacy, 4, 4);
        Assert.True(StoryStateCodec.Validate(legacy));
        var decoded = Assert.Single(StoryStateCodec.Decode(legacy));
        Assert.Empty(decoded.PendingChoices);
        Assert.False(decoded.FailureObserved);

        var newer = (byte[])current.Clone();
        Array.Copy(BitConverter.GetBytes(StoryStateCodec.SchemaVersion + 1), 0, newer, 4, 4);
        Assert.False(StoryStateCodec.Validate(newer));
        Assert.Throws<InvalidDataException>(() => StoryStateCodec.Decode(newer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SchemaTwoAtLogicalBudgetRestoresAndRepublishesWithoutExtraOverhead(bool globalBoundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "vg-story-budget-" + Guid.NewGuid().ToString("N"));
        try
        {
            var owner = StoryProviderIdentity.Segment(new StoryHostPlugin(AnimaPlugin, typeof(StoryContentTests).Assembly));
            var rows = new List<StoryOccurrenceEntry>();
            int remaining = globalBoundary ? StoryLedger.LedgerPayloadBudget - StoryStateCodec.HeaderBytes : StoryLedger.ProviderPayloadBudget;
            int providers = globalBoundary ? 33 : 1;
            for (int index = 0; index < providers; index++)
            {
                var id = new StoryContentId(index == 0 ? owner : "provider-" + index, "salvage-run");
                int allowance = index < 31 ? Math.Min(remaining, StoryLedger.ProviderPayloadBudget) : remaining / (providers - index);
                var empty = Enumerable.Range(0, 20).Select(_ => new StoryOccurrenceEntry(id, Guid.NewGuid(), StoryRetention.Campaign, rows.Count + 1)).ToArray();
                int reservation = allowance - empty.Sum(StoryStateCodec.EncodedSize);
                Assert.True(reservation >= 0);
                foreach (var entry in empty)
                {
                    int reserved = Math.Min(reservation, StoryMissionDefinition.MaxChoiceBytesPerOccurrence);
                    rows.Add(new StoryOccurrenceEntry(id, entry.OccurrenceId, StoryRetention.Campaign, rows.Count + 1, choiceReservation: reserved));
                    reservation -= reserved;
                }
                Assert.Equal(0, reservation);
                remaining -= allowance;
            }
            Assert.Equal(0, remaining);
            Assert.Null(StoryLedger.RefuseBounds(rows));
            var legacy = StoryStateCodec.Encode(rows);
            Array.Copy(BitConverter.GetBytes(2), 0, legacy, 4, 4);
            var store = new GenerationStore(root);
            var hash = new string('a', 64);
            var oldCodec = new OwnerSchemaCodec(StoryStateCodec.Owner, 2, StoryStateCodec.Validate);
            store.Publish("slot", hash, Guid.NewGuid(), new Dictionary<string, byte[]> { [StoryStateCodec.Owner] = oldCodec.Encode(legacy) });
            using var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected migration fault", error));
            hub.SetCapability("session-lifecycle", true, "Bound."); hub.SetCapability("save-outcomes", true, "Bound.");
            using var persistence = new PersistenceService(hub, store, path => path, _ => hash);
            var host = new FakeHost();
            hub.SetCapability("owned-story", true, "Test bindings.");
            using var service = new StoryContentService(hub.Services, persistence, hub, host.Authenticate, null, hub.CheckThread);
            var plugin = new object();
            host.Register(plugin, AnimaPlugin);
            Assert.True(service.AcquireProvider(plugin).Provider!.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
            var session = hub.Begin(SessionOrigin.SaveLoad, "slot");
            hub.PlayerReady(session);
            hub.GameplayInitialized(session);
            Assert.Equal(rows.Count, service.Ledger.Entries.Count());
            var operation = Guid.NewGuid();
            hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, operation, "upgraded-slot"));
            hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSucceeded, hub.CurrentSession, operation, "upgraded-slot"));
            var codec = new OwnerSchemaCodec(StoryStateCodec.Owner, StoryStateCodec.SchemaVersion, StoryStateCodec.Validate);
            var payload = codec.Decode(store.Load("upgraded-slot", hash)!.Owners[StoryStateCodec.Owner]).Payload!;
            Assert.Equal(legacy.Length, payload.Length);
            Assert.Equal(rows.Select(row => row.OccurrenceId), StoryStateCodec.Decode(payload).Select(row => row.OccurrenceId));
            Assert.Equal(legacy, oldCodec.Decode(store.Load("slot", hash)!.Owners[StoryStateCodec.Owner]).Payload);
            var reloaded = hub.Begin(SessionOrigin.SaveLoad, "upgraded-slot");
            hub.PlayerReady(reloaded);
            hub.GameplayInitialized(reloaded);
            Assert.Equal(rows.Count, service.Ledger.Entries.Count());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacyOwnerEnvelopeMigratesThroughTheRealGenerationCoordinator()
    {
        var root = Path.Combine(Path.GetTempPath(), "vg-story-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var occurrence = Guid.NewGuid();
            var id = new StoryContentId(StoryProviderIdentity.Segment(new StoryHostPlugin(AnimaPlugin, typeof(StoryContentTests).Assembly)), "salvage-run");
            var current = StoryStateCodec.Encode(new[] { new StoryOccurrenceEntry(id, occurrence, StoryRetention.Campaign, 1) });
            var legacy = current.Take(current.Length - 2).ToArray();
            Array.Copy(BitConverter.GetBytes(1), 0, legacy, 4, 4);
            var store = new GenerationStore(root);
            var hash = new string('a', 64);
            var oldCodec = new OwnerSchemaCodec(StoryStateCodec.Owner, 1, StoryStateCodec.Validate);
            store.Publish("slot", hash, Guid.NewGuid(), new Dictionary<string, byte[]> { [StoryStateCodec.Owner] = oldCodec.Encode(legacy) });
            using var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected migration fault", error));
            hub.SetCapability("session-lifecycle", true, "Bound."); hub.SetCapability("save-outcomes", true, "Bound.");
            using var persistence = new PersistenceService(hub, store, path => path, _ => hash);
            var host = new FakeHost();
            hub.SetCapability("owned-story", true, "Test bindings.");
            using var service = new StoryContentService(hub.Services, persistence, hub, host.Authenticate, null, hub.CheckThread);
            var plugin = new object();
            host.Register(plugin, AnimaPlugin);
            var provider = service.AcquireProvider(plugin).Provider!;
            Assert.True(provider.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
            var session = hub.Begin(SessionOrigin.SaveLoad, "slot");
            hub.PlayerReady(session);
            hub.GameplayInitialized(session);
            var restored = provider.Unresolved("salvage-run");
            Assert.Equal(StoryKnowledge.Known, restored.Knowledge);
            Assert.Equal(occurrence, Assert.Single(restored.Occurrences).OccurrenceId);
            var operation = Guid.NewGuid();
            hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, operation, "upgraded-slot"));
            hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSucceeded, hub.CurrentSession, operation, "upgraded-slot"));
            var upgraded = store.Load("upgraded-slot", hash)!;
            var currentCodec = new OwnerSchemaCodec(StoryStateCodec.Owner, StoryStateCodec.SchemaVersion, StoryStateCodec.Validate);
            var payload = currentCodec.Decode(upgraded.Owners[StoryStateCodec.Owner]).Payload!;
            Assert.Equal(StoryStateCodec.SchemaVersion, BitConverter.ToInt32(payload, 4));
            Assert.Equal(occurrence, Assert.Single(StoryStateCodec.Decode(payload)).OccurrenceId);
            Assert.Equal(legacy, oldCodec.Decode(store.Load("slot", hash)!.Owners[StoryStateCodec.Owner]).Payload);
            Assert.True(provider.Withdraw(occurrence).Accepted);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyCampaignOccurrenceMigratesThroughTheServiceAndCanStillComplete(bool active)
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var offered = provider.Offer("salvage-run");
        Assert.True(offered.Accepted);
        if (active) Assert.True(provider.Activate(offered.OccurrenceId).Accepted);
        var captured = Assert.Single(StoryStateCodec.Decode(world.Persistence.Provider!.Capture()));
        var current = StoryStateCodec.Encode(new[] { new StoryOccurrenceEntry(captured.Id, captured.OccurrenceId, captured.Retention,
            captured.Sequence, captured.State, captured.Outcome, captured.Choices, captured.ChoiceReservation) });
        // Schema 1 has no definition snapshot, pending choices or failure byte.
        var legacy = current.Take(current.Length - 2).ToArray();
        Array.Copy(BitConverter.GetBytes(1), 0, legacy, 4, 4);
        world.StartAndRestore(legacy);
        var restored = Assert.Single(provider.Unresolved("salvage-run").Occurrences);
        Assert.Equal(offered.OccurrenceId, restored.OccurrenceId);
        Assert.Equal(active ? StoryOccurrenceStage.Active : StoryOccurrenceStage.Offered, restored.Stage);
        Assert.Null(service.SuspendedReason);
        if (!active) Assert.True(provider.Activate(restored.OccurrenceId).Accepted);
        world.CompleteInGame(provider, "salvage-run", restored.OccurrenceId);
        Assert.True(provider.IsCompleted("salvage-run").Completed);
        var upgraded = world.Persistence.Provider.Capture();
        Assert.Equal(StoryStateCodec.SchemaVersion, BitConverter.ToInt32(upgraded, 4));
        world.StartAndRestore(upgraded);
        Assert.True(provider.IsCompleted("salvage-run").Completed);
        world.StartAndRestore(legacy);
        Assert.False(provider.IsCompleted("salvage-run").Completed);
        Assert.Equal(offered.OccurrenceId, Assert.Single(provider.Unresolved("salvage-run").Occurrences).OccurrenceId);
    }

    /// <summary>
    /// The game leaves a failed story mission in the player's list and offers to retry it, so a
    /// reported failure is a fact about a live occurrence, not its terminal outcome: the occurrence
    /// stays active and owned, and the removal that eventually follows is what settles it.
    /// </summary>
    [Fact]
    public void AnObservedFailureKeepsTheOccurrenceLiveUntilTheMissionIsActuallyGone()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        world.Missions.Publish(MissionTransitionKind.Failed, identifier);

        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var live));
        Assert.Equal(StoryOccurrenceState.Active, live.State);
        Assert.True(live.FailureObserved);
        Assert.True(world.World.IsInstalled(identifier));           // the retry path still resolves
        Assert.Empty(provider.Occurrences("salvage-run").Records);
        Assert.Equal(occurrence.OccurrenceId, Assert.Single(provider.Unresolved("salvage-run").Occurrences).OccurrenceId);

        // Saved and reloaded, the failure is still just a fact about a live occurrence: no orphan.
        var bytes = world.Persistence.Provider!.Capture();
        world.StartAndRestore(bytes);
        Assert.Null(service.SuspendedReason);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var reloaded));
        Assert.True(reloaded.FailureObserved);
        Assert.Equal(StoryOccurrenceState.Active, reloaded.State);

        // The game completing it after a retry is a completion, not a failure.
        world.CompleteInGame(provider, "salvage-run", occurrence.OccurrenceId);
        Assert.Equal(StoryOutcome.Completed, Assert.Single(provider.Occurrences("salvage-run").Records).Outcome);
    }

    [Fact]
    public void AFailureFollowedByRemovalIsTheOutcomeThatFailureMeant()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        world.Missions.Publish(MissionTransitionKind.Failed, identifier);
        world.World.CompleteInWorld(identifier);                    // the game no longer holds it
        world.Missions.Publish(MissionTransitionKind.Removed, identifier);

        Assert.Equal(StoryOutcome.Failed, Assert.Single(provider.Occurrences("salvage-run").Records).Outcome);
        Assert.False(world.World.IsInstalled(identifier));
    }

    /// <summary>
    /// A transient condition inside a consumer's own observer — a save starting, callbacks
    /// dispatching — must not undo a native change that already happened. Only stable facts are
    /// re-checked afterwards.
    /// </summary>
    [Fact]
    public void ASaveStartingInsideANativeCallDoesNotRollBackOrBlockAnything()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        world.World.DuringAccept = () => world.Persistence.MutationsPaused = true;   // a save begins
        var accepted = provider.Activate(occurrence.OccurrenceId);

        Assert.True(accepted.Accepted);
        Assert.Equal(0, world.World.Rollbacks);
        Assert.Null(service.FaultReason);
        Assert.True(world.World.IsActive(identifier));
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var recorded));
        Assert.Equal(StoryOccurrenceState.Active, recorded.State);

        // The same for a retirement: the world already changed, so the record follows it.
        world.Persistence.MutationsPaused = false;
        var second = provider.Offer("salvage-run");
        Assert.True(provider.Activate(second.OccurrenceId).Accepted);
        world.World.DuringRelease = () => world.Persistence.MutationsPaused = true;
        Assert.True(provider.Retire(second.OccurrenceId, StoryOutcome.Abandoned).Accepted);
        Assert.Null(service.FaultReason);
    }

    /// <summary>A world that cannot be inspected is not a world that can be trusted with owned content.</summary>
    [Fact]
    public void AWorldSnapshotThatCannotBeTakenSuspendsTheModule()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        Assert.True(provider.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);

        world.World.Unavailable = true;                    // the world cannot be read
        world.StartAndRestore();
        Assert.NotNull(service.SuspendedReason);
        Assert.Contains("could not be inspected", service.SuspendedReason!);
        Assert.Equal(0, world.Protection.AdmittedCount);
        Assert.Contains(world.Reports, report => report.StartsWith("unavailable: ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The guards only ever run content this module vouches for RIGHT NOW, and a session boundary
    /// takes all of that back along with the catalog entries and the per-session flags.
    /// </summary>
    [Fact]
    public void AdmissionsAndSessionStateAreWithdrawnAtEverySessionBoundary()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        Assert.Equal(1, world.Protection.AdmittedCount);
        Assert.False(world.Protection.IsQuarantined(identifier));

        world.StartSession();                              // a new load begins, nothing restored yet
        Assert.Equal(0, world.Protection.AdmittedCount);
        Assert.True(world.Protection.IsQuarantined(identifier));
        // The previous save's catalog entries went with it; definitions belong to the process.
        Assert.False(world.World.IsInstalled(identifier));
        Assert.True(world.World.IsInstalled(StoryContentPolicy.Identifier(new StoryContentId(provider.ProviderId, "salvage-run"))));

        // A suspension in one session does not outlive it.
        world.World.AdoptInWorld(FakeWorld.Native(provider, "salvage-run", Guid.NewGuid()));
        world.StartAndRestore();
        Assert.NotNull(service.SuspendedReason);
        Assert.Equal(0, world.Protection.AdmittedCount);
        world.World.ClearWorld();
        world.StartAndRestore();
        Assert.Null(service.SuspendedReason);
    }

    /// <summary>
    /// A travel objective aimed at a place this world does not have could never be completed, so it
    /// is refused where a world exists to ask, not at registration where one may not.
    /// </summary>
    [Fact]
    public void ATravelTargetTheWorldDoesNotHaveIsRefusedBeforeAnythingIsOffered()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        world.World.ForgetPointOfInterest("poi-guid-1");
        var refused = provider.Offer("salvage-run");
        Assert.Equal(StoryTransitionStatus.InvalidTransition, refused.Status);
        Assert.Contains("does not hold mission target", refused.Detail);
        Assert.Empty(service.Ledger.Entries);

        // No galaxy loaded is not the same as an absent place; nothing is asserted either way.
        world.World.GalaxyLoaded = false;
        var unknown = provider.Offer("salvage-run");
        Assert.Equal(StoryTransitionStatus.Unavailable, unknown.Status);
        Assert.Empty(service.Ledger.Entries);
    }

    /// <summary>
    /// The game's own retry button removes an owned mission and re-adds the same identifier. That is
    /// ONE operation on the SAME occurrence: no outcome is recorded, the catalog entry is kept, and
    /// the reported failure is cleared only because the game accepted it again.
    /// </summary>
    [Fact]
    public void ARetryThroughTheGamesOwnButtonContinuesTheSameOccurrence()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        world.Missions.Publish(MissionTransitionKind.Failed, identifier);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var failed));
        Assert.True(failed.FailureObserved);

        var transactions = (IStoryUiTransaction)service;
        var token = transactions.BeginAbandon(identifier);
        Assert.NotNull(token);
        // The removal the button performs is seen while the transaction is open, and settles nothing.
        world.Missions.Publish(MissionTransitionKind.Removed, identifier);
        Assert.Equal(StoryOccurrenceState.Active, failed.State);
        transactions.EndAbandon(token!, StoryAbandonSettlement.OneReplacementHeld);

        Assert.Equal(StoryOccurrenceState.Active, failed.State);
        Assert.False(failed.FailureObserved);                       // the game accepted it afresh
        Assert.Empty(provider.Occurrences("salvage-run").Records);
        Assert.True(world.World.IsInstalled(identifier));
        Assert.False(world.Protection.IsQuarantined(identifier));
        // A completion after the retry is a completion, once.
        world.CompleteInGame(provider, "salvage-run", occurrence.OccurrenceId);
        Assert.Equal(StoryOutcome.Completed, Assert.Single(provider.Occurrences("salvage-run").Records).Outcome);
    }

    /// <summary>
    /// The same button on a mission the game does NOT put back is the ending it looked like: a
    /// reported failure becomes final, and a plain abandon is an abandonment.
    /// </summary>
    [Fact]
    public void AButtonRemovalTheGameDoesNotUndoSettlesTheOccurrence()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var failing = provider.Offer("salvage-run");
        Assert.True(provider.Activate(failing.OccurrenceId).Accepted);
        var failingId = FakeWorld.Native(provider, "salvage-run", failing.OccurrenceId);
        world.Missions.Publish(MissionTransitionKind.Failed, failingId);
        var transactions = (IStoryUiTransaction)service;
        var failingToken = transactions.BeginAbandon(failingId);
        Assert.NotNull(failingToken);
        world.World.CompleteInWorld(failingId);
        transactions.EndAbandon(failingToken!, StoryAbandonSettlement.NoneHeld);
        Assert.Equal(StoryOutcome.Failed, Assert.Single(provider.Occurrences("salvage-run").Records).Outcome);
        Assert.False(world.World.IsInstalled(failingId));

        var abandoned = provider.Offer("salvage-run");
        Assert.True(provider.Activate(abandoned.OccurrenceId).Accepted);
        var abandonedId = FakeWorld.Native(provider, "salvage-run", abandoned.OccurrenceId);
        var abandonedToken = transactions.BeginAbandon(abandonedId);
        Assert.NotNull(abandonedToken);
        world.World.CompleteInWorld(abandonedId);
        transactions.EndAbandon(abandonedToken!, StoryAbandonSettlement.NoneHeld);
        Assert.Equal(StoryOutcome.Abandoned, provider.Occurrences("salvage-run").Records[1].Outcome);
    }

    /// <summary>Only a live occurrence this module admits may take the button's route at all.</summary>
    [Fact]
    public void TheButtonRouteIsRefusedForAnythingThisModuleCannotVouchFor()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var transactions = (IStoryUiTransaction)service;
        var occurrence = provider.Offer("salvage-run");
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);

        Assert.Null(transactions.BeginAbandon("vgmodapi.story.anima.salvage-run"));      // a base identifier
        Assert.Null(transactions.BeginAbandon(identifier + "-malformed"));
        Assert.Null(transactions.BeginAbandon(
            StoryContentPolicy.OccurrenceIdentifier(new StoryContentId(provider.ProviderId, "salvage-run"), Guid.NewGuid())));
        // One at a time: a second route cannot open while one is running.
        var token = transactions.BeginAbandon(identifier);
        Assert.NotNull(token);
        Assert.Null(transactions.BeginAbandon(identifier));
        transactions.EndAbandon(token!, StoryAbandonSettlement.OneReplacementHeld);
        // And nothing at all once the module is suspended.
        world.World.AdoptInWorld(FakeWorld.Native(provider, "salvage-run", Guid.NewGuid()));
        world.StartAndRestore();
        Assert.NotNull(service.SuspendedReason);
        Assert.Null(transactions.BeginAbandon(identifier));
    }

    /// <summary>
    /// A world can lose a place between offering a mission and accepting it, so the target is checked
    /// again immediately before the game is asked to hold it; nothing native happens on a refusal.
    /// </summary>
    [Fact]
    public void ATargetThatDisappearsBetweenOfferAndActivateRefusesTheAcceptance()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(occurrence.Accepted);
        int accepts = world.World.Accepts;

        world.World.ForgetPointOfInterest("poi-guid-1");
        var refused = provider.Activate(occurrence.OccurrenceId);
        Assert.Equal(StoryTransitionStatus.InvalidTransition, refused.Status);
        Assert.Contains("does not hold mission target", refused.Detail);
        Assert.Equal(accepts, world.World.Accepts);                  // the world was never asked
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var untouched));
        Assert.Equal(StoryOccurrenceState.Offered, untouched.State);
    }

    /// <summary>
    /// A restored occurrence whose destination this world no longer has is not run: it is not vouched
    /// for, so the native guards quarantine it, and its record and its bytes are left alone.
    /// </summary>
    [Fact]
    public void ARestoredOccurrenceWhoseTargetIsGoneIsHeldBackRatherThanRun()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        var bytes = world.Persistence.Provider!.Capture();

        world.World.ForgetPointOfInterest("poi-guid-1");
        world.StartAndRestore(bytes);

        Assert.True(world.Protection.IsQuarantined(identifier));
        Assert.Contains(service.Reconciliation, reason => reason.Contains("does not hold mission target"));
        Assert.Equal(1, service.Ledger.Count);                       // the record is kept
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var kept));
        Assert.Equal(StoryOccurrenceState.Active, kept.State);
        Assert.True(StoryStateCodec.Validate(world.Persistence.Provider!.Capture()));
        // Once the world has the place again, the occurrence is vouched for as before.
        world.World.RestorePointOfInterest("poi-guid-1");
        world.StartAndRestore(bytes);
        Assert.False(world.Protection.IsQuarantined(identifier));
    }

    /// <summary>
    /// The game's abandon/retry holds the SAME boundary a native operation of this module holds:
    /// while it is open nothing else mutates, and no catalog entry is released underneath it. That is
    /// what keeps the game's own re-add from looking up an entry this module just removed.
    /// </summary>
    [Fact]
    public void NothingElseMutatesWhileTheGamesAbandonIsOpenAndNoEntryIsReleasedUnderIt()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        var transactions = (IStoryUiTransaction)service;
        var token = transactions.BeginAbandon(identifier);
        Assert.NotNull(token);

        // Every public mutation, including one for the very occurrence being abandoned, is busy.
        foreach (var refused in new[]
        {
            provider.Retire(occurrence.OccurrenceId, StoryOutcome.Abandoned),
            provider.Offer("salvage-run"),
            provider.Activate(occurrence.OccurrenceId),
            provider.Withdraw(occurrence.OccurrenceId),
            provider.DeclareChoices(occurrence.OccurrenceId, new Dictionary<string, string> { ["branch"] = "left" })
        })
        {
            Assert.Equal(StoryTransitionStatus.Busy, refused.Status);
            Assert.Contains("abandoning or retrying", refused.Detail);
        }
        // Nothing moved in the world or the ledger, and the catalog entry the game is about to look up
        // is still there.
        Assert.Equal(1, service.Ledger.Count);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var untouched));
        Assert.Equal(StoryOccurrenceState.Active, untouched.State);
        Assert.True(world.World.IsInstalled(identifier));

        // A provider tearing itself down mid-transaction does not pull the entry either.
        provider.Dispose();
        Assert.True(world.World.IsInstalled(identifier));
        transactions.EndAbandon(token!, StoryAbandonSettlement.NoneHeld);
        // Once it settles, the deferred removals happen and the boundary is closed again.
        Assert.False(world.World.IsInstalled(identifier));
    }

    /// <summary>
    /// A settlement nobody could establish decides nothing: the occurrence, its reported failure, its
    /// declared choices and its catalog entry are preserved, and the module stops rather than
    /// inventing an ending. The transaction still closes, so nothing is left busy forever.
    /// </summary>
    [Fact]
    public void AnUnknownSettlementPreservesEverythingAndStopsTheModule()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.DeclareChoices(occurrence.OccurrenceId, new Dictionary<string, string> { ["branch"] = "left" }).Accepted);
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        world.Missions.Publish(MissionTransitionKind.Failed, identifier);
        var transactions = (IStoryUiTransaction)service;

        var token = transactions.BeginAbandon(identifier);
        Assert.NotNull(token);
        transactions.EndAbandon(token!, StoryAbandonSettlement.UnknownOrAmbiguous);

        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var kept));
        Assert.Equal(StoryOccurrenceState.Active, kept.State);
        Assert.True(kept.FailureObserved);
        Assert.Equal("left", kept.PendingChoices["branch"]);
        Assert.True(world.World.IsInstalled(identifier));
        Assert.Empty(provider.Occurrences("salvage-run").Records);
        Assert.NotNull(service.FaultReason);
        // The boundary closed: later calls are refused for the fault, not left waiting as busy.
        var later = provider.Offer("salvage-run");
        Assert.Equal(StoryTransitionStatus.Unavailable, later.Status);
        Assert.Contains("blocked", later.Detail);
        Assert.True(world.Protection.IsQuarantined(identifier));
    }

    /// <summary>An unchanged original is not a retry: the reported failure stands and nothing is recorded.</summary>
    [Fact]
    public void AnUnchangedOriginalIsNotARetryAndDoesNotClearTheFailure()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        world.Missions.Publish(MissionTransitionKind.Failed, identifier);
        var transactions = (IStoryUiTransaction)service;

        var token = transactions.BeginAbandon(identifier);
        Assert.NotNull(token);
        transactions.EndAbandon(token!, StoryAbandonSettlement.OriginalStillHeld);

        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var entry));
        Assert.True(entry.FailureObserved);
        Assert.Equal(StoryOccurrenceState.Active, entry.State);
        Assert.Empty(provider.Occurrences("salvage-run").Records);
        Assert.Null(service.FaultReason);
    }

    /// <summary>
    /// The guards can only protect what they can decide about, so a degraded guard stops NEW owned
    /// content: nothing is registered, offered or accepted while it cannot decide.
    /// </summary>
    [Fact]
    public void ADegradedGuardStopsNewOwnedContentWithoutTouchingTheWorld()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        Assert.True(provider.Register(Definition(retention: StoryRetention.Campaign)).Succeeded);
        var offered = provider.Offer("salvage-run");
        Assert.True(offered.Accepted);
        int accepts = world.World.Accepts, installs = world.World.Installs;

        world.DegradeProtection("a scan the guard could not complete");

        Assert.Equal(StoryRegistrationStatus.Unavailable, provider.Register(Definition("other", StoryRetention.Campaign)).Status);
        Assert.Equal(StoryTransitionStatus.Unavailable, provider.Offer("salvage-run").Status);
        Assert.Equal(StoryTransitionStatus.Unavailable, provider.Activate(offered.OccurrenceId).Status);
        Assert.Equal(accepts, world.World.Accepts);
        Assert.Equal(installs, world.World.Installs);
        // Existing content is not vouched for while the guard cannot decide, so it stays quarantined.
        Assert.Equal(0, world.Protection.AdmittedCount);

        // A healthy guard in the next session vouches for it again.
        world.ProtectionHealthy = true;
        world.StartAndRestore();
        Assert.True(provider.Offer("salvage-run").Accepted);
    }

    /// <summary>
    /// The boundary is shared in BOTH directions. The game's own button cannot open while a native
    /// operation of this module is running — a consumer's observer, running inside an acceptance, is
    /// exactly where that would happen — and it never overwrites the operation that is open.
    /// </summary>
    [Fact]
    public void TheGamesButtonCannotOpenWhileThisModulesOwnOperationIsRunning()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        var transactions = (IStoryUiTransaction)service;
        StoryUiTransactionToken? reverse = null;
        // The game's mission observers run consumer code inside the native acceptance; the button is
        // pressed from there.
        world.World.DuringAccept = () => reverse = transactions.BeginAbandon(identifier);
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);

        Assert.Null(reverse);
        // The acceptance completed normally and nothing of the refused route happened.
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var recorded));
        Assert.Equal(StoryOccurrenceState.Active, recorded.State);
        Assert.True(world.World.IsInstalled(identifier));
        // And the boundary is free again afterwards, so the button works once nothing else is running.
        Assert.NotNull(transactions.BeginAbandon(identifier));
    }

    /// <summary>
    /// A teardown queued while the boundary is open removes an identifier by NAME later. Registering
    /// the same local ID meanwhile would hand that queued removal a brand new entry to delete, so
    /// registration is refused for as long as the boundary is open, and works again after it closes.
    /// </summary>
    [Fact]
    public void RegisteringIsRefusedWhileTheBoundaryIsOpenSoAQueuedRemovalCannotDeleteAFreshEntry()
    {
        var host = new FakeHost();
        var world = new FakeWorld();
        using var service = world.Service(host);
        world.StartAndRestore();
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var provider = service.AcquireProvider(plugin).Provider!;
        var registration = provider.Register(Definition(retention: StoryRetention.Campaign)).Definition!;
        var baseIdentifier = StoryContentPolicy.Identifier(new StoryContentId(provider.ProviderId, "salvage-run"));
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var transactions = (IStoryUiTransaction)service;
        var token = transactions.BeginAbandon(FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId));
        Assert.NotNull(token);

        // A teardown during the open boundary queues the base identifier's removal.
        registration.Dispose();
        Assert.True(world.World.IsInstalled(baseIdentifier));
        // Re-registering the same local ID now would be deleted by that queued removal, so it is
        // refused rather than accepted into a race.
        var refused = provider.Register(Definition(retention: StoryRetention.Campaign));
        Assert.Equal(StoryRegistrationStatus.Unavailable, refused.Status);
        Assert.Contains("operation is running", refused.Diagnostic);

        transactions.EndAbandon(token!, StoryAbandonSettlement.OriginalStillHeld);
        // The queued removal has now happened, and a fresh registration installs its own entry.
        Assert.False(world.World.IsInstalled(baseIdentifier));
        var again = provider.Register(Definition(retention: StoryRetention.Campaign));
        Assert.True(again.Succeeded);
        Assert.True(world.World.IsInstalled(baseIdentifier));
        Assert.True(again.Definition!.InternalActive());
    }

    /// <summary>
    /// A transaction belongs to the session that opened it. After a reload — the same save, the same
    /// occurrence identity, a new session — the old finalizer's token is no longer current: it
    /// settles nothing, and it cannot close or drain a transaction the new session opened.
    /// </summary>
    [Fact]
    public void ATransactionFromAReplacedSessionSettlesNothingInTheNewOne()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        var occurrence = provider.Offer("salvage-run");
        Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var identifier = FakeWorld.Native(provider, "salvage-run", occurrence.OccurrenceId);
        world.Missions.Publish(MissionTransitionKind.Failed, identifier);
        var transactions = (IStoryUiTransaction)service;
        var stale = transactions.BeginAbandon(identifier);
        Assert.NotNull(stale);
        var bytes = world.Persistence.Provider!.Capture();

        // The same save is reloaded while that transaction is open: same occurrence, new session.
        world.StartAndRestore(bytes);
        Assert.False(transactions.IsTransactionCurrent(stale!));
        var fresh = transactions.BeginAbandon(identifier);
        Assert.NotNull(fresh);
        Assert.NotEqual(stale!.Id, fresh!.Id);
        Assert.NotEqual(stale.SessionId, fresh.SessionId);

        // The old finalizer arrives late: it settles nothing and does not close the new transaction.
        transactions.EndAbandon(stale, StoryAbandonSettlement.NoneHeld);
        Assert.True(transactions.IsTransactionCurrent(fresh));
        Assert.Empty(provider.Occurrences("salvage-run").Records);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var untouched));
        Assert.Equal(StoryOccurrenceState.Active, untouched.State);
        Assert.True(untouched.FailureObserved);
        Assert.Null(service.FaultReason);

        // The new transaction's own settlement still works normally.
        transactions.EndAbandon(fresh, StoryAbandonSettlement.OneReplacementHeld);
        Assert.False(transactions.IsTransactionCurrent(fresh));
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var cleared));
        Assert.False(cleared.FailureObserved);
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
        Assert.True(provider.DeclareChoices(done.OccurrenceId, new Dictionary<string, string> { ["branch"] = "left" }).Accepted);
        Assert.True(provider.Activate(done.OccurrenceId).Accepted);
        world.CompleteInGame(provider, "salvage-run", done.OccurrenceId);
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
        Assert.True(provider.Activate(early.OccurrenceId).Accepted);
        world.CompleteInGame(provider, "salvage-run", early.OccurrenceId);
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
        newer[4] = StoryStateCodec.SchemaVersion + 1;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdRestoreUsesSavedGeneratedDefinitionRatherThanStartupReplacement(bool active)
    {
        var provider = Provider(out var original, out _, out _, StoryRetention.Campaign);
        var offered = provider.Offer("salvage-run");
        if (active) Assert.True(provider.Activate(offered.OccurrenceId).Accepted);
        var saved = original.Persistence.Provider!.Capture();
        var later = new FakeWorld();
        var host = new FakeHost();
        using var service = later.Service(host);
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var current = service.AcquireProvider(plugin).Provider!;
        Assert.True(current.Register(new StoryMissionDefinition("salvage-run", "Different generated pitch", "Not the saved payload", Faction,
            new[] { new StoryStep("Wrong destination", new[] { StoryObjective.TravelTo("missing-new-target") }) },
            new[] { StoryReward.Credits(999) }, retention: StoryRetention.Campaign)).Succeeded);
        var native = FakeWorld.Native(current, "salvage-run", offered.OccurrenceId);
        if (active) later.World.AdoptInWorld(native);
        later.StartAndRestore(saved);
        var restored = later.World.InstalledDefinition(native);
        Assert.Equal("Salvage run", restored.Title);
        Assert.Equal("poi-guid-1", restored.Steps[0].Objectives[0].TargetPoiId);
        Assert.Equal(500, restored.Rewards[0].Amount);
        Assert.Equal(new[] { "branch" }, restored.ChoiceKeys);
        Assert.Equal(saved, later.Persistence.Provider!.Capture());
        if (!active) Assert.True(current.Activate(offered.OccurrenceId).Accepted);
        Assert.True(current.DeclareChoices(offered.OccurrenceId, new Dictionary<string, string> { ["branch"] = "saved-choice" }).Accepted);
        later.CompleteInGame(current, "salvage-run", offered.OccurrenceId);
        var completed = Assert.Single(StoryStateCodec.Decode(later.Persistence.Provider!.Capture()));
        Assert.Null(completed.RetainedDefinition);
        Assert.Equal("saved-choice", completed.Choices["branch"]);
    }

    [Fact]
    public void NativeProgressQueriesResolveAfterReloadWithoutWritingRetainedState()
    {
        var provider = Provider(out var world, out _, out _, StoryRetention.Campaign);
        Assert.True(provider.Register(new StoryMissionDefinition("observed", "Observe", "Description", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("Credits", new[] { StoryObjective.CollectCredits(100).WithKey("balance") }) })).Succeeded);
        var offered = provider.Offer("observed");
        var objective = new StoryObjectiveId(new StoryContentId(provider.ProviderId, "observed"), offered.OccurrenceId, "balance");
        var query = (StoryContentService.Lease)provider;
        Assert.Equal(StoryKnowledge.Unavailable, query.Query(world.SessionId, objective).Knowledge);
        Assert.True(provider.Activate(offered.OccurrenceId).Accepted);
        world.World.ObservedObjectiveProgress = 20;
        var before = world.Persistence.Provider!.Capture();
        Assert.Equal(20, query.Query(world.SessionId, objective).Progress);
        Assert.Equal(before, world.Persistence.Provider!.Capture());
        var session = world.SessionId;
        world.StartAndRestore(before);
        world.World.ObservedObjectiveProgress = 10;
        Assert.Equal(StoryKnowledge.Unavailable, query.Query(session, objective).Knowledge);
        Assert.Equal(10, query.Query(world.SessionId, objective).Progress);
        Assert.False(query.SetProgress(world.SessionId, objective, 100).Accepted);
        world.Persistence.StateReady = false;
        Assert.Null(query.Query(world.SessionId, objective).Progress);
    }

    [Fact]
    public void AuthoredDestinationsGateOfferingAndReportLossAsTypedStateNotRefusal()
    {
        var provider = Provider(out var world, out _, out _, StoryRetention.Campaign);
        Assert.True(provider.Register(new StoryMissionDefinition("heed", "Heed the coordinates", "Description", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("Enter the pocket", new[]
            {
                StoryObjective.TravelToPocketSystemEntrance("margin-pocket", "act2", requireNewVisit: true).WithKey("enter")
            }) })).Succeeded);
        // While the provider's authored occurrence does not exist in the loaded game, the mission is
        // refused at the offering edge - never a mission holding an unreachable step.
        Assert.False(provider.Offer("heed").Accepted);
        string? gate = "gate-poi-7";
        world.AuthoredDestinations = (owner, objective) =>
            owner == AnimaPlugin && objective.Kind == StoryObjectiveKind.TravelToPocketSystemEntrance
            && objective.LocalId == "margin-pocket" && objective.OccurrenceKey == "act2" ? gate : null;
        var offered = provider.Offer("heed");
        Assert.True(offered.Accepted);
        Assert.True(provider.Activate(offered.OccurrenceId).Accepted);
        var lease = (StoryContentService.Lease)provider;
        var objectiveId = new StoryObjectiveId(new StoryContentId(provider.ProviderId, "heed"), offered.OccurrenceId, "enter");
        world.World.ObservedObjectiveProgress = 0;
        Assert.Equal(0, lease.Query(world.SessionId, objectiveId).Progress);
        Assert.False(lease.Query(world.SessionId, objectiveId).DestinationLost);
        // The world loses the pocket AFTER the mission is built: reported as typed state, decided by
        // nobody but the owner. Knowledge stays Known - this is world truth, not an unavailable read.
        world.World.ObservedDestinationLost = true;
        var lost = lease.Query(world.SessionId, objectiveId);
        Assert.Equal(StoryKnowledge.Known, lost.Knowledge);
        Assert.True(lost.DestinationLost);
        Assert.Null(lost.Progress);
        // The service resolves the world adapter's destinations through the provider's own identity.
        var identifier = FakeWorld.Native(provider, "heed", offered.OccurrenceId);
        var expected = StoryObjective.TravelToPocketSystemEntrance("margin-pocket", "act2");
        Assert.Equal("gate-poi-7", ServiceOf(provider).ResolveContentDestination(identifier, expected));
        gate = null;
        Assert.Null(ServiceOf(provider).ResolveContentDestination(identifier, expected));
        Assert.Null(ServiceOf(provider).ResolveContentDestination("vgmodapi.story.someone.else.00000000000000000000000000000000", expected));
    }
    private static StoryContentService ServiceOf(IStoryProvider provider) => (StoryContentService)typeof(StoryContentService.Lease)
        .GetField("_service", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .GetValue(provider)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedSourceCannotLoseUnkeyedRequirementsDuringRevisionMigration(bool active)
    {
        var provider = Provider(out var world, out _, out _, StoryRetention.Campaign);
        Assert.True(provider.Register(new StoryMissionDefinition("mixed", "Mixed", "Description", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("Both", new[] { StoryObjective.Scripted("talk", "Talk"), StoryObjective.TravelTo("poi-guid-1") }) })).Succeeded);
        var occurrence = provider.Offer("mixed");
        if (active) Assert.True(provider.Activate(occurrence.OccurrenceId).Accepted);
        var bytes = world.Persistence.Provider!.Capture();
        var later = new FakeWorld();
        var host = new FakeHost();
        using var service = later.Service(host);
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var current = service.AcquireProvider(plugin).Provider!;
        Assert.True(current.Register(new StoryMissionDefinition("mixed", "Mixed", "Description", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("Talk only", new[] { StoryObjective.Scripted("talk", "Talk") }) }).WithRevision(2, 1)).Succeeded);
        if (active) later.World.AdoptInWorld(FakeWorld.Native(current, "mixed", occurrence.OccurrenceId));
        later.StartAndRestore(bytes);
        Assert.True(service.Ledger.TryGet(occurrence.OccurrenceId, out var entry));
        Assert.Equal(1, entry.ObjectiveLayout.Revision);
        Assert.False(entry.ObjectiveLayout.FullyScripted);
        var identity = new StoryObjectiveId(entry.Id, entry.OccurrenceId, "talk");
        Assert.False(((StoryContentService.Lease)current).SetProgress(later.SessionId, identity, 1).Accepted);
        Assert.Equal(bytes, later.Persistence.Provider!.Capture());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataChangingMigrationRequiresAnOfferedOccurrence(bool active)
    {
        var provider = Provider(out var world, out _, out _, StoryRetention.Campaign);
        StoryMissionDefinition DefinitionFor(bool next) => new StoryMissionDefinition("conversation", next ? "New title" : "Old title", "Description", Faction,
            next ? new[] { new StoryStep("B", new[] { StoryObjective.Scripted("b", "B") }), new StoryStep("A", new[] { StoryObjective.Scripted("a", "A") }) }
                : new[] { new StoryStep("A", new[] { StoryObjective.Scripted("a", "A") }), new StoryStep("B", new[] { StoryObjective.Scripted("b", "B") }) },
            new[] { StoryReward.Credits(next ? 200 : 100) });
        Assert.True(provider.Register(DefinitionFor(false)).Succeeded);
        var offered = provider.Offer("conversation");
        if (active) Assert.True(provider.Activate(offered.OccurrenceId).Accepted);
        var saved = world.Persistence.Provider!.Capture();
        var later = new FakeWorld();
        var host = new FakeHost();
        using var service = later.Service(host);
        var plugin = new object(); host.Register(plugin, AnimaPlugin);
        var current = service.AcquireProvider(plugin).Provider!;
        Assert.True(current.Register(DefinitionFor(true).WithRevision(2, 1)).Succeeded);
        var identifier = FakeWorld.Native(current, "conversation", offered.OccurrenceId);
        if (active) later.World.AdoptInWorld(identifier);
        later.StartAndRestore(saved);
        Assert.True(service.Ledger.TryGet(offered.OccurrenceId, out var entry));
        Assert.Equal(active ? 1 : 2, entry.ObjectiveLayout.Revision);
        Assert.Equal(active ? "Old title" : "New title", entry.RetainedDefinition!.Title);
        if (active)
        {
            Assert.Equal(saved, later.Persistence.Provider!.Capture());
            Assert.False(later.World.IsInstalled(identifier));
        }
        else Assert.Equal(200, later.World.InstalledDefinition(identifier).Rewards[0].Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevisionMigrationAndRollbackUseAutomaticOccurrenceRestore(bool active)
    {
        var provider = Provider(out var world, out _, out _, StoryRetention.Campaign);
        StoryMissionDefinition DefinitionFor(bool reverse) => new("conversation", "Conversation", "Description", new StoryFactionId("TradingGuild"),
            reverse ? new[] { new StoryStep("Report", new[] { StoryObjective.Scripted("report", "Report") }),
                new StoryStep("Talk", new[] { StoryObjective.Scripted("talk", "Talk", 5) }) }
                : new[] { new StoryStep("Talk", new[] { StoryObjective.Scripted("talk", "Talk", 5) }),
                new StoryStep("Report", new[] { StoryObjective.Scripted("report", "Report") }) });
        Assert.True(provider.Register(DefinitionFor(false)).Succeeded);
        var offered = provider.Offer("conversation");
        var objective = new StoryObjectiveId(new StoryContentId(provider.ProviderId, "conversation"), offered.OccurrenceId, "talk");
        if (active)
        {
            Assert.True(provider.Activate(offered.OccurrenceId).Accepted);
            Assert.True(((StoryContentService.Lease)provider).SetProgress(world.SessionId, objective, 2).Accepted);
        }
        var older = world.Persistence.Provider!.Capture();
        var later = new FakeWorld();
        var host = new FakeHost();
        using var service = later.Service(host);
        var plugin = new object();
        host.Register(plugin, AnimaPlugin);
        var currentProvider = service.AcquireProvider(plugin).Provider!;
        Assert.True(currentProvider.Register(DefinitionFor(true).WithRevision(2, 1)).Succeeded);
        if (active) later.World.AdoptInWorld(FakeWorld.Native(currentProvider, "conversation", offered.OccurrenceId));
        later.StartAndRestore(older);
        Assert.True(service.Ledger.TryGet(offered.OccurrenceId, out var restored));
        Assert.Equal(2, restored.ObjectiveLayout.Revision);
        Assert.True(restored.ObjectiveLayout.TryResolve("talk", out var talk));
        Assert.Equal(1, talk.Step);
        Assert.Equal(active ? 2 : 0, talk.Progress);
        if (!active) Assert.True(currentProvider.Activate(offered.OccurrenceId).Accepted);
        Assert.True(((StoryContentService.Lease)currentProvider).SetProgress(later.SessionId, objective, 5).Accepted);
        var upgraded = later.Persistence.Provider!.Capture();
        later.StartAndRestore(upgraded);
        Assert.True(service.Ledger.TryGet(offered.OccurrenceId, out var complete));
        Assert.Equal(5, complete.ObjectiveLayout.Slots.Single(slot => slot.Key == "talk").Progress);
        later.StartAndRestore(older);
        Assert.True(service.Ledger.TryGet(offered.OccurrenceId, out var rollback));
        Assert.Equal(active ? 2 : 0, rollback.ObjectiveLayout.Slots.Single(slot => slot.Key == "talk").Progress);
        Assert.Equal(2, rollback.ObjectiveLayout.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScriptedProgressDoesNotCommitAfterSessionOrLeaseInvalidation(bool disposeLease)
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        Assert.True(provider.Register(new StoryMissionDefinition("conversation", "Conversation", "Description", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("Talk", new[] { StoryObjective.Scripted("answer", "Talk", 5) }) })).Succeeded);
        var offered = provider.Offer("conversation");
        Assert.True(provider.Activate(offered.OccurrenceId).Accepted);
        var objective = new StoryObjectiveId(new StoryContentId(provider.ProviderId, "conversation"), offered.OccurrenceId, "answer");
        var before = world.Persistence.Provider!.Capture();
        Assert.True(service.Ledger.TryGet(offered.OccurrenceId, out var original));
        world.World.DuringObjectiveWrite = () =>
        {
            if (disposeLease) provider.Dispose();
            else world.StartAndRestore(before);
        };
        Assert.False(((StoryContentService.Lease)provider).SetProgress(world.SessionId, objective, 2).Accepted);
        Assert.Equal(0, world.World.ObjectiveWrites);
        Assert.Equal(0, Assert.Single(original.ObjectiveLayout.Slots).Progress);
        Assert.True(service.Ledger.TryGet(offered.OccurrenceId, out var current));
        Assert.Equal(0, Assert.Single(current.ObjectiveLayout.Slots).Progress);
    }

    [Fact]
    public void ScriptedProgressUsesAuthenticatedSessionAndAutomaticOwnerCapture()
    {
        var provider = Provider(out var world, out _, out var service, StoryRetention.Campaign);
        Assert.True(provider.Register(new StoryMissionDefinition("conversation", "Conversation", "Description", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("Talk", new[] { StoryObjective.Scripted("answer", "Talk to the broker", 5) }) })).Succeeded);
        var offered = provider.Offer("conversation");
        var objective = new StoryObjectiveId(new StoryContentId(provider.ProviderId, "conversation"), offered.OccurrenceId, "answer");
        var objectives = (StoryContentService.Lease)provider;
        Assert.False(objectives.SetProgress(world.SessionId, objective, 2).Accepted);
        Assert.True(provider.Activate(offered.OccurrenceId).Accepted);
        Assert.True(objectives.SetProgress(world.SessionId, objective, 2).Accepted);
        var older = world.Persistence.Provider!.Capture();
        Assert.True(objectives.SetProgress(world.SessionId, objective, 5).Accepted);
        var oldSession = world.SessionId;
        world.StartAndRestore(older);
        int writes = world.World.ObjectiveWrites;
        Assert.False(objectives.SetProgress(oldSession, objective, 5).Accepted);
        Assert.Equal(writes, world.World.ObjectiveWrites);
        Assert.True(service.Ledger.TryGet(offered.OccurrenceId, out var entry));
        Assert.Equal(2, Assert.Single(entry.ObjectiveLayout.Slots).Progress);
        Assert.Equal(StoryKnowledge.Unavailable, objectives.Query(oldSession, objective).Knowledge);
        Assert.Equal(StoryKnowledge.Known, objectives.Query(world.SessionId, objective).Knowledge);
        Assert.Equal(2, objectives.Query(world.SessionId, objective).Progress);
        world.Persistence.MutationsPaused = true;
        Assert.Equal(StoryKnowledge.Known, objectives.Query(world.SessionId, objective).Knowledge);
        world.Persistence.MutationsPaused = false;
        world.Persistence.StateReady = false;
        Assert.Equal(StoryKnowledge.Unavailable, objectives.Query(world.SessionId, objective).Knowledge);
        Assert.Null(objectives.Query(world.SessionId, objective).Progress);
        world.Persistence.StateReady = true;
        world.ProtectionHealthy = false;
        Assert.Equal(StoryKnowledge.Unavailable, objectives.Query(world.SessionId, objective).Knowledge);
        Assert.Null(objectives.Query(world.SessionId, objective).Required);
        world.ProtectionHealthy = true;
        Assert.True(objectives.SetProgress(world.SessionId, objective, 2).Accepted);
        Assert.Equal(2, Assert.Single(entry.ObjectiveLayout.Slots).Progress);
        var foreign = new StoryObjectiveId(new StoryContentId("foreign", "conversation"), offered.OccurrenceId, "answer");
        Assert.False(objectives.SetProgress(world.SessionId, foreign, 5).Accepted);
        Assert.True(objectives.SetProgress(world.SessionId, objective, 5).Accepted);
        world.CompleteInGame(provider, "conversation", offered.OccurrenceId);
        var completed = world.Persistence.Provider!.Capture();
        world.StartAndRestore(completed);
        Assert.Equal(StoryOutcome.Completed, objectives.Query(world.SessionId, objective).Outcome);
        Assert.Equal(5, objectives.Query(world.SessionId, objective).Progress);
    }

    private sealed class FakeWorld
    {
        internal readonly FakePersistence Persistence;
        internal FakeWorld() => Persistence = new FakePersistence();
        internal readonly FakeLifecycle Lifecycle = new();
        internal Guid SessionId => Lifecycle.CurrentSession?.Id ?? Guid.Empty;

        internal readonly FakeStoryWorld World = new();
        internal readonly FakeMissionEvents Missions = new();
        internal readonly List<string> Reports = new();
        /// <summary>Stands in for the native guards' live readiness, exactly as the plugin wires it.</summary>
        internal bool ProtectionHealthy = true;
        /// <summary>What a guard does when a scan fails: it stops deciding and withdraws its admissions.</summary>
        internal void DegradeProtection(string reason)
        {
            ProtectionHealthy = false;
            Protection.WithdrawAll("the guard could not decide: " + reason);
        }
        internal readonly StoryProtection Protection = new();
        private readonly LifecycleHub _healthHub = new((_, _) => { });
        internal Func<string, StoryObjective, string?>? AuthoredDestinations;
        internal StoryContentService Service(FakeHost host, Action? checkThread = null, Func<string, string, bool?>? worldReferences = null)
        {
            _healthHub.SetCapability("owned-story", true, "Test bindings.");
            return new(_healthHub.Services, Persistence, Lifecycle, host.Authenticate, null, checkThread, World, Missions,
                (detail, available) => Reports.Add((available ? "available: " : "unavailable: ") + detail), Protection,
                () => ProtectionHealthy, worldReferences, (owner, objective) => AuthoredDestinations?.Invoke(owner, objective));
        }

        /// <summary>The identifier one occurrence is installed under, exactly as the module derives it.</summary>
        internal static string Native(IStoryProvider provider, string localId, Guid occurrenceId)
            => StoryContentPolicy.OccurrenceIdentifier(new StoryContentId(provider.ProviderId, localId), occurrenceId);

        /// <summary>The GAME completes an owned mission: it ends there and the observer reports it.</summary>
        internal void CompleteInGame(IStoryProvider provider, string localId, Guid occurrenceId)
        {
            var identifier = Native(provider, localId, occurrenceId);
            World.CompleteInWorld(identifier);
            Missions.Publish(MissionTransitionKind.Completed, identifier);
        }

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
            Persistence.StateReady = false;
        }
    }

    private sealed class FakeLifecycle : ILifecycleService
    {
        public SessionSnapshot? CurrentSession { get; private set; }
        public IServiceStatus SessionTracking { get; } = new FakeServiceStatus();
        public IServiceStatus SaveOutcomes { get; } = new FakeServiceStatus();
        public bool IsDispatchingCallbacks { get; private set; }
        public event Action<LifecycleEvent>? Changed;
        internal void Set(SessionSnapshot snapshot) => CurrentSession = snapshot;
        internal void Publish(LifecycleEventKind kind)
        {
            IsDispatchingCallbacks = true;
            try { Changed?.Invoke(new LifecycleEvent(kind, CurrentSession)); }
            finally { IsDispatchingCallbacks = false; }
        }
    }

    /// <summary>Where a hostile collection re-enters the API while a retirement is in flight.</summary>
    public enum ReentrancyPoint { GetEnumerator, MoveNext, Dispose }

    /// <summary>Well-formed choices that run caller code at one point of the enumeration.</summary>
    private sealed class ReentrantChoices : IReadOnlyDictionary<string, string>
    {
        private readonly ReentrancyPoint _point;
        private readonly Action _reenter;
        private readonly Dictionary<string, string> _pairs;
        internal bool Ran { get; private set; }
        internal ReentrantChoices(ReentrancyPoint point, Action reenter, Dictionary<string, string> pairs)
        { _point = point; _reenter = reenter; _pairs = pairs; }

        private void Reenter(ReentrancyPoint point)
        {
            if (point != _point || Ran) return;
            Ran = true;
            _reenter();
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            Reenter(ReentrancyPoint.GetEnumerator);
            return new Enumerator(this, _pairs.GetEnumerator());
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public int Count => _pairs.Count;
        public IEnumerable<string> Keys => _pairs.Keys;
        public IEnumerable<string> Values => _pairs.Values;
        public bool ContainsKey(string key) => _pairs.ContainsKey(key);
        public bool TryGetValue(string key, out string value) => _pairs.TryGetValue(key, out value!);
        public string this[string key] => _pairs[key];

        private sealed class Enumerator : IEnumerator<KeyValuePair<string, string>>
        {
            private readonly ReentrantChoices _owner;
            private Dictionary<string, string>.Enumerator _inner;
            internal Enumerator(ReentrantChoices owner, Dictionary<string, string>.Enumerator inner)
            { _owner = owner; _inner = inner; }
            public KeyValuePair<string, string> Current => _inner.Current;
            object System.Collections.IEnumerator.Current => Current;
            public bool MoveNext() { _owner.Reenter(ReentrancyPoint.MoveNext); return _inner.MoveNext(); }
            public void Reset() { }
            public void Dispose() { _owner.Reenter(ReentrancyPoint.Dispose); _inner.Dispose(); }
        }
    }

    /// <summary>Yields one set of pairs on the first enumeration and another on every later one.</summary>
    private sealed class ShiftingChoices : IReadOnlyDictionary<string, string>
    {
        private readonly Dictionary<string, string> _first, _later;
        private readonly int? _reportedCount;
        internal int Reads { get; private set; }
        internal ShiftingChoices(Dictionary<string, string> first, Dictionary<string, string> later, int? reportedCount = null)
        { _first = first; _later = later; _reportedCount = reportedCount; }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => (Reads++ == 0 ? _first : _later).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public int Count => _reportedCount ?? _first.Count;
        public IEnumerable<string> Keys => (Reads++ == 0 ? _first : _later).Keys;
        public IEnumerable<string> Values => (Reads++ == 0 ? _first : _later).Values;
        public bool ContainsKey(string key) => _later.ContainsKey(key);
        public bool TryGetValue(string key, out string value) => _later.TryGetValue(key, out value!);
        public string this[string key] => _later[key];
    }

    /// <summary>A collection whose Count, Keys and Values must never be trusted by the module.</summary>
    private abstract class HostileChoices : IReadOnlyDictionary<string, string>
    {
        public abstract IEnumerator<KeyValuePair<string, string>> GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public int Count => 1;
        public IEnumerable<string> Keys => throw new InvalidOperationException("Keys must not be trusted.");
        public IEnumerable<string> Values => throw new InvalidOperationException("Values must not be trusted.");
        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out string value) { value = ""; return false; }
        public string this[string key] => throw new KeyNotFoundException();
    }

    private sealed class ThrowingChoices : HostileChoices
    {
        private readonly Exception _error;
        private readonly bool _onDispose;
        private ThrowingChoices(Exception error, bool onDispose) { _error = error; _onDispose = onDispose; }
        internal static ThrowingChoices OnMoveNext(Exception error) => new(error, false);
        internal static ThrowingChoices OnDispose(Exception error) => new(error, true);
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() => new Enumerator(_error, _onDispose);

        private sealed class Enumerator : IEnumerator<KeyValuePair<string, string>>
        {
            private readonly Exception _error;
            private readonly bool _onDispose;
            private bool _yielded;
            internal Enumerator(Exception error, bool onDispose) { _error = error; _onDispose = onDispose; }
            public KeyValuePair<string, string> Current => new("branch", "left");
            object System.Collections.IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (!_onDispose) throw _error;
                if (_yielded) return false;
                _yielded = true;
                return true;
            }
            public void Reset() { }
            public void Dispose() { if (_onDispose) throw _error; }
        }
    }

    /// <summary>Never ends; the module must stop after a bounded number of items.</summary>
    private sealed class EndlessChoices : HostileChoices
    {
        internal int Yielded { get; private set; }
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Sequence();
        private IEnumerator<KeyValuePair<string, string>> Sequence()
        {
            for (int index = 0; ; index++)
            {
                Yielded++;
                yield return new KeyValuePair<string, string>("k" + index, "v");
            }
        }
    }

    private sealed class DuplicateKeyChoices : HostileChoices
    {
        private readonly string _key, _first, _second;
        internal DuplicateKeyChoices(string key, string first, string second) { _key = key; _first = first; _second = second; }
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Sequence();
        private IEnumerator<KeyValuePair<string, string>> Sequence()
        {
            yield return new KeyValuePair<string, string>(_key, _first);
            yield return new KeyValuePair<string, string>(_key, _second);
        }
    }

    private sealed class NullEntryChoices : HostileChoices
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Sequence();
        private static IEnumerator<KeyValuePair<string, string>> Sequence()
        {
            yield return new KeyValuePair<string, string>("branch", null!);
        }
    }

    /// <summary>The mission observation boundary every consumer sees, driven explicitly by the tests.</summary>
    private sealed class FakeMissionEvents : FakeServiceStatus, IMissionService
    {
        private long _sequence;
        public event Action<MissionTransition>? Transitioned;
        internal int Subscribers => Transitioned?.GetInvocationList().Length ?? 0;
        public IServiceStatus IdentityContinuity { get; } = new FakeServiceStatus();
        public bool TryGetNative(MissionSnapshot snapshot, out object? native) { native = null; return false; }
        internal void Publish(MissionTransitionKind kind, string? definitionId)
        {
            var snapshot = new MissionSnapshot(Guid.NewGuid(), Guid.NewGuid(), definitionId, "mission",
                Array.Empty<string>(), acceptanceObserved: true);
            Transitioned?.Invoke(new MissionTransition(kind, snapshot, ++_sequence));
        }
    }

    /// <summary>
    /// Stands in for the game's story catalog and the player's mission list, with the same rules the
    /// native adapter verifies: a duplicate identifier is never replaced, a duplicate story mission is
    /// refused, and a completion is the WORLD's to make.
    /// </summary>
    private sealed class FakeStoryWorld : IStoryWorld, IStoryObjectiveWorld, IStoryObjectiveObservationWorld
    {
        private readonly Dictionary<string, StoryMissionDefinition> _installed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _factions = new(StringComparer.Ordinal) { "TradingGuild", "MiningGuild" };
        private readonly HashSet<string> _pointsOfInterest = new(StringComparer.Ordinal) { "poi-guid-1" };
        internal bool GalaxyLoaded = true;
        internal void ForgetPointOfInterest(string guid) => _pointsOfInterest.Remove(guid);
        internal void RestorePointOfInterest(string guid) => _pointsOfInterest.Add(guid);
        public bool? KnowsPointOfInterest(string guid)
            => !GalaxyLoaded || Unavailable ? null : _pointsOfInterest.Contains(guid);
        internal readonly HashSet<string> ItemTypes = new(StringComparer.Ordinal) { "UmbralMetafiber" };
        internal readonly HashSet<string> DeliveryStations = new(StringComparer.Ordinal) { "poi-guid-1" };
        public bool? KnowsItemType(string itemTypeId) => Unavailable ? null : ItemTypes.Contains(itemTypeId);
        public bool? KnowsDeliveryTarget(string guid)
            => !GalaxyLoaded || Unavailable ? null : _pointsOfInterest.Contains(guid) && DeliveryStations.Contains(guid);
        private readonly HashSet<string> _foreign = new(StringComparer.Ordinal);
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);
        private readonly HashSet<string> _archived = new(StringComparer.Ordinal);
        internal bool Unavailable;
        internal bool RefuseAccept;
        internal int Installs, Uninstalls, Accepts, Releases;

        /// <summary>Content the world already holds, which this API must never replace.</summary>
        internal void AddForeign(string identifier) => _foreign.Add(identifier);
        /// <summary>The world's own outcome: the mission ends and its story identifier is archived.</summary>
        internal void CompleteInWorld(string identifier) { _active.Remove(identifier); _archived.Add(identifier); }
        /// <summary>A mission the world holds for one of our identifiers without this API admitting it.</summary>
        internal void AdoptInWorld(string identifier) => _active.Add(identifier);
        internal void ClearWorld() { _active.Clear(); _archived.Clear(); }
        internal bool IsInstalled(string identifier) => _installed.ContainsKey(identifier);
        internal StoryMissionDefinition InstalledDefinition(string identifier) => _installed[identifier];
        internal bool IsActive(string identifier) => _active.Contains(identifier);

        public StoryWorldResult MigrateScripted(string identifier, StoryMissionDefinition definition, StoryObjectiveLayout source,
            StoryObjectiveLayout destination, Func<bool> stillValid)
            => _active.Contains(identifier) && stillValid() ? StoryWorldResult.Ok : new StoryWorldResult(StoryWorldStatus.Refused, "unavailable");
        internal int? ObservedObjectiveProgress;
        internal bool ObservedDestinationLost = false;
        public StoryObjectiveReading? ReadProgress(string identifier, StoryObjectiveLayout.Slot slot, StoryObjective expected, Func<bool> stillValid)
            => !_active.Contains(identifier) || !stillValid() ? null
                : ObservedDestinationLost ? StoryObjectiveReading.Lost
                : ObservedObjectiveProgress is { } value ? StoryObjectiveReading.Of(value) : null;
        internal int ObjectiveWrites;
        internal Action? DuringObjectiveWrite;
        public StoryWorldResult SetScriptedProgress(string identifier, StoryObjectiveLayout.Slot slot, int progress, Func<bool>? stillValid = null)
        {
            if (!_active.Contains(identifier) || Unavailable) return new StoryWorldResult(StoryWorldStatus.Unavailable, "no live occurrence");
            DuringObjectiveWrite?.Invoke();
            if (stillValid?.Invoke() == false) return new StoryWorldResult(StoryWorldStatus.Refused, "invalidated");
            ObjectiveWrites++;
            return StoryWorldResult.Ok;
        }

        internal bool RefuseRollback;
        internal int Rollbacks;
        internal void ForgetFaction(string factionId) => _factions.Remove(factionId);
        public bool KnowsFaction(string factionId) => !Unavailable && _factions.Contains(factionId);
        public IReadOnlyCollection<string> InstalledIdentifiers() => _installed.Keys.Concat(_foreign).ToArray();

        public StoryWorldResult RollbackAccept(string identifier)
        {
            Rollbacks++;
            if (RefuseRollback) return new StoryWorldResult(StoryWorldStatus.Refused, "the world still held the mission after the rollback");
            _active.Remove(identifier);
            return StoryWorldResult.Ok;
        }

        internal Action? DuringInstall;
        public StoryWorldResult Install(string identifier, StoryMissionDefinition definition)
        {
            Installs++;
            var callback = DuringInstall; DuringInstall = null; callback?.Invoke();
            if (Unavailable) return new StoryWorldResult(StoryWorldStatus.Unavailable, "no world");
            if (_foreign.Contains(identifier)) return new StoryWorldResult(StoryWorldStatus.AlreadyPresent, "already in the catalog");
            _installed[identifier] = definition;
            return StoryWorldResult.Ok;
        }

        public bool Uninstall(string identifier) { Uninstalls++; return _installed.Remove(identifier); }

        /// <summary>Consumer code the game's own mission observers would run INSIDE the native call.</summary>
        internal Action? DuringAccept, DuringRelease;

        public StoryWorldResult Accept(string identifier)
        {
            Accepts++;
            var during = DuringAccept; DuringAccept = null; during?.Invoke();
            if (Unavailable) return new StoryWorldResult(StoryWorldStatus.Unavailable, "no current player");
            if (RefuseAccept) return new StoryWorldResult(StoryWorldStatus.Refused, "the world did not hold the accepted mission afterwards");
            if (!_installed.ContainsKey(identifier)) return new StoryWorldResult(StoryWorldStatus.Refused, "not installed by this API");
            if (_active.Contains(identifier) || _archived.Contains(identifier))
                return new StoryWorldResult(StoryWorldStatus.AlreadyPresent, "already active or archived");
            _active.Add(identifier);
            return StoryWorldResult.Ok;
        }

        public StoryWorldResult Release(string identifier, StoryOutcome outcome)
        {
            Releases++;
            var during = DuringRelease; DuringRelease = null; during?.Invoke();
            if (Unavailable) return new StoryWorldResult(StoryWorldStatus.Unavailable, "no current player");
            if (!_installed.ContainsKey(identifier)) return new StoryWorldResult(StoryWorldStatus.Refused, "not installed by this API");
            if (!_active.Contains(identifier)) return StoryWorldResult.Ok;
            if (outcome == StoryOutcome.Completed)
                return new StoryWorldResult(StoryWorldStatus.Refused, "the world still holds this mission");
            _active.Remove(identifier);
            return StoryWorldResult.Ok;
        }

        public StoryWorldSnapshot? Snapshot()
            => Unavailable ? null : new StoryWorldSnapshot(InstalledIdentifiers(), _active.ToArray(), _archived.ToArray());
    }

    private class FakePersistence : TestSaveDataService
    {
        internal PersistenceProvider? Provider;
        /// <summary>The owner's restored state is readable. False models blocked, unreadable or restore-failed data.</summary>
        internal bool StateReady = true;
        /// <summary>Transient: callbacks dispatching or a save in flight. Reading stays safe, mutating does not.</summary>
        internal bool MutationsPaused;
        internal bool OwnerDisposed;
        public override SaveDataRegistrationResult Register(PersistenceProvider provider)
        {
            Assert.Null(Provider);
            Provider = provider;
            return new(SaveDataRegistrationStatus.Registered, this);
        }
        public override bool CanMutate => !OwnerDisposed && StateReady && !MutationsPaused;
        public override bool CanRead => !OwnerDisposed && StateReady;
        public override void Dispose() => OwnerDisposed = true;
    }

}
