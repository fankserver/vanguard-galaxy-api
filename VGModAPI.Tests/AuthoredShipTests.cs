using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

internal sealed class FakeAuthoredShipNative : IAuthoredShipNative
{
    internal readonly Dictionary<string, string> Units = new(StringComparer.Ordinal); // unitId -> station
    internal readonly List<(string Station, string Unit)> Maintained = new();
    internal bool RefuseCreation;
    internal int Creations;
    private int _next;
    public string? CreateShip(Guid session, string stationPoiId, AuthoredShipDeclaration declaration)
    {
        Creations++;
        if (RefuseCreation || stationPoiId == "missing") return null;
        var id = "unit-" + ++_next;
        Units[id] = stationPoiId;
        return id;
    }
    public bool ResolveShip(Guid session, string stationPoiId, string unitId)
        => Units.TryGetValue(unitId, out var station) && station == stationPoiId;
    public void Maintain(Guid session, string stationPoiId, string unitId, AuthoredShipDeclaration declaration)
        => Maintained.Add((stationPoiId, unitId));
}

public sealed class AuthoredShipTests
{
    private sealed class Harness : IDisposable
    {
        internal readonly LifecycleHub Hub;
        internal readonly FakeAuthoredShipNative Native;
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly AuthoredShipRegistry Ships;
        internal readonly AuthoredShipCoordinator Coordinator;
        internal readonly WorldContentService Service;
        internal readonly List<(string UnitId, string? Key)> Protections = new();
        internal IWorldProvider Provider = null!;
        internal Guid Session;
        internal Harness()
        {
            Hub = new LifecycleHub((_, error) => throw error);
            Native = new FakeAuthoredShipNative();
            var plugin = new object();
            StoryHostAuthenticator auth = (instance, caller) => ReferenceEquals(instance, plugin) ? new StoryHostPlugin("author.a", caller) : null;
            Combat = new WorldDefinitionRegistry(auth, Hub.CheckThread);
            Ships = new AuthoredShipRegistry(auth, Hub.CheckThread);
            Coordinator = new AuthoredShipCoordinator(Hub, Ships, Native, _ => true, (unit, key) => Protections.Add((unit, key)), _ => { });
            Service = new WorldContentService(Hub, Combat, null!, () => true, null, null, null, null, null, null, null, null, Ships, Coordinator);
            Provider = Service.AcquireProvider(plugin)!;
        }
        internal void BeginGameplay()
        {
            Session = Hub.Begin(SessionOrigin.NewGame, null);
            Hub.PlayerReady(Session); Hub.GameplayInitialized(Session);
        }
        public void Dispose() { Provider?.Dispose(); Coordinator.Dispose(); Service.Dispose(); Combat.Dispose(); Ships.Dispose(); Hub.Dispose(); }
    }
    private static AuthoredShipDefinition Promise(int revision = 1)
        => new("promise", revision, "Foundation's Promise", "Redemption", "Gold", 24, 30, protect: true);

    [Fact]
    public void KeyedCreationOwnsIdentityProtectsAndMaintains()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterAuthoredShip(Promise()));
        Assert.Equal(WorldStatus.DuplicateDefinition, h.Provider.RegisterAuthoredShip(Promise()));
        h.BeginGameplay();
        var ship = h.Provider.CreateAuthoredShip("promise", "act3", "station-poi");
        Assert.NotNull(ship);
        Assert.True(ship!.State.Reconstructed);
        Assert.NotNull(ship.UnitId);
        Assert.Same(ship, h.Provider.CreateAuthoredShip("promise", "act3", "station-poi"));
        Assert.Same(ship, h.Provider.GetAuthoredShip("promise", "act3"));
        Assert.Equal(1, h.Native.Creations);
        var protection = Assert.Single(h.Protections);
        Assert.Equal(ship.UnitId, protection.UnitId);
        Assert.Contains("act3", protection.Key);
        // Maintenance runs at the world pass and is keyed to the owned identity.
        h.Service.MaintainAuthoredSystems(h.Session);
        Assert.Contains(("station-poi", ship.UnitId!), h.Native.Maintained);
        // Repeated maintenance does not re-declare protection.
        h.Service.MaintainAuthoredSystems(h.Session);
        Assert.Single(h.Protections);
    }

    [Fact]
    public void RestoredRowsReconstructAndSettleWithActualOutcomes()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterAuthoredShip(Promise()));
        h.BeginGameplay();
        var ship = h.Provider.CreateAuthoredShip("promise", "act3", "station-poi")!;
        var rows = h.Coordinator.CaptureRows();
        var bytes = AuthoredSystemStateCodec.Encode(Array.Empty<AuthoredSystemOccurrence>(), Array.Empty<AuthoredSiteOccurrence>(), rows);
        var decoded = AuthoredSystemStateCodec.DecodeAll(bytes);
        Assert.Single(decoded.Ships);

        using var restored = new Harness();
        Assert.Equal(WorldStatus.Succeeded, restored.Provider.RegisterAuthoredShip(Promise()));
        AuthoredShipsSettledEvent? settled = null;
        restored.Provider.AuthoredShipReconstructionSettled += e => settled = e;
        restored.BeginGameplay();
        restored.Coordinator.RestoreRows(restored.Session, decoded.Ships);
        // Datum not yet natively present: settles as NativeMissing, then converges with a Changed event.
        restored.Service.MaintainAuthoredSystems(restored.Session);
        Assert.NotNull(settled);
        var failure = Assert.Single(settled!.Failures);
        Assert.Equal(AuthoredSystemFailureReason.NativeMissing, failure.Reason);
        int changes = 0;
        failure.Occurrence.Changed += _ => changes++;
        restored.Native.Units[ship.UnitId!] = "station-poi";
        restored.Service.MaintainAuthoredSystems(restored.Session);
        Assert.True(failure.Occurrence.State.Reconstructed);
        Assert.Equal(1, changes);
        // Restoration re-declares protection for the owned identity exactly once.
        Assert.Single(restored.Protections, p => p.UnitId == ship.UnitId);
    }

    [Fact]
    public void CrossKindKeysAndTypedRefusalsAreEnforced()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterAuthoredShip(Promise()));
        h.BeginGameplay();
        h.Native.RefuseCreation = true;
        var refused = h.Provider.CreateAuthoredShip("promise", "k", "station");
        Assert.Equal(AuthoredActionStatus.Rejected, refused!.LastAction.Status);
        h.Native.RefuseCreation = false;
        Assert.Equal(AuthoredActionStatus.Rejected, h.Provider.CreateAuthoredShip("promise", "k", "station")!.LastAction.Status);
        Assert.Equal(1, h.Native.Creations);
        Assert.Null(h.Provider.GetAuthoredShip("promise", "k"));
        Assert.Null(h.Provider.CreateAuthoredShip("promise", "bad key\u0000", "station"));
    }
}
