# Navigation

`game.Navigation` provides main-thread station metadata, directed jump-gate hop counts and native map-focus requests for a captured game. Obtain that game from a gameplay event or `ModApi.Services.Game.Current`. Operations retain their game internally: old navigation objects cannot act on a replacement save, and callers pass no session token. Inspect each operation's result rather than guessing availability.

## Queries

`GetStations()` returns visible, previously visited stations by default. Pass `visitedOnly: false` to include unvisited stations already present in the map. Hidden POIs are always excluded. This is metadata, not shop stock or purchase availability. The API never initializes shops, generates inventory, or calls the lazy native name getter. `Name` can therefore be null. Inventory, price and logistics policy stay with consumers.

`GetJumpCounts(fromSystemId)` reads the graph once for a complete distance list (reachable IDs only). `GetJumpCount(fromSystemId, toSystemId)` computes the shortest **unweighted, directed jump-gate hop count**. It uses existing gate targets, ignores access passes, hostility, fuel and travel restrictions, and is not an access-aware route or promise that the player can travel. A gated edge still counts as one hop. Same-system count is zero. Missing identifiers and disconnected systems have distinct statuses; unsuccessful results have no hop count.

Queries read current membership without persistent caches or saved query state. They refuse changed or stale map membership. Use stable native system/POI IDs, not display names. Fetch on UI refresh rather than every frame.

## Focus

`FocusPoi(poiId)` requests the native map tab and selected POI. Success means scheduled, not travel or completed UI animation. Missing/hidden destinations and overlapping focus requests are refused. API-owned world sites require `FocusResourceSite(reference)` instead of their internal native IDs. This resolves provider, local ID and instance ID against restored world content and continues checking that reference while the request is active. Two providers can use the same local destination name without aliasing.

The native coroutine is stopped on session replacement, API shutdown or loss/replacement of its target. Cleanup does not clear a different focus target. The operation never starts travel or creates destinations. Consumers should not run a parallel native focus coroutine for the same request.

Stockpile-style station locators and ItemCompass-style distance lists can use these operations without private game objects. Consumer save formats and inventory behavior are independent of this service.
