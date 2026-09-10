using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed partial class StoryContentTests
{
    [Fact]
    public void StoryDefinitionCompletionCarriesItsGameAndCanOfferTheNextMission()
    {
        using var f = new StoryObjectsFixture();
        using var intro = f.Register(); using var next = f.Register("next");
        f.Start();
        var mission = f.Game.Story.Offer(intro);
        Assert.Equal(StoryMissionState.Offering, mission.State);
        var activation = mission.Activate();
        Assert.Equal(StoryActionStatus.Queued, activation.Status);
        f.Tick();
        Assert.Equal(StoryActionStatus.Succeeded, activation.Status);
        Assert.Equal(StoryMissionState.Active, mission.State);
        IStoryMission? followup = null;
        intro.Completed += _ => throw new InvalidOperationException("isolated subscriber");
        intro.Completed += completed =>
        {
            Assert.Same(mission, completed); Assert.Same(f.Game, completed.Game);
            Assert.False(f.Hub.IsDispatchingCallbacks);
            followup = completed.Game.Story.Offer(next);
        };
        f.Complete(mission);
        Assert.Null(followup); Assert.Equal(StoryMissionState.Completed, mission.State);
        f.Tick();
        Assert.NotNull(followup); Assert.Equal(StoryMissionState.Offering, followup.State);
        f.Tick();
        Assert.Equal(StoryMissionState.Offered, followup.State);
        Assert.Equal(StoryActionStatus.Succeeded, followup.LastAction.Status);
        Assert.Single(f.Errors);
    }

    [Fact]
    public void AcceptedMissionCanSetItsScriptedObjectiveWithoutTokensOrAFrameDriver()
    {
        using var f = new StoryObjectsFixture();
        using var definition = f.Provider.Register(new StoryMissionDefinition("conversation", "Talk", "Talk", Faction,
            new[] { new StoryStep("Talk", new[] { StoryObjective.Scripted("answer", "Answer", 5) }) })).Definition!;
        StoryActionResult? progress = null;
        definition.Accepted += mission => progress = mission.GetObjective("answer").SetProgress(4);
        f.Start(); var mission = f.Game.Story.Offer(definition); mission.Activate(); f.Tick();
        Assert.Null(progress); f.Tick(); Assert.NotNull(progress);
        Assert.Equal(StoryActionStatus.Queued, progress.Status); Assert.Equal(0, f.World.ObjectiveWrites);
        f.Tick(); Assert.Equal(StoryActionStatus.Succeeded, progress.Status);
        Assert.Equal(1, f.World.ObjectiveWrites);
        Assert.Equal(4, mission.GetObjective("answer").Snapshot.Progress);
    }

    [Fact]
    public void RefusedStoryAcceptanceLeavesTheOfferAndDoesNotTriggerAcceptanceReactions()
    {
        using var f = new StoryObjectsFixture(); using var definition = f.Register(); f.Start();
        var accepted = 0; definition.Accepted += _ => accepted++;
        f.World.RefuseAccept = true;
        var mission = f.Game.Story.Offer(definition); var action = mission.Activate();
        f.Tick(); f.Tick();
        Assert.Equal(StoryActionStatus.Rejected, action.Status); Assert.NotEmpty(action.Detail);
        Assert.Equal(StoryMissionState.Offered, mission.State); Assert.Equal(0, accepted);
        var progress = mission.GetObjective("unknown").SetProgress(1); f.Tick();
        Assert.Equal(StoryActionStatus.Rejected, progress.Status); Assert.Equal(0, f.World.ObjectiveWrites);
    }

    [Fact]
    public void StoryActionsRetainTheirOwnResultsWhenSeveralActionsAreQueued()
    {
        using var f = new StoryObjectsFixture(); using var definition = f.Register(); f.Start();
        var mission = f.Game.Story.Offer(definition);
        var activate = mission.Activate(); var withdraw = mission.Withdraw();
        f.Tick();
        Assert.Equal(StoryActionStatus.Succeeded, activate.Status);
        Assert.Equal(StoryActionStatus.Rejected, withdraw.Status);
        Assert.Equal(StoryMissionState.Active, mission.State);
        Assert.Same(withdraw, mission.LastAction);
    }

    [Fact]
    public void StoryOfferWaitsForSaveAndItsOwnedPersistenceWithoutConsumerRetries()
    {
        using var f = new StoryObjectsFixture(); using var definition = f.Register(); f.Start();
        var save = Guid.NewGuid();
        f.Hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, f.Hub.CurrentSession, save, "slot"));
        var mission = f.Game.Story.Offer(definition); var installs = f.World.Installs;
        f.Tick(); Assert.Equal(installs, f.World.Installs);
        f.Hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSkipped, f.Hub.CurrentSession, save, "slot"));
        f.Persistence.Paused = true; f.Tick(); Assert.Equal(installs, f.World.Installs);
        f.Persistence.Paused = false; f.Tick();
        Assert.Equal(StoryMissionState.Offered, mission.State);
        Assert.Equal(StoryActionStatus.Succeeded, mission.LastAction.Status);
    }

    [Fact]
    public void QueuedStoryActionsEndImmediatelyWithTheirCapturedGame()
    {
        using var f = new StoryObjectsFixture(); using var definition = f.Register(); f.Start();
        var game = f.Game; var mission = game.Story.Offer(definition); var activation = mission.Activate();
        var offer = mission.LastAction;
        f.Start();
        Assert.Equal(StoryMissionState.GameEnded, mission.State);
        Assert.Equal(StoryActionStatus.GameEnded, activation.Status);
        Assert.Equal(StoryActionStatus.GameEnded, offer.Status);
        Assert.Equal(StoryActionStatus.GameEnded, mission.GetObjective("answer").SetProgress(1).Status);
        var installs = f.World.Installs; f.Tick(); Assert.Equal(installs, f.World.Installs);
        Assert.Empty(f.Game.Story.GetMissions(definition).Missions);
    }

    [Fact]
    public void RemovingDefinitionHandlersOrDisposingTheDefinitionSuppressesPendingDelivery()
    {
        using var f = new StoryObjectsFixture(); var definition = f.Register(); f.Start();
        var calls = 0; Action<IStoryMission> callback = _ => calls++;
        definition.Accepted += callback;
        var mission = f.Game.Story.Offer(definition); mission.Activate(); f.Tick();
        definition.Accepted -= callback; f.Tick(); Assert.Equal(0, calls);
        definition.Completed += callback; f.Complete(mission); definition.Dispose(); f.Tick(); Assert.Equal(0, calls);
        Assert.Equal(StoryMissionState.Unavailable, mission.State);
        Assert.Equal(StoryActionStatus.Unavailable, mission.Activate().Status);
    }

    [Fact]
    public void DisposingAStoryDefinitionCancelsAnOfferWithoutLeavingAQueuedResult()
    {
        using var f = new StoryObjectsFixture(); var definition = f.Register(); f.Start();
        var mission = f.Game.Story.Offer(definition); var result = mission.LastAction;
        definition.Dispose();
        Assert.Equal(StoryMissionState.Unavailable, mission.State);
        Assert.Equal(StoryActionStatus.Unavailable, result.Status);
        var installs = f.World.Installs; f.Tick(); Assert.Equal(installs, f.World.Installs);
    }

    [Fact]
    public void ReloadBuildsFreshMissionObjectsWithoutReplayingDefinitionEvents()
    {
        using var f = new StoryObjectsFixture(); using var definition = f.Register(); f.Start();
        var accepted = 0; definition.Accepted += _ => accepted++;
        var previous = f.Game.Story.Offer(definition); previous.Activate(); f.Tick(); f.Tick(); Assert.Equal(1, accepted);
        var bytes = f.Persistence.Provider!.Capture();
        f.Start(bytes); f.Tick();
        var current = Assert.Single(f.Game.Story.GetMissions(definition).Missions);
        Assert.NotSame(previous, current); Assert.Equal(previous.Id, current.Id); Assert.Same(f.Game, current.Game);
        Assert.Equal(StoryMissionState.Active, current.State); Assert.Equal(1, accepted);
        Assert.Equal(StoryActionStatus.GameEnded, previous.Abandon().Status);
        Assert.Equal(StoryMissionState.Active, current.State);
    }

    [Fact]
    public void StoryRestoringAfterGameInitializationStillBuildsObjectsWithoutReplayingAcceptance()
    {
        using var f = new StoryObjectsFixture(); using var definition = f.Register(); f.Start();
        var mission = f.Game.Story.Offer(definition); mission.Activate(); f.Tick();
        var bytes = f.Persistence.Provider!.Capture();
        var id = f.Hub.Begin(SessionOrigin.SaveLoad, "slot"); f.Hub.PlayerReady(id); f.Hub.GameplayInitialized(id);
        Assert.False(f.Game.Story.GetMissions(definition).IsAvailable);
        var accepted = 0; definition.Accepted += _ => accepted++;
        f.Persistence.Provider.Restore(f.Hub.CurrentSession!, bytes); f.Tick();
        var restored = Assert.Single(f.Game.Story.GetMissions(definition).Missions);
        Assert.Equal(mission.Id, restored.Id); Assert.Same(f.Game, restored.Game);
        Assert.Equal(StoryMissionState.Active, restored.State); Assert.Equal(0, accepted);
    }

    [Fact]
    public void ReplacementRegistrationDoesNotRebindOldMissionObjects()
    {
        using var f = new StoryObjectsFixture(); var oldDefinition = f.Register(); f.Start();
        var oldMission = f.Game.Story.Offer(oldDefinition); f.Tick(); oldDefinition.Dispose();
        using var replacement = f.Register();
        var current = Assert.Single(f.Game.Story.GetMissions(replacement).Missions);
        Assert.NotSame(oldMission, current); Assert.Equal(oldMission.Id, current.Id);
        Assert.Equal(StoryMissionState.Unavailable, oldMission.State);
        Assert.Equal(StoryActionStatus.Unavailable, oldMission.Activate().Status);
        current.Activate(); f.Tick(); Assert.Equal(StoryMissionState.Active, current.State);
    }

    [Fact]
    public void StoryProviderDeclaresCustomSaveDependencyOnceForActionsAndEvents()
    {
        using var f = new StoryObjectsFixture(); using var definition = f.Register(); f.Start();
        f.CustomData.Paused = true;
        var mission = f.Game.Story.Offer(definition); var activate = mission.Activate(); f.Tick();
        Assert.Equal(StoryMissionState.Offering, mission.State); Assert.Equal(0, f.World.Accepts);
        f.CustomData.Paused = false; f.Tick(); Assert.Equal(StoryActionStatus.Succeeded, activate.Status);
        var completed = 0; definition.Completed += _ => completed++;
        f.CustomData.Paused = true; f.Complete(mission); f.Tick(); Assert.Equal(0, completed);
        f.CustomData.Paused = false; f.Tick(); Assert.Equal(1, completed);
        var pending = f.Game.Story.Offer(definition); var result = pending.LastAction;
        f.CustomData.Dispose(); Assert.Equal(StoryActionStatus.Unavailable, result.Status);
        Assert.Equal(StoryMissionState.Unavailable, pending.State); f.Tick();
    }

    [Fact]
    public void StoryChoicesAreBoundedFrozenAtSubmissionAndRetainedOnTheMission()
    {
        using var f = new StoryObjectsFixture();
        using var definition = f.Provider.Register(Definition(retention: StoryRetention.Campaign)).Definition!;
        f.Start(); var mission = f.Game.Story.Offer(definition); mission.Activate(); f.Tick();
        Assert.Equal(StoryActionStatus.Rejected, mission.DeclareChoices(new EndlessChoices()).Status);
        var choices = new Dictionary<string, string> { ["branch"] = "saved" };
        var action = mission.DeclareChoices(choices); choices["branch"] = "changed"; f.Tick();
        Assert.Equal(StoryActionStatus.Succeeded, action.Status); Assert.Equal("saved", mission.Choices["branch"]);
        f.Complete(mission); Assert.Equal("saved", mission.Choices["branch"]);
        f.Start(f.Persistence.Provider!.Capture());
        Assert.Equal("saved", Assert.Single(f.Game.Story.GetMissions(definition).Missions).Choices["branch"]);
    }

    [Fact]
    public void StoryQueriesDistinguishUnavailableFromAnEmptyGame()
    {
        using var f = new StoryObjectsFixture(); using var definition = f.Register(); f.Start();
        var oldGame = f.Game;
        var query = oldGame.Story.GetMissions(definition); Assert.True(query.IsAvailable); Assert.Empty(query.Missions);
        f.Start(); Assert.False(oldGame.Story.GetMissions(definition).IsAvailable);
    }

    [Fact]
    public void StoryPublicAuthoringSurfaceHasNoSessionMutationOrObjectiveProviderProtocol()
    {
        var assembly = typeof(IStory).Assembly;
        Assert.Null(assembly.GetType("VGModAPI.IStoryObjectiveProvider"));
        Assert.Null(assembly.GetType("VGModAPI.IStoryRegistration"));
        Assert.False(assembly.GetType("VGModAPI.StoryTransitionStatus")!.IsPublic);
        Assert.Null(typeof(IStoryProvider).GetMethod("Offer")); Assert.Null(typeof(IStoryProvider).GetMethod("Unresolved"));
        foreach (var type in new[] { typeof(IStory), typeof(IStoryMission), typeof(IStoryObjective), typeof(IStoryDefinition) })
            foreach (var parameter in type.GetMethods().SelectMany(method => method.GetParameters()))
                Assert.NotEqual(typeof(Guid), parameter.ParameterType);
    }

    private sealed class StoryObjectsFixture : IDisposable
    {
        internal readonly List<Exception> Errors = new();
        internal readonly LifecycleHub Hub;
        internal readonly FakeStoryWorld World = new();
        internal readonly FakeMissionEvents Missions = new();
        internal readonly StoryObjectPersistence Persistence;
        internal readonly StoryObjectPersistence CustomData;
        internal readonly StoryContentService Engine;
        internal readonly IStoryProvider Provider;
        internal readonly GameService Games;
        internal IGame Game => Games.Current!;
        internal StoryObjectsFixture()
        {
            Hub = new LifecycleHub((_, error) => Errors.Add(error));
            foreach (var capability in new[] { "owned-story", "session-lifecycle", "save-outcomes" }) Hub.SetCapability(capability, true, "test");
            Persistence = new(Hub); CustomData = new(Hub); var host = new FakeHost(); var plugin = new object(); host.Register(plugin, AnimaPlugin);
            Engine = new StoryContentService(Hub.Services, Persistence, Hub, host.Authenticate, checkThread: Hub.CheckThread, world: World, missions: Missions);
            Provider = Engine.AcquireProvider(plugin, saveData: CustomData).Provider!;
            Games = new GameService(Hub, new NavigationService(Hub, _ => null, (_, _, _) => NavigationStatus.Unavailable, (_, _) => null), new InventoryService(Hub, () => null), Engine, new BarContentService(null, Hub, (_, _) => null, _ => false, Hub.CheckThread));
        }
        internal IStoryDefinition Register(string local = "salvage-run") => Provider.Register(Definition(local)).Definition!;
        internal void Start(byte[]? bytes = null)
        {
            var id = Hub.Begin(bytes == null ? SessionOrigin.NewGame : SessionOrigin.SaveLoad, "slot");
            if (bytes == null) World.ClearWorld();
            Hub.PlayerReady(id); Persistence.Provider!.Restore(Hub.CurrentSession!, bytes); Hub.GameplayInitialized(id);
        }
        internal void Tick() => Hub.Gameplay.Tick();
        internal void Complete(IStoryMission mission)
        {
            var identifier = StoryContentPolicy.OccurrenceIdentifier(mission.Definition.Id, mission.Id);
            World.CompleteInWorld(identifier); Missions.Publish(MissionTransitionKind.Completed, identifier);
        }
        public void Dispose() { Provider.Dispose(); Games.Dispose(); Engine.Dispose(); Hub.Dispose(); }
    }

    private sealed class StoryObjectPersistence : FakeServiceStatus, ISaveDataService, ISaveDataRegistration
    {
        private readonly LifecycleHub _hub;
        internal PersistenceProvider? Provider;
        internal bool Paused;
        private bool _disposed;
        internal StoryObjectPersistence(LifecycleHub hub) { _hub = hub; }
        public bool CanRead => !_disposed;
        public bool CanMutate => CanRead && !Paused;
        public SaveDataState State => new(_disposed ? SaveDataStateKind.Disposed : SaveDataStateKind.Ready, _disposed ? null : _hub.CurrentSession?.Id);
        public event Action<SaveDataState>? StateChanged { add { } remove { } }
        public SaveDataRegistrationResult Register(PersistenceProvider provider)
        { Provider = provider; return new(SaveDataRegistrationStatus.Registered, this); }
        public void Dispose() { _disposed = true; }
    }
}
