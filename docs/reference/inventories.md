# Player-owned inventories

`ModApi.Services.Inventories` provides immutable snapshots and immediate, main-thread transfers. Availability describes inspected integration support; each operation independently requires a current gameplay session.

## Endpoints and access

| Kind | Owner and location | Movement access |
|---|---|---|
| ShipCargo | Local player's exact current ship GUID | Docked at a station; capacity uses the ship's actual cargo capacity |
| PlayerArmory | Local player, global rather than physically station-owned | Docked; incoming items must pass native armory eligibility |
| StationMaterials | Local player's belongings at the exact station POI | Remote station-to-station movement supported; movement to/from player inventories requires docking at that station; native materials eligibility applies |
| PlayerData | Local player, global | Readable, movement unsupported because data/currency have special routing |

Shop/trade stock, other ships, hidden stations and arbitrary containers are not endpoints. Discovery neither generates shop stock nor creates containers. An unavailable container is not an empty container. Snapshots expose access state, volume capacity and independent stack selections; they do not aggregate nonfungible instances. A reference is save-local: resolve it again after loading the intended save, never interpret it as a campaign identity. Runtime handles and stack IDs become stale across sessions or stack replacement.

## Transfer semantics

Choose a stack from a fresh snapshot and supply a nonempty operation GUID, two distinct handles, quantity 1–100000 and explicit options. By default insufficient stock/capacity refuses the whole request. `AllowPartial` permits the smaller fitting quantity. Favourites require `IncludeFavourite`; mission-required items remain protected. Buyback/sold/cost-item stock and Vanguard marks are refused. Data items never bypass native special routing through cargo.

The adapter retains the exact native item instance and copies stack flags, rather than resolving a generated item by type or merging unrelated stacks. Destination insertion uses a separate slot (bounded to 16384); the inspected container has volume rather than a gameplay slot limit. Native visible-item caches are refreshed after movement. API notifications describe the actual result; they are not purchases, pickups, fees or shipment admission.

Both replacement inventory arrays are allocated before publication. The inspected field assignments invoke no virtual Add/Remove or consumer callbacks between debit and credit. Success requires both published arrays to match. A failed assignment restores only this transaction's own arrays and verifies restoration; unrelated replacements are never overwritten. An unverified recovery returns unknown quantities, retains `PendingRecovery`, blocks further movement and refuses serialization/save. Use its session and operation ID with `Recover`; a successful recovery changes the result and notifies subscribers. Recovery state is not discarded just because a new session begins.

Operation IDs deduplicate accepted requests within a session, including refused prepared operations. Reusing an ID with different arguments is invalid. A repeat does not repeat publication or notification. There is a bounded 4096-operation session history. Pre-admission refusals (busy/stale/not-ready/invalid) are not reserved operations. Callbacks are isolated and observational; mutation during dispatch or serialization is refused.

## Persistence and deferred delivery

Completed movement lives entirely in vanilla inventories: no consumer save hooks or transfer sidecars are required. Native saves retain both inventory contents and item representations. Loading an older save restores its older contents; operation IDs are runtime-only and must not be used as a durable shipment ledger.

This service is the **immediate movement primitive**, not a replacement for Stockpile's pending-transfer engine. It does not reserve goods for later, schedule arrivals, charge fees, compute ETA or own deferred jobs. Managed deferred delivery remains separate functionality; consumers must not infer it from an immediate transfer result.
