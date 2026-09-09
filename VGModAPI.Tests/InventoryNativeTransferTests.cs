using System;
using System.Linq;
using Source.Galaxy;
using Source.Galaxy.POI;
using Source.Player;
using Source.SpaceShip;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;
public sealed class InventoryNativeTransferTests
{
    [Fact]
    public void MovesExactFavouriteStackBetweenCargoAndStationsInBothDirections()
    {
        var previous = GamePlayer.current; var previousStation = SpaceStation.TestCurrent;
        try
        {
            var session = Guid.NewGuid(); var map = new GalaxyMapData();
            var sector = new SectorMapData { guid = "sector" }; var system = new SystemMapData { guid = "system" };
            map.TestSectors.Add(sector); sector.TestSystems.Add(system);
            var a = new SpaceStation { guid = "a", system = system }; var b = new SpaceStation { guid = "b", system = system };
            system.pointsOfInterest.Add(a); system.pointsOfInterest.Add(b);
            var ship = new SpaceShipData { dockingState = Source.SpaceShip.Auto.DockingState.Docked };
            GamePlayer.current = new GamePlayer { map = map, currentSpaceShip = ship }; SpaceStation.TestCurrent = a;
            var item = new Behaviour.Item.InventoryItemType { identifier = "goods", displayName = "Goods" };
            ship.cargo.items = new[] { new Source.Item.Inventory.InventoryItem(item, ship.cargo, 0, 10, false) { favourite = true } };
            var backend = new InventoryNativeBackend(typeof(GamePlayer).Assembly, id => id == session);
            var cargo = new InventoryHandle(session, new InventoryReference(InventoryKind.ShipCargo, ship.guid));
            var stationA = new InventoryHandle(session, new InventoryReference(InventoryKind.StationMaterials, "a"));
            var stationB = new InventoryHandle(session, new InventoryReference(InventoryKind.StationMaterials, "b"));
            var stack = Assert.Single(backend.Resolve(session, cargo.Reference)!.Stacks);
            Assert.Equal(InventoryTransferStatus.Protected, backend.Prepare(cargo, stationA, stack.StackId, 10, new()).Status);
            void Move(InventoryHandle source, InventoryHandle destination)
            {
                var selected = Assert.Single(backend.Resolve(session, source.Reference)!.Stacks);
                var move = backend.Prepare(source, destination, selected.StackId, 10, new(includeFavourite: true));
                Assert.Equal(InventoryTransferStatus.Succeeded, move.Status);
                Assert.Equal(InventoryCommitStatus.Committed, move.Commit!.Commit()); move.Refresh();
                Assert.Empty(backend.Resolve(session, source.Reference)!.Stacks);
                var received = Assert.Single(backend.Resolve(session, destination.Reference)!.Stacks);
                Assert.Equal(10, received.Count); Assert.True(received.Favourite);
            }
            Move(cargo, stationA); Move(stationA, stationB); Move(stationB, stationA); Move(stationA, cargo);
            Assert.Same(item, ship.cargo.items.Single().item);
            Assert.True(ship.cargo.Refreshes > 0); Assert.True(a.materialStorage.Refreshes > 0);
            a.materialStorage.Capacity = 3;
            var currentStack = Assert.Single(backend.Resolve(session, cargo.Reference)!.Stacks);
            Assert.Equal(InventoryTransferStatus.CapacityExceeded, backend.Prepare(cargo, stationA, currentStack.StackId, 5, new(includeFavourite: true)).Status);
            var partial = backend.Prepare(cargo, stationA, currentStack.StackId, 5, new(allowPartial: true, includeFavourite: true));
            Assert.Equal(3, partial.Quantity); Assert.Equal(InventoryCommitStatus.Committed, partial.Commit!.Commit());
            Assert.Equal(7, ship.cargo.items.Single().count); Assert.Equal(3, a.materialStorage.items.Single().count);
            Assert.Equal(InventoryTransferStatus.Missing, backend.Prepare(cargo, stationA, currentStack.StackId, 1, new(includeFavourite: true)).Status);
            var live = Assert.Single(backend.Resolve(session, cargo.Reference)!.Stacks);
            Assert.Equal(InventoryTransferStatus.AccessDenied, backend.Prepare(cargo, stationB, live.StackId, 1, new(includeFavourite: true)).Status);
            Assert.Equal(InventoryTransferStatus.Unsupported, backend.Prepare(cargo, new(session, new(InventoryKind.PlayerData)), live.StackId, 1, new(includeFavourite: true)).Status);
            var armory = new InventoryHandle(session, new(InventoryKind.PlayerArmory));
            Assert.Equal(InventoryTransferStatus.Unsupported, backend.Prepare(cargo, armory, live.StackId, 1, new(includeFavourite: true)).Status);
            var equipment = new Behaviour.Item.InventoryItemType { identifier = "unique-module", itemCategory = Source.Item.ItemCategory.Module,
                itemLevel = 23, equipmentBuilder = new Behaviour.Equipment.Builder.EquipmentBuilder() };
            ship.cargo.items = ship.cargo.items.Concat(new[] { new Source.Item.Inventory.InventoryItem(equipment, ship.cargo, 1, 1, false) }).ToArray();
            var module = backend.Resolve(session, cargo.Reference)!.Stacks.Single(x => x.ItemId == "unique-module");
            var deposit = backend.Prepare(cargo, armory, module.StackId, 1, new());
            Assert.Equal(InventoryCommitStatus.Committed, deposit.Commit!.Commit());
            Assert.Same(equipment, GamePlayer.current.globalInventory.items.Single().item);
            var withdrawal = backend.Prepare(armory, cargo, backend.Resolve(session, armory.Reference)!.Stacks.Single().StackId, 1, new());
            Assert.Equal(InventoryCommitStatus.Committed, withdrawal.Commit!.Commit());
            Assert.Same(equipment, ship.cargo.items.Single(x => x.item.identifier == "unique-module").item);
            Assert.Equal(23, equipment.itemLevel);
            foreach (bool remove in new[] { true, false })
            {
                ship.cargo.items = new[] { new Source.Item.Inventory.InventoryItem(equipment, ship.cargo, 0, 1, false) };
                var selected = backend.Resolve(session, cargo.Reference)!.Stacks.Single();
                var array = (Array)typeof(Source.Item.Inventory).GetField("allItems", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(ship.cargo)!;
                var replacement = remove ? null : new Source.Item.Inventory.InventoryItem(item, ship.cargo, 0, 4, false);
                GamePlayer.current.RequirementCallback = () => array.SetValue(replacement, 0);
                Assert.Equal(InventoryTransferStatus.Changed, backend.Prepare(cargo, armory, selected.StackId, 1, new()).Status);
                Assert.Same(replacement, array.GetValue(0)); Assert.Empty(GamePlayer.current.globalInventory.items);
                GamePlayer.current.RequirementCallback = null;
            }
        }
        finally { GamePlayer.current = previous; SpaceStation.TestCurrent = previousStation; }
    }
}
