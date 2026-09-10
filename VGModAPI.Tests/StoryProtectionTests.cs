using System;
using System.Collections.Generic;
using System.Linq;
using Source.MissionSystem;
using VGModAPI;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// The quarantine boundary, exercised on the production guard against reflection doubles shaped like
/// the installed assembly. A save keeps accepted missions as full objects, so an API-owned mission
/// comes back whether or not the module that owns it is present. These tests pin what happens then:
/// it does not advance, it does not pay out, it is not retried — and it is not touched either, so the
/// player's save keeps exactly what it had.
/// </summary>
[Collection("story-native")]
public sealed class StoryProtectionTests : IDisposable
{
    private static readonly StoryFactionId Trading = new("TradingGuild");
    private readonly Source.Player.GamePlayer _player = new();
    private readonly StoryProtection _protection = new();
    private readonly StoryQuarantine _quarantine;

    public StoryProtectionTests()
    {
        StoryMission.allMissions.Clear();
        Source.Player.GamePlayer.current = _player;
        _quarantine = new StoryQuarantine(new StoryProtectionGuard(typeof(StoryMission).Assembly), _protection,
            // Read through the CURRENT player, exactly as the installer wires it.
            () => Source.Player.GamePlayer.current?.missions.Cast<object>().ToArray() ?? Array.Empty<object>());
    }

    public void Dispose()
    {
        StoryMission.allMissions.Clear();
        Source.Player.GamePlayer.current = null;
    }

    private static StoryMissionDefinition Definition() => new("salvage-run", "Salvage run", "Recover it.", Trading,
        new[] { new StoryStep("Reach the wreck", new[] { StoryObjective.TravelTo("poi-guid-1", requireNewVisit: true) }) },
        new[] { StoryReward.Credits(500) }, StoryDifficulty.Normal, StoryRetention.Campaign);

    private (string Identifier, Mission Mission) Hold(string local = "salvage-run")
    {
        var identifier = StoryContentPolicy.OccurrenceIdentifier(new StoryContentId("anima", local), Guid.NewGuid());
        var world = new StoryNativeWorld(new StoryNativeBindings(typeof(StoryMission).Assembly), () => { });
        Assert.True(world.Install(identifier, Definition()).Applied);
        Assert.True(world.Accept(identifier).Applied);
        return (identifier, _player.missions.Single(mission => mission.storyId == identifier));
    }

    /// <summary>With nobody vouching for it, an owned mission is quarantined; other content never is.</summary>
    [Fact]
    public void OwnedMissionsAreQuarantinedByDefaultAndNothingElseIsEverTouched()
    {
        var owned = Hold();
        var vanilla = new Mission { storyId = "tutorial_11", sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") };
        var unnamed = new Mission { sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") };
        _player.missions.Add(vanilla);
        _player.missions.Add(unnamed);

        Assert.True(_quarantine.Blocks(owned.Mission));
        // Vanilla story content and missions with no identifier are not ours to judge.
        Assert.False(_quarantine.Blocks(vanilla));
        Assert.False(_quarantine.Blocks(unnamed));
        Assert.False(_quarantine.Blocks(null));
        Assert.Contains(owned.Identifier, _protection.Quarantined);
        Assert.DoesNotContain("tutorial_11", _protection.Quarantined);
    }

    /// <summary>
    /// Only the exact identifiers the owning module vouches for, in the session it vouched for them,
    /// are allowed through; withdrawing admission puts the mission straight back under quarantine.
    /// </summary>
    [Fact]
    public void AdmissionIsExactAndWithdrawnAdmissionQuarantinesAgain()
    {
        var owned = Hold();
        var other = Hold("side-run");
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        Assert.False(_quarantine.Blocks(owned.Mission));
        Assert.True(_quarantine.Blocks(other.Mission));

        _protection.WithdrawAll("the owning module suspended itself");
        Assert.True(_quarantine.Blocks(owned.Mission));
        Assert.Equal("the owning module suspended itself", _protection.State);
        // A session with no identity admits nothing, whatever it is handed.
        _protection.Admit(Guid.Empty, new[] { owned.Identifier }, "no session");
        Assert.True(_quarantine.Blocks(owned.Mission));
    }

    /// <summary>
    /// Objectives are recognised by identity, so a trigger dispatched to every held mission's
    /// objectives advances the ones that may run and not the ones that may not.
    /// </summary>
    [Fact]
    public void ObjectivesOfQuarantinedMissionsAreRecognisedAndOthersAreLeftAlone()
    {
        var quarantined = Hold();
        var admitted = Hold("side-run");
        _protection.Admit(Guid.NewGuid(), new[] { admitted.Identifier }, "admitted");
        var vanilla = new Source.MissionSystem.Objectives.CollectCredits();

        foreach (var objective in quarantined.Mission.steps.SelectMany(step => step.objectives))
            Assert.True(_quarantine.BlocksObjective(objective));
        foreach (var objective in admitted.Mission.steps.SelectMany(step => step.objectives))
            Assert.False(_quarantine.BlocksObjective(objective));
        Assert.False(_quarantine.BlocksObjective(vanilla));
        Assert.False(_quarantine.BlocksObjective(null));

        // Admission changes are picked up: the same objects flip when the module vouches for them.
        _protection.Admit(Guid.NewGuid(), new[] { quarantined.Identifier, admitted.Identifier }, "admitted");
        foreach (var objective in quarantined.Mission.steps.SelectMany(step => step.objectives))
            Assert.False(_quarantine.BlocksObjective(objective));
    }

    /// <summary>
    /// Quarantine changes nothing about the mission: the object stays in the player's list and
    /// serializes byte-for-byte as it was loaded, so a provider that returns later finds it intact.
    /// </summary>
    [Fact]
    public void QuarantineLeavesTheMissionAndWhatItWouldSaveExactlyAsTheyWere()
    {
        var owned = Hold();
        // Seeded away from every default, including the flags a guard could plausibly disturb, so the
        // comparison below is evidence rather than a comparison of empty values. This is a HOST
        // PROJECTION of the persisted shape; running the game's own serializer is PR3 work.
        owned.Mission.failed = true;
        owned.Mission.canAbandon = false;
        owned.Mission.trackedOnHud = true;
        owned.Mission.canBeIdled = true;
        owned.Mission.idle = true;
        owned.Mission.autoComplete = true;
        owned.Mission.nextMissionOnFailed = null;                    // an owned mission never carries one
        owned.Mission.turnIn = new Source.Galaxy.MapPointOfInterest { guid = "poi-turn-in" };
        owned.Mission.sourceName = "Trading Guild";
        owned.Mission.iconName = "icon";
        foreach (var step in owned.Mission.steps) step.hidden = true;
        var before = owned.Mission.SerializeLikeTheGame();
        Assert.Contains("failed:True", before);
        Assert.Contains("hidden=True", before);
        Assert.Contains("autoComplete:True", before);
        var missionCount = _player.missions.Count;
        var archived = _player.missionsArchive.ToArray();

        for (int index = 0; index < 3; index++)
        {
            Assert.True(_quarantine.Blocks(owned.Mission));
            foreach (var objective in owned.Mission.steps.SelectMany(step => step.objectives))
                Assert.True(_quarantine.BlocksObjective(objective));
        }

        Assert.Equal(before, owned.Mission.SerializeLikeTheGame());
        Assert.Equal(missionCount, _player.missions.Count);
        Assert.Equal(archived, _player.missionsArchive);
        Assert.Contains(owned.Mission, _player.missions);
        // The seeded values are still exactly what they were: nothing was cleared or flipped.
        Assert.True(owned.Mission.failed);
        Assert.All(owned.Mission.steps, step => Assert.True(step.hidden));
    }

    /// <summary>
    /// The button the player actually presses removes the mission and re-adds the same identifier.
    /// For an orphan nobody vouches for, that whole operation is refused: the mission stays in the
    /// player's list exactly as the save had it, and the catalog is never asked for an entry that may
    /// no longer be there.
    /// </summary>
    [Fact]
    public void TheAbandonAndRetryButtonIsRefusedOutrightForAnOrphan()
    {
        var owned = Hold();
        owned.Mission.failed = true;
        StoryMission.allMissions.Remove(owned.Identifier);        // the orphan's entry is gone
        var before = owned.Mission.SerializeLikeTheGame();
        var ui = new Behaviour.UI.Missions.MissionDetails { Retryable = true };

        Assert.False(_quarantine.AllowAbandon(owned.Mission, out var state));
        Assert.Null(state);
        // The guard refuses before the game runs, so nothing of the route executes.
        Assert.Contains(owned.Mission, _player.missions);
        Assert.Equal(before, owned.Mission.SerializeLikeTheGame());
        // Running the route without the guard is what the refusal prevents: it throws on the catalog.
        Assert.ThrowsAny<Exception>(() => ui.AbandonMission(owned.Mission));
    }

    /// <summary>
    /// A mission carrying a follow-up identifier would make the button install something this module
    /// never admitted, so an owned mission with one is refused even when it is admitted.
    /// </summary>
    [Fact]
    public void AnOwnedMissionWithAFollowUpIdentifierIsRefusedRatherThanRedirected()
    {
        var owned = Hold();
        owned.Mission.nextMissionOnFailed = "vgmodapi.story.anima.other." + Guid.NewGuid().ToString("N");
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        Assert.False(_quarantine.AllowAbandon(owned.Mission, out _));
        Assert.Contains(owned.Mission, _player.missions);
    }

    /// <summary>
    /// An admitted mission's abandon/retry is handed to the owning module, and what the game turned
    /// out to be holding decides what it MEANT: the game builds a NEW object for a retry, so the
    /// original still being there is not a retry at all, and none being there is an ending.
    /// </summary>
    [Fact]
    public void SettlementDistinguishesARetryFromAnUnchangedOriginalAndAnEnding()
    {
        var owned = Hold();
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        var transactions = new RecordingTransactions();
        _quarantine.Transactions = transactions;
        var ui = new Behaviour.UI.Missions.MissionDetails { Retryable = true };

        Assert.True(_quarantine.AllowAbandon(owned.Mission, out var state));
        Assert.Equal(owned.Identifier, state!.Identifier);
        ui.AbandonMission(owned.Mission);                       // remove, then re-add the same id
        _quarantine.EndAbandon(state);
        Assert.Equal(StoryAbandonSettlement.OneReplacementHeld, transactions.Settlement);
        Assert.Equal(1, transactions.Ends);

        // The button pressed but nothing actually removed: NOT a retry.
        var unchanged = _player.missions.Single(mission => mission.storyId == owned.Identifier);
        Assert.True(_quarantine.AllowAbandon(unchanged, out var untouched));
        _quarantine.EndAbandon(untouched!);
        Assert.Equal(StoryAbandonSettlement.OriginalStillHeld, transactions.Settlement);

        // A non-retryable abandon ends it.
        var plain = new Behaviour.UI.Missions.MissionDetails { Retryable = false };
        Assert.True(_quarantine.AllowAbandon(unchanged, out var ending));
        plain.AbandonMission(unchanged);
        _quarantine.EndAbandon(ending!);
        Assert.Equal(StoryAbandonSettlement.NoneHeld, transactions.Settlement);

        // Content that is not ours never reaches the module at all.
        var vanilla = new Mission { storyId = "tutorial_11", sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") };
        _player.missions.Add(vanilla);
        transactions.Began = null;
        Assert.True(_quarantine.AllowAbandon(vanilla, out var none));
        Assert.Null(none);
        Assert.Null(transactions.Began);
    }

    /// <summary>
    /// The game removes BY REFERENCE and re-adds with the duplicate check forced off, so a stale
    /// object carrying an admitted identifier would remove nothing and add a SECOND live mission for
    /// it. Only an object the player actually holds, held exactly once, may take the button's route.
    /// </summary>
    [Fact]
    public void OnlyAHeldObjectWithAnUnambiguousIdentifierMayTakeTheButtonsRoute()
    {
        var owned = Hold();
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        var transactions = new RecordingTransactions();
        _quarantine.Transactions = transactions;

        // A stale object from an earlier load of the same save: same identifier, different object.
        var stale = new Mission { storyId = owned.Identifier, sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") };
        Assert.False(_quarantine.AllowAbandon(stale, out var refused));
        Assert.Null(refused);
        Assert.Null(transactions.Began);
        Assert.Contains(owned.Mission, _player.missions);

        // The same object against a DIFFERENT current player is not held either.
        var otherPlayer = new Source.Player.GamePlayer();
        Source.Player.GamePlayer.current = otherPlayer;
        Assert.False(_quarantine.AllowAbandon(owned.Mission, out _));
        Source.Player.GamePlayer.current = _player;

        // Two live missions for one identifier is ambiguous: neither is authorised.
        var duplicate = new Mission { storyId = owned.Identifier, sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") };
        _player.missions.Add(duplicate);
        Assert.False(_quarantine.AllowAbandon(owned.Mission, out _));
        Assert.False(_quarantine.AllowAbandon(duplicate, out _));
        Assert.Null(transactions.Began);
    }

    /// <summary>
    /// A world that cannot be read after the button ran is UNKNOWN, never "it is gone": inventing an
    /// ending would retire an occurrence and drop its catalog entry over a world nobody could read.
    /// </summary>
    [Fact]
    public void AnUnreadableOrAmbiguousWorldAfterTheButtonSettlesAsUnknown()
    {
        var owned = Hold();
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        var transactions = new RecordingTransactions();
        var reports = new List<string>();
        bool fail = false;
        var quarantine = new StoryQuarantine(new StoryProtectionGuard(typeof(StoryMission).Assembly), _protection,
            () => fail ? throw new InvalidOperationException("the player could not be read") : _player.missions.Cast<object>().ToArray(),
            null, reports.Add)
        { Transactions = transactions };

        // Unreadable after the removal.
        Assert.True(quarantine.AllowAbandon(owned.Mission, out var state));
        _player.missions.Remove(owned.Mission);
        fail = true;
        quarantine.EndAbandon(state!);
        Assert.Equal(StoryAbandonSettlement.UnknownOrAmbiguous, transactions.Settlement);
        Assert.NotNull(quarantine.DegradedReason);
        Assert.Single(reports);

        // Degrading withdrew the admissions, as a guard that cannot decide must; a healthy scan and a
        // fresh admission are what let the route run again.
        Assert.Equal(0, _protection.AdmittedCount);
        fail = false;
        Assert.True(quarantine.VerifyHealthy());
        _player.missions.Add(owned.Mission);
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        Assert.True(quarantine.AllowAbandon(owned.Mission, out var second));
        var replacement = new Mission { storyId = owned.Identifier, sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") };
        _player.missions.Remove(owned.Mission);
        _player.missions.Add(replacement);
        fail = true;
        quarantine.EndAbandon(second!);
        Assert.Equal(StoryAbandonSettlement.UnknownOrAmbiguous, transactions.Settlement);

        // Two replacements is ambiguous even when the world reads perfectly.
        fail = false;
        Assert.True(quarantine.VerifyHealthy());
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        _player.missions.Clear();
        _player.missions.Add(owned.Mission);
        Assert.True(quarantine.AllowAbandon(owned.Mission, out var third));
        _player.missions.Remove(owned.Mission);
        _player.missions.Add(replacement);
        _player.missions.Add(new Mission { storyId = owned.Identifier, sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") });
        quarantine.EndAbandon(third!);
        Assert.Equal(StoryAbandonSettlement.UnknownOrAmbiguous, transactions.Settlement);
        Assert.Equal(3, transactions.Ends);
    }

    /// <summary>
    /// Ownership is resolved against the CURRENT player every time, so a mission that appears later —
    /// a second save loaded into the same process with nothing bumping an admission — is still seen.
    /// </summary>
    [Fact]
    public void ObjectiveOwnershipIsResolvedAgainstThePlayerRatherThanACache()
    {
        var first = Hold();
        _protection.Admit(Guid.NewGuid(), new[] { first.Identifier }, "admitted");
        foreach (var objective in first.Mission.steps.SelectMany(step => step.objectives))
            Assert.False(_quarantine.BlocksObjective(objective));

        // A later save's mission, with no admission change of any kind.
        var later = Hold("side-run");
        foreach (var objective in later.Mission.steps.SelectMany(step => step.objectives))
            Assert.True(_quarantine.BlocksObjective(objective));

        // The same player, mutated in place: the newcomer is refused as well.
        var swapped = Hold("swap-run");
        _player.missions.Remove(first.Mission);
        foreach (var objective in swapped.Mission.steps.SelectMany(step => step.objectives))
            Assert.True(_quarantine.BlocksObjective(objective));
    }

    /// <summary>
    /// If ownership cannot be established the objective is refused and the protection says it is
    /// degraded: an unreadable world is not permission to let possibly owned content advance.
    /// </summary>
    [Fact]
    public void AnUnreadableWorldRefusesTheObjectiveAndReportsItRatherThanFailingOpen()
    {
        var owned = Hold();
        var reports = new List<string>();
        var failing = new StoryQuarantine(new StoryProtectionGuard(typeof(StoryMission).Assembly), _protection,
            () => throw new InvalidOperationException("the player could not be read"), null, reports.Add);
        var objective = owned.Mission.steps.SelectMany(step => step.objectives).First();

        Assert.True(failing.BlocksObjective(objective));
        Assert.NotNull(failing.DegradedReason);
        Assert.False(failing.Healthy);
        Assert.Single(reports);
        Assert.Contains("could not read", reports[0]);
        // A degraded guard is not restored by asking again while the world is still unreadable.
        Assert.False(failing.VerifyHealthy());
        Assert.NotNull(failing.DegradedReason);
        // Only a scan that actually completes restores it, which is what a later healthy world gives.
        var healthy = new StoryQuarantine(new StoryProtectionGuard(typeof(StoryMission).Assembly), _protection,
            () => _player.missions.Cast<object>().ToArray());
        Assert.True(healthy.VerifyHealthy());
        Assert.True(healthy.Healthy);
    }

    /// <summary>The scan is bounded: a player holding more than the guard can examine is refused, not skipped.</summary>
    [Fact]
    public void AnUnscannablyLargeMissionListIsRefusedRatherThanSkipped()
    {
        var owned = Hold();
        for (int index = 0; index < StoryQuarantine.MaxScannedMissions + 1; index++)
            _player.missions.Insert(0, new Mission { sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") });
        var objective = owned.Mission.steps.SelectMany(step => step.objectives).First();
        Assert.True(_quarantine.BlocksObjective(objective));
        Assert.Contains("scan", _quarantine.DegradedReason!);
    }

    /// <summary>
    /// The whole reserved namespace is ours to answer for, not only well-formed occurrence
    /// identifiers: a base definition id, a malformed one, or an occurrence nobody minted is content
    /// nobody can vouch for. A neighbouring namespace is left alone.
    /// </summary>
    [Fact]
    public void EveryIdentifierInTheReservedNamespaceIsQuarantinedUnlessItIsAdmitted()
    {
        var admitted = StoryContentPolicy.OccurrenceIdentifier(new StoryContentId("anima", "salvage-run"), Guid.NewGuid());
        _protection.Admit(Guid.NewGuid(), new[] { admitted }, "admitted");
        Assert.False(_protection.IsQuarantined(admitted));
        foreach (var identifier in new[]
        {
            StoryContentPolicy.Identifier(new StoryContentId("anima", "salvage-run")),        // the base definition
            StoryContentPolicy.IdentifierPrefix + "anima.salvage-run.not-a-guid",             // malformed
            StoryContentPolicy.OccurrenceIdentifier(new StoryContentId("anima", "salvage-run"), Guid.NewGuid()),
            StoryContentPolicy.IdentifierPrefix
        }) Assert.True(_protection.IsQuarantined(identifier), identifier);
        // Ordinal matching: a neighbouring namespace and vanilla content are not ours.
        Assert.False(_protection.IsQuarantined("vgmodapi.story-other.anima.thing"));
        Assert.False(_protection.IsQuarantined("VGMODAPI.STORY.anima.thing"));
        Assert.False(_protection.IsQuarantined("tutorial_11"));
        Assert.False(_protection.IsQuarantined(null));
    }

    /// <summary>Steps this API installs are visible; quarantine never has to hide or reveal one.</summary>
    [Fact]
    public void InstalledStepsCarryTheSupportedVisibleShape()
    {
        var owned = Hold();
        Assert.All(owned.Mission.steps, step => Assert.False(step.hidden));
        Assert.True(_quarantine.Blocks(owned.Mission));
        Assert.All(owned.Mission.steps, step => Assert.False(step.hidden));
    }

    private sealed class RecordingTransactions : IStoryUiTransaction
    {
        internal string? Began;
        internal int Ends;
        internal StoryUiTransactionToken? Open;
        internal StoryAbandonSettlement Settlement = StoryAbandonSettlement.UnknownOrAmbiguous;
        public StoryUiTransactionToken? BeginAbandon(string identifier)
        {
            Began = identifier;
            Open = new StoryUiTransactionToken(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            return Open;
        }
        public bool IsTransactionCurrent(StoryUiTransactionToken token) => ReferenceEquals(token, Open);
        public void EndAbandon(StoryUiTransactionToken token, StoryAbandonSettlement settlement)
        {
            Ends++;
            Settlement = settlement;
        }
    }

    /// <summary>
    /// The guard binds only against the shape it actually guards, and refuses to bind when an
    /// installable objective kind would slip past the one trigger method it protects.
    /// </summary>
    [Fact]
    public void TheGuardBindsAgainstTheDeclaredShapeAndCoversEveryInstallableObjective()
    {
        var guard = new StoryProtectionGuard(typeof(StoryMission).Assembly);
        var owned = Hold();
        Assert.Equal(owned.Identifier, guard.StoryId(owned.Mission));
        Assert.True(guard.IsMission(owned.Mission));
        Assert.False(guard.IsMission("not a mission"));
        Assert.Single(guard.Objectives(owned.Mission));
        Assert.ThrowsAny<Exception>(() => new StoryProtectionGuard(typeof(string).Assembly));
        // Kinds with a dedicated guard binding must declare the override that binding patches
        // (Scripted, KillEnemies, MineItems); every other installable kind must ride a patched
        // method - the base guard, or Mining's patched override in Salvage's case. The fakes model
        // the REAL assembly here; the Cecil suite pins the same map against the installed game.
        var overriding = new System.Collections.Generic.HashSet<StoryObjectiveKind>
        { StoryObjectiveKind.Scripted, StoryObjectiveKind.KillEnemies, StoryObjectiveKind.MineItems };
        foreach (StoryObjectiveKind kind in Enum.GetValues(typeof(StoryObjectiveKind)))
        {
            if (StoryContentPolicy.RefuseObjective(kind) != null) continue;
            var type = typeof(StoryMission).Assembly.GetType(
                StoryContentPolicy.ObjectiveNamespace + "." + StoryContentPolicy.ObjectiveTypeName(kind))!;
            var method = type.GetMethod("ProcessMissionTrigger",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
            if (overriding.Contains(kind)) Assert.NotNull(method);
            else Assert.Null(method);
        }
    }
}
