using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed partial class BarContentServiceTests
{
    [Fact]
    public void AutomaticPlacementWaitsForSaveReadinessAndDoesNotDuplicate()
    {
        using var f = new Objects();
        var definition = f.Provider.Register(Definition()).Definition!;
        f.Start();
        var patron = f.Games.Current!.Bars.Get(definition);
        f.Storage.MutationAllowed = false;
        f.Pump();
        Assert.Equal(BarPatronStatus.Waiting, patron.Status);
        Assert.Equal(BarStatus.Queued, patron.LastAction.Status);
        Assert.Empty(BarPatronCodec.Decode(f.Storage.Provider.Capture()));
        f.Storage.MutationAllowed = true;
        f.Pump(); f.Pump();
        Assert.Equal(BarPatronStatus.Assigned, patron.Status);
        Assert.Single(BarPatronCodec.Decode(f.Storage.Provider.Capture()));
    }

    [Fact]
    public void DisposingOldKeyedHandleDoesNotRevokeReplacementOrAnotherProvider()
    {
        using var f = new Objects();
        int oldCalls = 0, calls = 0;
        var old = f.Provider.Register(Definition(), _ => oldCalls++).Definition!;
        f.Start();
        var oldObject = f.Games.Current!.Bars.Get(old);
        var replacement = f.Provider.Register(Definition(), _ => calls++).Definition!;
        var otherProvider = f.Engine.AcquireProvider("other").Provider!;
        var other = otherProvider.Register(Definition()).Definition!;
        old.Dispose();
        f.Pump();
        Assert.Equal(BarPatronStatus.Unavailable, oldObject.Status);
        Assert.Equal(BarPatronStatus.Assigned, f.Games.Current.Bars.Get(replacement).Status);
        Assert.Equal(BarPatronStatus.Assigned, f.Games.Current.Bars.Get(other).Status);
        var plan = f.Engine.Plan(f.Session, "station")!;
        var own = Assert.Single(plan.Patrons, row => row.Id.Provider == f.Provider.ProviderId);
        Assert.True(f.Engine.Interact(plan, own));
        Assert.Equal(0, calls);
        f.Pump();
        Assert.Equal(1, calls); Assert.Equal(0, oldCalls);
    }

    [Fact]
    public void RemovalBeforePlacementIsPersistentAbsenceUntilExplicitRestore()
    {
        using var f = new Objects();
        var definition = f.Provider.Register(Definition()).Definition!;
        f.Start();
        var patron = f.Games.Current!.Bars.Get(definition);
        var removal = patron.Remove();
        Assert.Equal(BarStatus.Queued, removal.Status);
        f.Pump();
        Assert.True(removal.Succeeded);
        Assert.True(Assert.Single(BarPatronCodec.Decode(f.Storage.Provider.Capture())).Removed);
        var bytes = f.Storage.Provider.Capture();
        f.Start(bytes); f.Pump(); f.Pump();
        var restoredGamePatron = f.Games.Current!.Bars.Get(definition);
        Assert.Equal(BarPatronStatus.Removed, restoredGamePatron.Status);
        Assert.Empty(f.Engine.Plan(f.Session, "station")!.Patrons);
        var restore = restoredGamePatron.Restore(); f.Pump();
        Assert.True(restore.Succeeded);
        Assert.Equal(BarPatronStatus.Assigned, restoredGamePatron.Status);
        Assert.Single(f.Engine.Plan(f.Session, "station")!.Patrons);
    }

    [Theory]
    [InlineData(BarPatronRetention.Persistent)]
    [InlineData(BarPatronRetention.Transient)]
    public void RemoveAndRestoreHaveSeparateRetainedResults(BarPatronRetention retention)
    {
        using var f = new Objects();
        var definition = f.Provider.Register(Definition(retention)).Definition!;
        f.Start(); f.Pump();
        var patron = f.Games.Current!.Bars.Get(definition);
        var removal = patron.Remove(); f.Pump();
        Assert.Equal(BarPatronStatus.Removed, patron.Status);
        var restoration = patron.Restore(); f.Pump();
        Assert.NotSame(removal, restoration);
        Assert.True(removal.Succeeded); Assert.True(restoration.Succeeded);
        Assert.Equal(BarPatronStatus.Assigned, patron.Status);
    }

    [Fact]
    public void PendingActionsEndWithoutAnotherTickAndCannotAffectReplacementGame()
    {
        using var f = new Objects();
        var definition = f.Provider.Register(Definition()).Definition!;
        f.Start(); f.Pump();
        var old = f.Games.Current!.Bars.Get(definition);
        f.Storage.MutationAllowed = false;
        var removal = old.Remove(); f.Pump();
        f.Start();
        Assert.Equal(BarStatus.GameEnded, removal.Status);
        Assert.Equal(BarPatronStatus.GameEnded, old.Status);
        Assert.Equal(BarStatus.GameEnded, old.Restore().Status);
        f.Storage.MutationAllowed = true; f.Pump();
        Assert.Equal(BarPatronStatus.Assigned, f.Games.Current!.Bars.Get(definition).Status);
    }

    [Fact]
    public void InteractionCarriesCapturedGameAndCanRequestRemoval()
    {
        using var f = new Objects();
        IGame? delivered = null;
        BarResult? removal = null;
        var definition = f.Provider.Register(Definition(), patron => { delivered = patron.Game; removal = patron.Remove(); }).Definition!;
        f.Start(); f.Pump();
        var game = f.Games.Current!;
        var plan = f.Engine.Plan(f.Session, "station")!;
        Assert.True(f.Engine.Interact(plan, Assert.Single(plan.Patrons)));
        Assert.Null(delivered);
        f.Pump(); f.Pump();
        Assert.Same(game, delivered);
        Assert.True(removal!.Succeeded);
        Assert.Equal(BarPatronStatus.Removed, game.Bars.Get(definition).Status);
    }

    [Fact]
    public void QueuedInteractionDoesNotSurviveDefinitionReplacement()
    {
        using var f = new Objects();
        int calls = 0;
        f.Provider.Register(Definition(), _ => calls++);
        f.Start(); f.Pump();
        var plan = f.Engine.Plan(f.Session, "station")!;
        Assert.True(f.Engine.Interact(plan, Assert.Single(plan.Patrons)));
        f.Provider.Register(Definition()); f.Pump();
        Assert.Equal(0, calls);
    }

    [Fact]
    public void WaitingRetriesAreNotChangedEvents()
    {
        using var f = new Objects();
        Guid? occurrence = null;
        f.Engine.ResolveOccurrence = (_, _) => occurrence;
        var definition = f.Provider.Register(new BarPatronDefinition("contact", "station", "Name", "Description", new BarPatronPresentation("seed"),
            mission: new StoryContentId(f.Provider.ProviderId, "job"))).Definition!;
        f.Start();
        var patron = f.Games.Current!.Bars.Get(definition);
        int changes = 0;
        patron.Changed += _ => changes++;
        f.Pump(); f.Pump(); f.Pump(); f.Pump();
        Assert.Equal(BarPatronStatus.Waiting, patron.Status);
        Assert.True(changes <= 1); // Entering Waiting may publish once; per-frame retries publish nothing.
        var beforeAssign = changes;
        occurrence = Guid.NewGuid();
        f.Pump(); f.Pump(); f.Pump();
        Assert.Equal(BarPatronStatus.Assigned, patron.Status);
        Assert.Equal(beforeAssign + 1, changes);
    }

    [Fact]
    public void ProviderSavePrerequisiteGatesPlacementAndInteraction()
    {
        var prerequisite = new Storage { MutationAllowed = false };
        using var f = new Objects(prerequisite);
        int calls = 0;
        var definition = f.Provider.Register(Definition(), _ => calls++).Definition!;
        f.Start(); prerequisite.SessionId = f.Session;
        f.Pump();
        Assert.Equal(BarPatronStatus.Waiting, f.Games.Current!.Bars.Get(definition).Status);
        prerequisite.MutationAllowed = true; f.Pump();
        var plan = f.Engine.Plan(f.Session, "station")!;
        Assert.True(f.Engine.Interact(plan, Assert.Single(plan.Patrons)));
        prerequisite.MutationAllowed = false; f.Pump(); Assert.Equal(0, calls);
        prerequisite.MutationAllowed = true; f.Pump(); Assert.Equal(1, calls);
    }

    [Fact]
    public void AutomaticPlacementWaitsForTheNativeSaveTerminalEvent()
    {
        using var f = new Objects();
        var definition = f.Provider.Register(Definition()).Definition!;
        f.Start();
        var save = Guid.NewGuid();
        f.Hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, f.Hub.CurrentSession, save, "slot"));
        f.Pump();
        Assert.Equal(BarPatronStatus.Waiting, f.Games.Current!.Bars.Get(definition).Status);
        f.Hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSkipped, f.Hub.CurrentSession, save, "slot"));
        f.Pump();
        Assert.Equal(BarPatronStatus.Assigned, f.Games.Current.Bars.Get(definition).Status);
    }

    [Fact]
    public void MissionLinkedContactWaitsUntilTheCurrentOccurrenceResolves()
    {
        using var f = new Objects();
        Guid? occurrence = null;
        f.Engine.ResolveOccurrence = (_, _) => occurrence;
        var definition = f.Provider.Register(new BarPatronDefinition("contact", "station", "Name", "Description", new BarPatronPresentation("seed"),
            mission: new StoryContentId(f.Provider.ProviderId, "job"))).Definition!;
        f.Start(); f.Pump(); f.Pump();
        var patron = f.Games.Current!.Bars.Get(definition);
        Assert.Equal(BarPatronStatus.Waiting, patron.Status);
        Assert.Equal(BarStatus.MissionNotReady, patron.LastAction.Status);
        occurrence = Guid.NewGuid();
        f.Pump();
        Assert.Equal(BarPatronStatus.Assigned, patron.Status);
        var row = Assert.Single(BarPatronCodec.Decode(f.Storage.Provider.Capture()));
        Assert.Equal(occurrence, row.Occurrence);
    }

    private sealed class Objects : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly Storage Storage = new();
        internal readonly BarContentService Engine;
        internal readonly GameService Games;
        internal readonly IBarProvider Provider;
        internal Guid Session;
        internal Objects(ISaveDataRegistration? prerequisite = null)
        {
            Hub.SetCapability("owned-bars", true, "Test bindings.");
            Engine = new BarContentService(Storage, Hub, (plugin, _) => new StoryHostPlugin((string)plugin, typeof(Objects).Assembly), _ => true, Hub.CheckThread);
            Provider = Engine.AcquireProvider("author", prerequisite).Provider!;
            Games = new GameService(Hub, new NavigationService(Hub, _ => null, (_, _, _) => NavigationStatus.Unavailable, (_, _) => null), new InventoryService(Hub, () => null),
                new StoryContentService(Hub.Services, null, Hub, (_, _) => null), Engine);
        }
        internal void Start(byte[]? bytes = null) => Session = Ready(Hub, Storage, bytes);
        internal void Pump() { Engine.Tick(); Hub.Gameplay.Tick(); }
        public void Dispose() { Games.Dispose(); Engine.Dispose(); Hub.Dispose(); }
    }
}
