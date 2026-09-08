# Recipe catalog — experimental

Requires API **0.1.29** or newer. `ModApi.Recipes` exposes immutable Forge/refining definitions for the current station. Enable `[Recipes] Enabled = true`; the service is absent when disabled, uninspected or unable to bind. Calls are main-thread-only and require a tracked `GameplayInitialized` session plus an accessible station with supported crafting facilities. Refining does not require that the station also has a Forge. This is not universal UI/world readiness or native qualification.

## Querying

```csharp
var catalog = ModApi.Recipes?.Read();
if (catalog?.Status != RecipeCatalogStatus.Available) return;
var wanted = new RecipeResourceId("vanilla", "MyKnownItemId", RecipeResourceKind.Item);
var alternatives = catalog.FindProducers(wanted);
// Let the player/mod select a route; there can be zero, one or many producers.
```

`Read(includeUnavailable: true)` additionally includes known unavailable Forge variants. `FindProducers` excludes unavailable/unsupported variants unless explicitly requested. `Available` means the definition is a supported process in this catalog, not that the player can afford or queue it. Definition availability does not count inventory or reserve ingredients. A failed query is not a successful empty catalog.

Reads rebuild snapshots from the current registries and the station's actual virtual recipe list. There is no retained Unity recipe reference or stale catalog cache. Session invalidation during a query rejects its result. Display text is localized on each read and is presentation only; consumers must disable rich-text interpretation for game/mod-authored names.

## Identity and quantities

- `RecipeId` has separate provider/local identities. The vanilla adapter uses provider `vanilla`, local `forge/<native recipe identifier>` or `refining/<native item identifier>`. Preserve the whole opaque local identity; do not parse variant names or construct them from display text. These keys identify definitions, not job instances.
- A Forge subrecipe has its own ID, optional parent ID and rarity. Multiple variants and recipes may produce the same resource. A repeated native availability entry for the same object is coalesced; conflicting objects with the same identity reject the read.
- `RecipeResourceId` distinguishes items, refined materials, equipment templates and item templates. A template identifies possible generated output, not an already-generated item instance. Rarity and `OutputLevelDependsOnPlayer` describe the recipe context; exact output level is not resolved by the definition query.
- Each recipe has input/output collections. Material quantities can be fractional; item/template quantities are integral. Amounts describe one nominal batch, not one output item. `LevelScaled` inputs are base values requiring a context quote before use as actual costs. The adapter does not invoke preview builders, result builders or delivery methods.
- Refining definitions enumerate refinable items with supported refinement data, not only regular ore categories. Outputs are base yields. Skill-dependent bonuses and crystal rewards are not claimed as guaranteed outputs by this catalog.
- Unresolved/unsupported resource rows make a recipe unavailable for default producer lookup. Consumers must not turn partial rows into a claim that the recipe is safe to execute.

The graph is many-to-many. Producer lookup does not recursively expand requirements, pick cheapest routes or solve cycles. Callers choosing to traverse it need their own visited set, limits, route selection and batch rounding. Snapshots are bounded to 16,384 recipes and 256 inputs/outputs per recipe; native collection scans are bounded. Unsupported collection sizes fail rather than silently truncate into a successful catalog.

## Scope and evidence

This service is observational. It does not register custom content, queue/cancel jobs, extract canisters, guarantee output delivery or attach UI. Contextual requirements are provided separately by `ModApi.RecipeQuotes` below. Existing vanilla catalog access does not require custom item/recipe registration. Capability status means inspected bindings resolved, not runtime acceptance.

## Contextual requirements (API 0.1.31)

The same `[Recipes] Enabled` setting enables `ModApi.RecipeQuotes` when its additional inspected bindings succeed. Catalog and quote capabilities are reported separately; quote failure does not disable an otherwise usable catalog.

```csharp
var quotes = ModApi.RecipeQuotes;
var station = quotes?.CurrentStation;
if (station == null || quotes == null) return;
var result = quotes.Quote(station, selectedRecipe.Id, batches: 3);
if (result.Status != RecipeQuoteStatus.Available) return;
// RequirementsMet is an advisory evaluation, not a reservation or a queued job.
foreach (var input in result.Inputs) Show(input.Required, input.Available, input.Missing);
```

Station handles are issued for the current station and can be retained within that session for remote requirements queries. They are not persistent location IDs or general inventory-transfer handles. Unknown, replaced and previous-session handles are refused. A station is checked against the current galaxy before use. CurrentStation returns null when no supported station context can be captured. Read capabilities for integration availability rather than interpreting null as an empty inventory.

Quotes include monotonically increasing read revision, batches, native builder level, credits, input requirements and inventory breakdowns, output estimates, queue usage/capacity, per-batch duration and all observed blockers. Revision identifies a read, not a reservation or a guarantee that skills, stock, queue or location still match. Fixed item outputs retain their inherent level; OutputLevel is the level supplied to generated-output builders. Native ingredient getters supply scaled/rounded quantities. Credits reflect the inspected float-to-integer debit conversion, not an idealized integer multiplication. Repeated ingredient rows share stock and their required quantities are combined.

Inventory purposes remain distinct: player-wide refined materials, station material storage, player armory/data inventory and ship cargo. These are read-only recipe access scopes, not a replacement implementation of the transfer/discovery API tracked in #82. Each station-material balance carries its issued station handle. Unknown accessible inventory yields null availability and an InventoryUnavailable blocker. Inaccessible cargo has no reported amount and is excluded from usable totals; it is not presented as an empty cargo hold. No shop stock is counted.

Forge cargo access follows the matching POI; refining additionally requires the station interior. `RefineryInputPolicy.Manual` includes native manually usable stock. `AutomaticSelection` applies favourite exclusion and the inspected per-stack mission-reservation subtraction, and exposes items disabled for auto-refine as blocked. It describes native selection eligibility, not a guarantee that every native consumption path preserves mission cargo or that station automation is enabled. The policy is recorded in the quote. No setting is changed by querying.

Output amounts are base or conditional estimates for the requested batches. `ProbabilityPerBatch` is the chance on each completed batch; a bonus row's amount is the maximum if that outcome occurs for every requested batch. Crystal identity can be unknown until completion. Current item/template category, station type, cargo capacity and delivery preference inform possible Forge destinations; later batches can route differently. No generated output is instantiated merely to quote it. These are not delivery receipts or exact future floating-point accumulation guarantees.

`QuoteMaterialExtraction(station, refinedMaterialId, count)` estimates credits and canisters without extracting anything. Extraction is immediate, not queued: queue and duration fields are null. Native extraction forces output into cargo; this preview does not authorize bypassing capacity or access checks in a separately supported command.

Host tests exercise duplicate names, multiple producers, template identity, multi-output/fractional quantities, station-specific availability, registry rereads, unresolved outputs, conflicting IDs, session invalidation and failure isolation. Installed-metadata tests verify the declared native member shapes. These do not execute Unity; full recipe/Forge/refining acceptance remains tracked by milestone 10's native gate.
