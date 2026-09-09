using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using VGModAPI.Examples;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ServiceConsumerTests
{
    [Fact]
    public void MissionObserverReportsInitialHealthAndRejectsStaleSessionFacts()
    {
        var session = Guid.NewGuid();
        var lifecycle = new FakeLifecycleService { CurrentSession = new SessionSnapshot(session, SessionPhase.PlayerReady, SessionOrigin.SaveLoad, "save") };
        var missions = new FakeMissionService();
        var observed = new List<MissionTransition>();
        var statuses = new List<ServiceAvailability>();
        using var consumer = new MissionObserver(missions, lifecycle, observed.Add, statuses.Add);
        Assert.Single(statuses);
        Assert.True(statuses[0].IsAvailable);
        Assert.Empty(observed);
        missions.Emit(MissionFact(Guid.NewGuid()));
        missions.Emit(MissionFact(session));
        Assert.Single(observed);
        lifecycle.CurrentSession = new SessionSnapshot(session, SessionPhase.Invalidated, SessionOrigin.SaveLoad, "save");
        missions.Emit(MissionFact(session));
        Assert.Single(observed);
        missions.SetAvailability(ServiceUnavailableReason.ObserverFault);
        Assert.Equal(ServiceUnavailableReason.ObserverFault, statuses[^1].Reason);
        Assert.Equal(0, missions.TransitionListeners);
        Assert.Equal(0, missions.AvailabilityListeners);
        missions.SetAvailability(ServiceUnavailableReason.None);
        missions.Emit(MissionFact(session));
        Assert.Single(observed);
    }

    [Fact]
    public void MissionObserverDoesNotDemandSavedIdentityOrInventASession()
    {
        var missions = new FakeMissionService();
        ((FakeServiceStatus)missions.IdentityContinuity).SetAvailability(ServiceUnavailableReason.DependencyUnavailable);
        var lifecycle = new FakeLifecycleService();
        var count = 0;
        using var consumer = new MissionObserver(missions, lifecycle, _ => count++, _ => { });
        missions.Emit(MissionFact(Guid.NewGuid()));
        Assert.Equal(0, count);
        Assert.Equal(1, missions.TransitionListeners);
        var session = Guid.NewGuid();
        lifecycle.CurrentSession = new SessionSnapshot(session, SessionPhase.GameplayInitialized, SessionOrigin.NewGame, null);
        missions.Emit(MissionFact(session));
        Assert.Equal(1, count);
    }

    [Fact]
    public void MissionObserverCleansUpFailedConstructionOrCallbackAndRepeatedDisposal()
    {
        var session = Guid.NewGuid();
        var lifecycle = new FakeLifecycleService { CurrentSession = new SessionSnapshot(session, SessionPhase.PlayerReady, SessionOrigin.NewGame, null) };
        var missions = new FakeMissionService();
        Assert.Throws<InvalidOperationException>(() => new MissionObserver(missions, lifecycle, _ => { }, _ => throw new InvalidOperationException()));
        Assert.Equal(0, missions.AvailabilityListeners);
        Assert.Equal(0, missions.TransitionListeners);
        var consumer = new MissionObserver(missions, lifecycle, _ => throw new InvalidOperationException(), _ => { });
        Assert.Throws<InvalidOperationException>(() => missions.Emit(MissionFact(session)));
        consumer.Dispose(); consumer.Dispose();
        Assert.Equal(0, missions.AvailabilityListeners);
        Assert.Equal(0, missions.TransitionListeners);
    }

    [Fact]
    public void OptionalConsumerHandlesNoProviderWrongProviderAndFailedDiagnostics()
    {
        using var absent = new OptionalTravelObserver(null, _ => { }, _ => throw new InvalidOperationException());
        Assert.False(absent.IsListening);
        var messages = new List<string>();
        using var wrong = new OptionalTravelObserver(() => new object(), _ => { }, messages.Add);
        Assert.False(wrong.IsListening);
        Assert.Single(messages);
        using var badLogger = new OptionalTravelObserver(() => throw new InvalidOperationException(), _ => { }, _ => throw new InvalidOperationException());
        Assert.False(badLogger.IsListening);
    }

    [Fact]
    public void OptionalConsumerStopsOnlyItsObservationAndRejectsStaleFacts()
    {
        var session = Guid.NewGuid();
        var travel = new FakeTravelService { SessionId = session };
        var count = 0;
        using var consumer = new OptionalTravelObserver(() => travel, _ => count++, _ => { });
        Assert.True(consumer.IsListening);
        travel.Emit(TravelFact(Guid.NewGuid()));
        Assert.Equal(0, count);
        travel.Emit(TravelFact(session));
        Assert.Equal(1, count);
        travel.SetAvailability(ServiceUnavailableReason.ApiStopped);
        Assert.False(consumer.IsListening);
        Assert.Equal(0, travel.TransitionListeners);
        Assert.Equal(0, travel.AvailabilityListeners);
        consumer.Dispose();
        travel.Emit(TravelFact(session));
        Assert.Equal(1, count);
    }

    [Fact]
    public void OptionalConsumerUnsubscribesAfterItsOwnCallbackFailure()
    {
        var travel = new FakeTravelService { SessionId = Guid.NewGuid() };
        using var consumer = new OptionalTravelObserver(() => travel, _ => throw new InvalidOperationException(), _ => { });
        Assert.Throws<InvalidOperationException>(() => travel.Emit(TravelFact(travel.SessionId!.Value)));
        Assert.False(consumer.IsListening);
        Assert.Equal(0, travel.TransitionListeners);
        Assert.Equal(0, travel.AvailabilityListeners);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalEntryPointLoadsWithTheApiAssemblyRefused(bool supplyFactory)
    {
        var context = new WithoutApiContext();
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(OptionalTravelObserver).Assembly.Location);
            var type = assembly.GetType("VGModAPI.Examples.OptionalTravelObserver", throwOnError: true)!;
            var messages = new List<string>();
            var instance = Activator.CreateInstance(type, new object?[]
            {
                supplyFactory ? (Func<object>)(() => new object()) : null,
                (Action<Guid>)(_ => { }), (Action<string>)messages.Add
            })!;
            Assert.False((bool)type.GetProperty("IsListening")!.GetValue(instance)!);
            ((IDisposable)instance).Dispose();
            if (supplyFactory) { Assert.NotEqual(0, context.Refusals); Assert.Single(messages); }
            else { Assert.Equal(0, context.Refusals); Assert.Empty(messages); }
        }
        finally { context.Unload(); }
    }

    private sealed class WithoutApiContext : AssemblyLoadContext
    {
        internal int Refusals { get; private set; }
        internal WithoutApiContext() : base(isCollectible: true) { }
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name != "VGModAPI.Abstractions") return null;
            Refusals++;
            throw new FileNotFoundException("API deliberately absent.");
        }
    }

    [Fact]
    public void CustomDataSeparatesReadabilityFromMutationAndNeverReregistersOnStateChange()
    {
        var save = new FakeSaveDataService();
        CustomCounter? counter = null;
        var attemptedInNotification = new List<bool>();
        counter = new CustomCounter(save, "example.counter", _ => { if (counter != null) attemptedInNotification.Add(counter.TryIncrement()); });
        using (counter)
        {
            Assert.False(counter.TryRead(out _));
            Assert.False(counter.TryIncrement());
            var session = new SessionSnapshot(Guid.NewGuid(), SessionPhase.PlayerReady, SessionOrigin.NewGame, null);
            save.Provider!.Restore(session, null);
            save.Registration.SetState(new SaveDataState(SaveDataStateKind.Ready, session.Id), read: true, mutate: true);
            Assert.Equal(new[] { false }, attemptedInNotification);
            Assert.True(counter.TryIncrement());
            save.Registration.SaveInFlight = true;
            Assert.True(counter.TryRead(out var value));
            Assert.Equal(1, value);
            Assert.False(counter.TryIncrement());
            Assert.Equal(new byte[] { 1, 0, 0, 0 }, save.Provider.Capture());
            save.Registration.SaveInFlight = false;
            save.Registration.SetState(new SaveDataState(SaveDataStateKind.Blocked, session.Id, SaveDataBlockReason.PublicationFailed));
            Assert.False(counter.TryRead(out _));
            Assert.False(counter.TryIncrement());
            var replacement = new SessionSnapshot(Guid.NewGuid(), SessionPhase.PlayerReady, SessionOrigin.SaveLoad, "save");
            save.Provider.Restore(replacement, new byte[] { 7, 0, 0, 0 });
            save.Registration.SetState(new SaveDataState(SaveDataStateKind.Ready, replacement.Id), read: true, mutate: true);
            Assert.True(counter.TryRead(out value));
            Assert.Equal(7, value);
            Assert.Equal(1, save.RegisterCalls);
        }
        Assert.Equal(1, save.Registration.DisposeCalls);
        Assert.Equal(0, save.Registration.Listeners);
    }

    [Fact]
    public void CustomDataValidatesPayloadAndHonorsRefusalAndTeardown()
    {
        var save = new FakeSaveDataService { Result = SaveDataRegistrationStatus.SessionAlreadyStarted };
        Assert.Throws<InvalidOperationException>(() => new CustomCounter(save, "example.counter", _ => { }));
        Assert.Equal(0, save.Registration.Listeners);
        save.Result = SaveDataRegistrationStatus.Registered;
        Assert.Throws<InvalidOperationException>(() => new CustomCounter(save, "example.counter", _ => throw new InvalidOperationException()));
        Assert.True(save.Registration.Disposed);
        save = new FakeSaveDataService();
        using var counter = new CustomCounter(save, "example.counter", _ => { });
        var session = new SessionSnapshot(Guid.NewGuid(), SessionPhase.PlayerReady, SessionOrigin.NewGame, null);
        Assert.False(save.Provider!.Validate(new byte[3]));
        Assert.False(save.Provider.Validate(new byte[] { 0, 0, 0, 128 }));
        Assert.Throws<ArgumentException>(() => save.Provider.Restore(session, new byte[3]));
        save.Provider.Restore(session, new byte[] { 255, 255, 255, 127 });
        save.Registration.SetState(new SaveDataState(SaveDataStateKind.Ready, session.Id), read: true, mutate: true);
        Assert.False(counter.TryIncrement());
        save.Registration.Dispose();
        counter.Dispose(); counter.Dispose();
        Assert.False(counter.TryRead(out _));
        Assert.Equal(1, save.Registration.DisposeCalls);
    }

    private static MissionTransition MissionFact(Guid session) => new(MissionTransitionKind.Accepted,
        new MissionSnapshot(session, Guid.NewGuid(), "example", "Example", Array.Empty<string>(), true), 1);
    private static TravelTransition TravelFact(Guid session) => new(session, Guid.NewGuid(), 1, TravelTransitionKind.RouteCompleted,
        TravelMode.InSystem, null, null, new TravelLocation("system", null, null, null), 1, null);
}
