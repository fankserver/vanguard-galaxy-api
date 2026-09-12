using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonServiceTests
{
    private sealed class Persistence : TestSaveDataService
    {
        internal PersistenceProvider Provider = null!;
        public bool MutationAllowed => true;
        public bool StateReady => true;
        public string Status => "ready";
        public override bool CanRead => StateReady;
        public override bool CanMutate => StateReady && MutationAllowed;
        public override SaveDataRegistrationResult Register(PersistenceProvider provider) { Provider = provider; return new(SaveDataRegistrationStatus.Registered, this); }
        public override void Dispose() { }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly DungeonStateStore State;
        internal readonly DungeonService Service;
        internal readonly DungeonFacade Public;
        private readonly BoardingService _operations;
        private readonly BoardingCombatService _combat;
        private readonly DungeonRewardService _rewards;
        private readonly DungeonSettlementService _settlement;
        private readonly BoardingCommandService _commands;
        private readonly Runtime.BoardingTacticalAdapter _tactics;
        private readonly DungeonPanelService _panel;
        internal readonly Persistence Persistence = new();
        internal int Applied, Diagnosed, ChoiceChecks;
        internal bool ThrowNative;
        internal Func<string, BoardingHandle?>? Resolve;
        internal Fixture()
        {
            State = new(Hub, Persistence);
            var session = Hub.Begin(SessionOrigin.SaveLoad, "save"); Hub.PlayerReady(session);
            Persistence.Provider.Restore(Hub.CurrentSession!, null);
            Hub.SetAvailable("dungeon-content", "Test bindings.");
            Service = new(Hub, new(_ => true, _ => true, _ => true), State,
                new((_, _) => DungeonStatus.Attached, (_, _) => { }, (_, _, _) => { ChoiceChecks++; return DungeonStatus.ChoiceApplied; },
                    (_, _, _) => { Applied++; if (ThrowNative) throw new InvalidOperationException("native failure"); },
                    poiId => Resolve?.Invoke(poiId)), (_, _) => Diagnosed++);
            _combat = new BoardingCombatService(Hub, Hub.ReportSubscriberFailure);
            _rewards = new DungeonRewardService(Hub, Hub.ReportSubscriberFailure);
            _operations = new BoardingService(Hub, Hub.ReportSubscriberFailure);
            _settlement = new DungeonSettlementService(Hub, _operations, Hub.ReportSubscriberFailure);
            _commands = new BoardingCommandService(Hub, _operations, null, () => _combat.IsEvaluating || _rewards.IsEvaluating);
            _tactics = new Runtime.BoardingTacticalAdapter(Hub, _commands);
            _panel = new DungeonPanelService(Hub, null, Hub.ReportSubscriberFailure);
            Public = new DungeonFacade(Service, _combat, _rewards, _commands, _tactics, _operations, _settlement, _panel);
        }
        internal BoardingHandle Target => new(Hub.CurrentSession!.Id, Guid.NewGuid());
        public void Dispose()
        {
            Service.Dispose(); State.Dispose(); Hub.Dispose();
            _combat.Dispose(); _rewards.Dispose(); _operations.Dispose(); _settlement.Dispose();
            _commands.Dispose(); _tactics.Dispose(); _panel.Dispose();
        }
    }
    private static DungeonDefinition Definition(int version = 1) => new(version, "Dungeon", new DungeonLayout(new[]
    {
        new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "room" }),
        new DungeonCompartmentDefinition("room", CompartmentType.Corridor, new[] { "entry" })
    }), events: new[] { new DungeonEventDefinition("event", "room", "Choose", new[] { new DungeonChoiceDefinition("choice", "Choose") }) });
    [Fact]
    public void HealthLossInAuthorCallbackDoesNotCommitChoiceOrNativeEffects()
    {
        using var f = new Fixture(); var service = f.Service;
        using var provider = service.AcquireProvider("owner");
        using var registration = provider.Register("content", Definition(), _ =>
        { f.Hub.SetUnavailable("dungeon-content", ServiceUnavailableReason.ObserverFault, "Fault."); return true; });
        var id = provider.Attach("content", f.Target).DungeonId!.Value;
        Assert.Equal(DungeonStatus.Unavailable, provider.Choose(id, "event", "choice").Status);
        Assert.Empty(f.State.Get(id)!.Choices); Assert.Equal(0, f.Applied);
        f.Service.Dispose(); Assert.Equal(ServiceUnavailableReason.ObserverFault, service.Availability.Reason);
        Assert.Null(typeof(ModApi).GetProperty("Dungeons"));
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IDungeonContent"));
    }
    [Fact]
    public void MissingCatalogRetainsUnavailableDiagnosisAndRefusesDeclarations()
    {
        using var hub = new LifecycleHub((_, _) => { });
        hub.SetUnavailable("dungeon-content", ServiceUnavailableReason.Disabled, "Disabled.");
        using var service = new DungeonService(hub, null, null, null, (_, _) => { });
        Assert.Equal(ServiceUnavailableReason.Disabled, service.Availability.Reason);
        Assert.Throws<InvalidOperationException>(() => service.AcquireProvider("mod"));
    }
    private sealed class PanelSource : IDungeonPanelSource
    {
        internal DungeonPanelSnapshot? Snapshot;
        public DungeonPanelCapabilities Capabilities => new(true, true, true);
        public DungeonPanelSnapshot? Read() => Snapshot;
        public DungeonPanelOpenStatus Open(BoardingHandle target) => DungeonPanelOpenStatus.Opened;
    }
    [Fact]
    public void ChoiceBridgePreservesLongTextAndRemovesDeliveredActions()
    {
        using var f = new Fixture(); f.Hub.GameplayInitialized(f.Hub.CurrentSession!.Id);
        using var provider = f.Service.AcquireProvider("owner");
        using var registration = provider.Register("content", new DungeonDefinition(1, "Site", Definition().Layout,
            events: new[] { new DungeonEventDefinition("event", "room", new string('e', 4000), new[] { new DungeonChoiceDefinition("choice", new string('c', 1000)) }) }));
        var target = f.Target; var id = provider.Attach("content", target).DungeonId!.Value;
        var source = new PanelSource { Snapshot = new(Guid.NewGuid(), 1, new(target, 1, BoardingEncounterKind.Installation, "Site", null, null, BoardingAvailability.Available, null), null) };
        f.Hub.SetAvailable("dungeon-panel-opening", "Test bindings.");
        using var panel = new DungeonPanelService(f.Hub, source, (_, error) => throw error);
        using var bridge = new DungeonPanelChoices(panel, f.Service, _ => id); bridge.Refresh();
        var rows = panel.Render(); Assert.Equal(2, rows.Count); Assert.Equal(4000, rows[0].Section!.Text.Length); Assert.Equal(1000, rows[1].Action!.Tooltip.Length);
        Assert.True(panel.Activate(rows[1].Registration, rows[1].Snapshot.ViewId, rows[1].Snapshot.Revision));
        bridge.Refresh(); Assert.Empty(panel.Render()); Assert.Equal(1, f.Applied);
        source.Snapshot = null; bridge.Refresh(); Assert.Empty(panel.Render());
    }
    [Fact]
    public void ChoiceRefreshSkipsUnchangedContentAndPreservesLeasesAcrossSerialization()
    {
        using var f = new Fixture(); f.Hub.GameplayInitialized(f.Hub.CurrentSession!.Id);
        using var provider = f.Service.AcquireProvider("owner"); using var registration = provider.Register("content", Definition());
        var target = f.Target; var id = provider.Attach("content", target).DungeonId!.Value;
        var source = new PanelSource { Snapshot = new(Guid.NewGuid(), 1, new(target, 1, BoardingEncounterKind.Installation, "Site", null, null, BoardingAvailability.Available, null), null) };
        f.Hub.SetAvailable("dungeon-panel-opening", "Test bindings.");
        using var panel = new DungeonPanelService(f.Hub, source, (_, error) => throw error);
        using var bridge = new DungeonPanelChoices(panel, f.Service, _ => id);
        bridge.Refresh(); var checks = f.ChoiceChecks; bridge.Refresh(); Assert.Equal(checks, f.ChoiceChecks);
        var before = panel.Render();
        f.State.BeginSerialization();
        try { bridge.Refresh(); Assert.Empty(panel.Render()); }
        finally { f.State.EndSerialization(); }
        bridge.Refresh(); var after = panel.Render(); Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++) Assert.Equal(before[i].Registration, after[i].Registration);
        provider.Dispose(); bridge.Refresh(); Assert.Empty(panel.Render());
    }
    [Fact]
    public void PanelChoiceUsesRegisteredBehaviorAndDisappearsAfterSelectionOrDisposal()
    {
        using var f = new Fixture(); using var provider = f.Service.AcquireProvider("owner"); var allowed = false;
        using var definition = provider.Register("content", Definition(), _ => allowed);
        var id = provider.Attach("content", f.Target).DungeonId!.Value;
        Assert.Single(f.Service.PanelChoices(id));
        Assert.Equal(DungeonStatus.Vetoed, f.Service.ChooseFromPanel(id, "event", "choice").Status); Assert.Equal(0, f.Applied);
        allowed = true; Assert.Equal(DungeonStatus.ChoiceApplied, f.Service.ChooseFromPanel(id, "event", "choice").Status);
        Assert.Empty(f.Service.PanelChoices(id)); Assert.Equal(1, f.Applied);
        provider.Dispose(); Assert.Empty(f.Service.PanelChoices(id));
    }
    [Fact]
    public void AttachByInstallationResolvesIdentityWithoutDisplayNames()
    {
        using var f = new Fixture(); using var provider = f.Service.AcquireProvider("owner");
        using var registration = provider.Register("content", Definition());
        var installation = provider.GetInstallation("station-poi");
        // No live boarding target belongs to the installation yet: temporary refusal, not a name guess.
        Assert.Equal(DungeonStatus.StaleTarget, provider.Attach("content", installation).Status);
        var target = f.Target;
        f.Resolve = poiId => poiId == "station-poi" ? target : null;
        var attached = provider.Attach("content", installation);
        Assert.Equal(DungeonStatus.Attached, attached.Status);
        Assert.NotNull(attached.DungeonId);
        // Ambiguity resolves to null in the adapter and stays a temporary refusal here.
        f.Resolve = _ => null;
        Assert.Equal(DungeonStatus.StaleTarget, provider.Attach("content", installation).Status);
    }
    [Fact]
    public void ForeignOrUnobtainedInstallationObjectsAreProgrammingErrors()
    {
        using var f = new Fixture();
        using var provider = f.Service.AcquireProvider("owner");
        using var other = f.Service.AcquireProvider("other");
        using var registration = provider.Register("content", Definition());
        var foreign = other.GetInstallation("station-poi");
        Assert.Throws<ArgumentException>(() => provider.Attach("content", foreign));
        Assert.Throws<ArgumentNullException>(() => provider.Attach("content", (IDungeonInstallation)null!));
    }
    [Fact]
    public void CargoRecoveryExampleCreatesIndependentSavedAuthoredContent()
    {
        using var f = new Fixture(); using var example = new CargoRecovery.CargoEncounter(f.Public, "recovery", "native-item");
        var first = example.Attach(f.Target).DungeonId!.Value;
        var second = example.Attach(f.Target).DungeonId!.Value;
        Assert.NotEqual(first, second);
        Assert.Equal(DungeonStatus.ChoiceApplied, example.Recover(first).Status);
        Assert.Equal(DungeonStatus.ChoiceApplied, example.Leave(second).Status);
        var restored = DungeonStateCodec.Decode(f.Persistence.Provider.Capture());
        Assert.Equal(2, restored.Count);
        Assert.All(restored, dungeon =>
        {
            Assert.Equal(3, dungeon.Definition.Layout.Compartments.Count);
            Assert.False(dungeon.Definition.AllowHazards);
            Assert.False(dungeon.Definition.AllowScheduledReinforcements);
        });
    }
    [Fact]
    public void ProvidersCannotReadOrChooseEachOthersIndependentOccurrences()
    {
        using var f = new Fixture(); using var a = f.Service.AcquireProvider("a"); using var b = f.Service.AcquireProvider("b");
        using var ar = a.Register("shared", Definition()); using var br = b.Register("shared", Definition());
        var first = a.Attach("shared", f.Target); var second = a.Attach("shared", f.Target); var third = b.Attach("shared", f.Target);
        Assert.NotEqual(first.DungeonId, second.DungeonId); Assert.Equal(2, a.GetDungeons().Count); Assert.Single(b.GetDungeons());
        Assert.Equal(DungeonStatus.MissingDefinition, b.Choose(first.DungeonId!.Value, "event", "choice").Status);
        Assert.Equal(DungeonStatus.ChoiceApplied, a.Choose(first.DungeonId.Value, "event", "choice").Status);
        Assert.Equal(DungeonStatus.AlreadyChosen, a.Choose(first.DungeonId.Value, "event", "choice").Status); Assert.Equal(1, f.Applied);
    }
    [Fact]
    public void MissingAndChangedDefinitionsDoNotSilentlyAdoptSavedOccurrences()
    {
        using var f = new Fixture(); using var provider = f.Service.AcquireProvider("a"); var registration = provider.Register("id", Definition());
        var id = provider.Attach("id", f.Target).DungeonId!.Value; registration.Dispose();
        Assert.Equal(DungeonStatus.MissingDefinition, provider.Choose(id, "event", "choice").Status);
        using var changed = provider.Register("id", Definition(2));
        Assert.Equal(DungeonStatus.VersionMismatch, provider.Choose(id, "event", "choice").Status);
        Assert.Equal(1, provider.GetDungeons()[0].DefinitionVersion); Assert.Equal(0, f.Applied);
    }
    [Fact]
    public void CallbackFailureIsIsolatedButNativeFailurePropagatesWithoutRetry()
    {
        using var f = new Fixture(); using var provider = f.Service.AcquireProvider("a");
        var registration = provider.Register("id", Definition(), _ => throw new Exception("provider failure"));
        var id = provider.Attach("id", f.Target).DungeonId!.Value;
        Assert.Equal(DungeonStatus.Vetoed, provider.Choose(id, "event", "choice").Status); Assert.Equal(1, f.Diagnosed); Assert.Equal(0, f.Applied);
        registration.Dispose(); using var replacement = provider.Register("id", Definition()); f.ThrowNative = true;
        Assert.Throws<InvalidOperationException>(() => provider.Choose(id, "event", "choice"));
        Assert.Equal(DungeonStatus.AlreadyChosen, provider.Choose(id, "event", "choice").Status); Assert.Equal(1, f.Applied);
    }
    [Fact]
    public void CallbackCannotReenterAndDisposalRevokesItsPendingChoice()
    {
        using var f = new Fixture(); var provider = f.Service.AcquireProvider("a"); Guid id = default;
        using var registration = provider.Register("id", Definition(), _ =>
        {
            Assert.Equal(DungeonStatus.Unavailable, provider.Choose(id, "event", "choice").Status);
            provider.Dispose(); return true;
        });
        id = provider.Attach("id", f.Target).DungeonId!.Value;
        Assert.Equal(DungeonStatus.Unavailable, provider.Choose(id, "event", "choice").Status); Assert.Equal(0, f.Applied);
    }
    [Fact]
    public void UnavailableDetailNamesTheClosedServiceGate()
    {
        using var f = new Fixture();
        using var provider = f.Service.AcquireProvider("owner");
        using var registration = provider.Register("content", Definition(), _ =>
        { f.Hub.SetUnavailable("dungeon-content", ServiceUnavailableReason.ObserverFault, "Fault."); return true; });
        var id = provider.Attach("content", f.Target).DungeonId!.Value;
        var result = provider.Choose(id, "event", "choice");
        Assert.Equal(DungeonStatus.Unavailable, result.Status);
        // The detail names the actual gate (the service binding fault), not a bare enum name.
        Assert.Contains("ObserverFault", result.Detail, StringComparison.Ordinal);
        Assert.Contains("Fault.", result.Detail, StringComparison.Ordinal);
    }
    [Fact]
    public void UnavailableDetailNamesASpecificInjectedMutationGate()
    {
        using var hub = new LifecycleHub((_, _) => { });
        hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(hub.CurrentSession!.Id);
        hub.SetAvailable("dungeon-content", "Test bindings.");
        var bindings = new DungeonBindings(
            (_, _) => DungeonStatus.Attached, (_, _) => { }, (_, _, _) => DungeonStatus.ChoiceApplied, (_, _, _) => { }, _ => null);
        using var service = new DungeonService(hub, new(_ => true, _ => true, _ => true),
            new DungeonStateStore(hub, new Persistence()), bindings, (_, _) => { },
            mutationBlocked: () => true, mutationBlockReason: () => "Boarding combat is evaluating");
        using var provider = service.AcquireProvider("owner");
        using var registration = provider.Register("content", Definition(), _ => true);
        var result = provider.Choose(Guid.NewGuid(), "event", "choice");
        Assert.Equal(DungeonStatus.Unavailable, result.Status);
        // With no service-level gate closed, the detail comes from the injected mutation gate.
        Assert.Contains("Boarding combat", result.Detail, StringComparison.Ordinal);
    }
}
