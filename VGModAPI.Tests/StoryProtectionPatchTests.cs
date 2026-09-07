using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Source.MissionSystem;
using VGModAPI;
using VGModAPI.Core;
using VGModAPI.Patches;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Executes the ACTUAL patch entry points — the prefix and finalizer the installer attaches — rather
/// than the quarantine helpers behind them. The patch source is compiled into this assembly, so the
/// state handoff, the single settlement and the untouched exception are exercised, not described.
/// Attachment to the game's method is separately pinned by the installed-assembly tests.
/// </summary>
[Collection("story-native")]
public sealed class StoryProtectionPatchTests : IDisposable
{
    private static readonly StoryFactionId Trading = new("TradingGuild");
    private readonly Source.Player.GamePlayer _player = new();
    private readonly StoryProtection _protection = new();
    private readonly StoryQuarantine _quarantine;
    private readonly RecordingTransactions _transactions = new();

    private static readonly MethodInfo PrefixMethod = typeof(StoryProtectionPatches.AbandonMission)
        .GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo FinalizerMethod = typeof(StoryProtectionPatches.AbandonMission)
        .GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)!;

    public StoryProtectionPatchTests()
    {
        StoryMission.allMissions.Clear();
        Source.Player.GamePlayer.current = _player;
        _quarantine = new StoryQuarantine(new StoryProtectionGuard(typeof(StoryMission).Assembly), _protection,
            () => Source.Player.GamePlayer.current?.missions.Cast<object>().ToArray() ?? Array.Empty<object>())
            { Transactions = _transactions };
        StoryProtectionPatches.Quarantine = _quarantine;
    }

    public void Dispose()
    {
        StoryProtectionPatches.Quarantine = null;
        StoryMission.allMissions.Clear();
        Source.Player.GamePlayer.current = null;
    }

    /// <summary>Drives the patch exactly as Harmony would: prefix, then the original, then the finalizer.</summary>
    private bool RunPatchedAbandon(Mission mission, Action original)
    {
        var arguments = new object?[] { mission, null };
        bool proceed = (bool)PrefixMethod.Invoke(null, arguments)!;
        var state = arguments[1];
        try
        {
            if (proceed) original();
            return proceed;
        }
        finally { FinalizerMethod.Invoke(null, new[] { state }); }
    }

    private (string Identifier, Mission Mission) Hold(string local = "salvage-run")
    {
        var identifier = StoryContentPolicy.OccurrenceIdentifier(new StoryContentId("anima", local), Guid.NewGuid());
        var world = new StoryNativeWorld(new StoryNativeBindings(typeof(StoryMission).Assembly), () => { });
        var definition = new StoryMissionDefinition(local, "Salvage run", "Recover it.", Trading,
            new[] { new StoryStep("Reach the wreck", new[] { StoryObjective.TravelTo("poi-guid-1", 5) }) },
            new[] { new StoryReward(StoryRewardKind.Credits, 500) }, StoryDifficulty.Normal, StoryRetention.Campaign);
        Assert.True(world.Install(identifier, definition).Applied);
        Assert.True(world.Accept(identifier).Applied);
        return (identifier, _player.missions.Single(mission => mission.storyId == identifier));
    }

    [Fact]
    public void ThePrefixRefusesAnOrphanAndTheFinalizerSettlesNothing()
    {
        var owned = Hold();
        bool ran = false;
        Assert.False(RunPatchedAbandon(owned.Mission, () => ran = true));
        Assert.False(ran);
        Assert.Equal(0, _transactions.Ends);
        Assert.Null(_transactions.Began);
        Assert.Contains(owned.Mission, _player.missions);
    }

    [Fact]
    public void ThePrefixCarriesItsStateToTheFinalizerWhichSettlesExactlyOnce()
    {
        var owned = Hold();
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        var ui = new Behaviour.UI.Missions.MissionDetails { Retryable = true };

        Assert.True(RunPatchedAbandon(owned.Mission, () => ui.AbandonMission(owned.Mission)));
        Assert.Equal(owned.Identifier, _transactions.Began);
        Assert.Equal(1, _transactions.Ends);
        Assert.Equal(StoryAbandonSettlement.OneReplacementHeld, _transactions.Settlement);
    }

    [Fact]
    public void AnOriginalThatChangesNothingSettlesAsTheUnchangedOriginal()
    {
        var owned = Hold();
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        Assert.True(RunPatchedAbandon(owned.Mission, () => { }));
        Assert.Equal(StoryAbandonSettlement.OriginalStillHeld, _transactions.Settlement);
        Assert.Equal(1, _transactions.Ends);
    }

    /// <summary>A throwing original still settles once, and its exception reaches the caller unchanged.</summary>
    [Fact]
    public void AThrowingOriginalStillSettlesOnceAndItsExceptionIsNotSwallowed()
    {
        var owned = Hold();
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        var thrown = new InvalidOperationException("the game threw");
        var caught = Assert.Throws<InvalidOperationException>(() => RunPatchedAbandon(owned.Mission, () =>
        {
            _player.missions.Remove(owned.Mission);
            throw thrown;
        }));
        Assert.Same(thrown, caught);
        Assert.Equal(1, _transactions.Ends);
        Assert.Equal(StoryAbandonSettlement.NoneHeld, _transactions.Settlement);
    }

    /// <summary>A settlement that itself faults neither escapes the finalizer nor loses the original's exception.</summary>
    [Fact]
    public void ASettlementFaultDoesNotEscapeTheFinalizerOrHideTheOriginal()
    {
        var owned = Hold();
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        _transactions.EndFault = new InvalidOperationException("the module faulted");
        var thrown = new InvalidOperationException("the game threw");
        var caught = Assert.Throws<InvalidOperationException>(() => RunPatchedAbandon(owned.Mission, () => throw thrown));
        Assert.Same(thrown, caught);
        Assert.Equal(1, _transactions.Ends);

        // And with a normal original, a faulting settlement does not surface as a game exception.
        _transactions.EndFault = new InvalidOperationException("the module faulted again");
        Assert.True(RunPatchedAbandon(owned.Mission, () => { }));
        Assert.Equal(2, _transactions.Ends);
    }

    /// <summary>A module that refuses the transaction refuses the whole route; nothing is settled.</summary>
    [Fact]
    public void AModuleThatRefusesTheTransactionRefusesTheRoute()
    {
        var owned = Hold();
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        _transactions.Refuse = true;
        bool ran = false;
        Assert.False(RunPatchedAbandon(owned.Mission, () => ran = true));
        Assert.False(ran);
        Assert.Equal(0, _transactions.Ends);
    }

    /// <summary>A stale session withdraws every admission, so the same button is refused again.</summary>
    [Fact]
    public void AStaleSessionMakesTheSameButtonRefuseAgain()
    {
        var owned = Hold();
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        Assert.True(RunPatchedAbandon(owned.Mission, () => { }));
        _protection.WithdrawAll("a new session started");
        bool ran = false;
        Assert.False(RunPatchedAbandon(owned.Mission, () => ran = true));
        Assert.False(ran);
        Assert.Equal(1, _transactions.Ends);
    }

    /// <summary>Content that is not ours passes straight through the patch, with no state and no settlement.</summary>
    [Fact]
    public void ContentThatIsNotOursPassesThroughThePatchUntouched()
    {
        var vanilla = new Mission { storyId = "tutorial_11", sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") };
        _player.missions.Add(vanilla);
        bool ran = false;
        Assert.True(RunPatchedAbandon(vanilla, () => ran = true));
        Assert.True(ran);
        Assert.Equal(0, _transactions.Ends);
        Assert.Null(_transactions.Began);
    }

    private sealed class RecordingTransactions : IStoryUiTransaction
    {
        internal string? Began;
        internal int Ends;
        internal bool Refuse;
        internal Exception? EndFault;
        internal StoryAbandonSettlement Settlement = StoryAbandonSettlement.UnknownOrAmbiguous;
        public bool BeginAbandon(string identifier) { Began = identifier; return !Refuse; }
        public void EndAbandon(string identifier, StoryAbandonSettlement settlement)
        {
            Ends++;
            Settlement = settlement;
            if (EndFault != null) throw EndFault;
        }
    }
}
