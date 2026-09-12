# Owned recipes

`ModApi.Services.RecipeRegistration` acquires an authenticated provider lease. Register `OwnedRecipeDefinition` records with a provider-local ID, revision, name, base credit cost, duration, up to eight item ingredients and one fixed item output row. Each row explicitly references either a vanilla catalog ID or an owned provider/local item. Duplicate ingredient references and invalid bounds are refused. Economic balance remains author policy; native skill discounts and Forge speed modifiers still apply.

## Registration and use

Recipes may register before their owned items. `PendingDependencies` retains a declaration for automatic resolution on item registration or catalog reload. One missing dependency does not block unrelated declarations. `Find(localId)` returns a resolved `RecipeId` for the existing catalog, quote and crafting-command APIs, or null while pending. The identity is an opaque transport handle, not a display name or permission to skip station/session checks.

The native adapter accepts plain TradeGoods, RefinedProduct, Ore, Salvage, Junk and Crystal items, including API-owned goods. Equipment/item builders, arbitrary components and generated equipment outputs are not supported. Storage delivery is vanilla: current ship cargo when eligible, otherwise armory or station material storage according to the item's routing. The existing crafting APIs retain their readiness, quantity, observation and command refusal rules.

## Saved jobs and revisions

A bounded canonical recipe identifier contains the resolved immutable definition, including the exact owned-item definitions used by ingredients/output. Vanilla Forge jobs already save their recipe identifier, initial/remaining batches, progress and crafted level. The API reconstructs the retained recipe before native job loading; authors do not implement persistence callbacks or duplicate job state.

Item/recipe references mark a native snapshot as API-required, including a recipe-only saved job. Missing or unsupported reconstruction fails before the load barrier is removed. The original game version remains authoritative. Failed/skipped writes do not commit a separate recipe ledger; older saves and slot changes carry their own definitions, inventory and job counters together.

Revisions coexist without aliasing old jobs to new costs, durations or outputs. Retired/provider-absent recipes remain available internally to already-saved jobs, but are not newly unlocked in the Forge. Provider disposal retires declarations without destroying hosts referenced by live jobs. Existing jobs continue using the saved definition; this fixed-data shape requires the API and its vanilla dependencies, not provider executable code. No arbitrary save migration or safe-uninstall promise is made.

The [StationCommerce example](../../examples/StationCommerce/Plugin.cs) registers a recipe before its item and contains no save/load plumbing. It declares content only; it does not start crafting or modify player inventory automatically.
