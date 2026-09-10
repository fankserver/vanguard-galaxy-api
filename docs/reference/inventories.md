# Player-owned inventories

Inventories belong to a captured game. Get them through `game.Inventories`; old
inventory objects never rebind to another save. A transfer owns its execution,
transaction identity and recovery rather than asking the mod to coordinate them.

```csharp
var source = game.Inventories.Get(sourceReference);
var destination = game.Inventories.Get(destinationReference);
var move = source.MoveTo(destination, selectedStackId, quantity);
move.Completed += completed => ShowResult(completed.Result);
```

## Endpoints and access

| Kind | Owner and location | Movement access |
|---|---|---|
| ShipCargo | Local player's exact current ship GUID | Docked at a station; capacity uses the ship's actual cargo capacity |
| PlayerArmory | Local player, global rather than physically station-owned | Docked; incoming items must pass native armory eligibility |
| StationMaterials | Local player's belongings at the exact station POI | Remote station-to-station movement supported; movement to/from player inventories requires docking at that station; native materials eligibility applies |
| PlayerData | Local player, global | Readable, movement unsupported because data/currency have special routing |

`Discover()` returns inventory objects and an honest discovery result. `Get(reference)`
returns the same object for that reference within a game. A reference does not
create a container or grant access: `Snapshot` is null if the inventory cannot be
resolved, and moves recheck the exact endpoints before changing anything.

Shop stock, other ships, hidden stations and arbitrary containers are not endpoints.
Discovery never generates shop stock or creates containers. An unavailable inventory
is not an empty inventory. Copied snapshots expose access, capacity and individual
stacks; select a stack from the inventory's snapshot rather than guessing its ID.

## Moves and completion

`MoveTo` returns an `IInventoryTransfer`. Its `Result` starts as `Pending`; the API
executes at a safe gameplay boundary. No caller-generated operation ID, dispatch
check, polling loop or recovery call is needed. `Completed` delivers the terminal
result and may start another gameplay operation. It does not replay, so subscribe
when creating the move. Each subscriber is isolated; removing a handler before
its turn suppresses it. A completion-triggered move executes at a later boundary.

A move requires distinct inventories in the same game and a quantity of 1–100000.
By default insufficient stock/capacity refuses the whole request. `AllowPartial`
permits the smaller fitting quantity. Favourites require `IncludeFavourite`;
mission-required items remain protected. Buyback/sold/cost-item stock and Vanguard
marks are refused. Data items do not bypass native special routing through cargo.

The selected native item instance and stack flags are preserved; a changed stack
is not silently replaced with another instance. Destination insertion uses a
separate slot (bounded to 16384), and visible-item caches refresh after movement.
These are inventory moves, not purchases, pickups, fees or shipment admission.

## Safety and recovery

Save/serialization boundaries delay execution internally. The API prepares both
replacement inventory arrays before publishing them, with no consumer callback
between debit and credit. Success requires both arrays to match. A failed
publication restores only that transaction's own arrays; unrelated replacements
are never overwritten.

Unverified rollback keeps the move pending with unknown quantities, blocks further
movement and prevents saving inconsistent inventory state. The API retries that
rollback itself at safe boundaries. It does not retry the transfer or duplicate
its effects. Successful rollback ends the move without claiming items moved.

A replaced game ends its queued moves as `GameEnded` and suppresses old-game
completion callbacks; retained move objects still expose their result. Accounting
is left unknown where it cannot be established. Outstanding rollback ownership
is retained until verified rather than discarded on a load.

## Persistence and scope

Completed movement lives in vanilla inventories. No consumer save hooks or
transfer sidecars are required. Loading an older save restores its older contents;
operation IDs in results are runtime correlation, not a durable shipment ledger.

Safe-boundary scheduling is not a shipping simulation. This API does not reserve
goods for later, compute travel times, charge delivery fees or schedule shipment
arrivals. A move is not a cancellable shipping job. Each move commits independently;
multiple moves and separate credit changes are not an atomic group. Consumers
requiring all-or-nothing manifests and fees must retain a transaction design that
provides that guarantee rather than treating several `MoveTo` calls as one move.
Those are distinct gameplay features.
