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

Quotes include monotonically increasing read revision, batches, native builder level, credits, input requirements and inventory breakdowns, output estimates, queue usage/capacity, per-batch duration and all observed blockers. Revision identifies a read, not a reservation or a guarantee that skills, stock, queue or location still match. Fixed item outputs retain their inherent level; OutputLevel is the level supplied to generated-output builders. Native ingredient getters supply scaled/rounded quantities. Known credits reflect the inspected float-to-integer debit conversion, not an idealized integer multiplication. Cold native item-cost calculation can indirectly construct builder previews. Quotes therefore read Forge prices only from a custom/cached recipe price or when all ingredient price caches are safe; refinery prices require a safe item price cache. Otherwise `CreditsRequired` is null and `PricingUnavailable` prevents `RequirementsMet`, while ingredient/output information remains available. Queries never prime cold item price caches or open UI to resolve them. Repeated ingredient rows share stock and their required quantities are combined.

Inventory purposes remain distinct: player-wide refined materials, station material storage, player armory/data inventory and ship cargo. These are read-only recipe access scopes, not a general inventory transfer/discovery API. Each station-material balance carries its issued station handle. Unknown accessible inventory yields null availability and an InventoryUnavailable blocker. Inaccessible cargo has no reported amount and is excluded from usable totals; it is not presented as an empty cargo hold. No shop stock is counted.

Forge cargo access follows the matching POI; refining additionally requires the station interior. `RefineryInputPolicy.Manual` includes native manually usable stock. `AutomaticSelection` applies favourite exclusion and the inspected per-stack mission-reservation subtraction, and exposes items disabled for auto-refine as blocked. It describes native selection eligibility, not a guarantee that every native consumption path preserves mission cargo or that station automation is enabled. The policy is recorded in the quote. No setting is changed by querying.

Output amounts are base or conditional estimates for the requested batches. `ProbabilityPerBatch` is the chance on each completed batch; a bonus row's amount is the maximum if that outcome occurs for every requested batch. Crystal identity can be unknown until completion. Current item/template category, station type, cargo capacity and delivery preference inform possible Forge destinations; later batches can route differently. No generated output is instantiated merely to quote it. These are not delivery receipts or exact future floating-point accumulation guarantees.

`QuoteMaterialExtraction(station, refinedMaterialId, count)` estimates credits and canisters without extracting anything. Extraction is immediate, not queued: queue and duration fields are null. Native extraction forces output into cargo; this preview does not authorize bypassing capacity or access checks in a separately supported command. Counts that cannot be represented exactly by native float material arithmetic are refused rather than quoting a different debit from the actual canister count.

## Job observations and save/load

With `[Recipes] Enabled=true`, `ModApi.CraftingJobs` exposes optional `ICraftingJobs` observations when the `crafting-jobs` capability is available. Access and subscription disposal are main-thread-only. Obtain a station handle from `ModApi.RecipeQuotes.CurrentStation`, or from a job event, then call `Read(station)`. Only an `Available` result is a successful queue snapshot; missing definitions, malformed rows and inaccessible/stale stations are not successful empty queues.

`CraftingJobHandle` identifies one native job instance at one issued station in one session. It is not a persistent save identifier. Snapshots copy process/recipe identity, initial/remaining batches, captured Forge level, bounded display progress and per-batch duration. Unknown timing remains null. No native object is exposed.

Subscribe with `Subscribe(pluginId, callback)` and retain/dispose the subscription. Sequence numbers increase during the service lifetime. Callbacks are individually isolated and reentrant notifications are queued. `IsDispatchingCallbacks` permits command integrations to reject reentrant requests. Facts are distinct:

- `Queued`: an actual new queue entry was observed through native `StartJob`, including normal `TryStartJob` callers. This is not a payment or ingredient-consumption receipt. Failed admission without a new job emits no queue fact.
- `BatchObserved`: one native batch call ended. Multiple batches in a tick remain separate. Inspect `DeliveryStatus` and each transfer: remaining count decreases before output delivery and cannot establish successful arrival.
- `Finished`: native processing removed a zero-remaining job. This is not proof that every output arrived, including outputs produced before subscription or save/load.
- `Cancelled`: native cancellation removed the job without an operation exception. Resource refunds are scoped transfer observations; credit refunds are not receipts in this service.
- `OperationFaulted`: cancellation did not establish a clean outcome; effects may be partial. Batch exceptions are represented by an unresolved `BatchObserved` fact. Vanilla exceptions are never suppressed.
- `Invalidated`: the session changed or a known job disappeared without an observed terminal outcome. Do not infer completion or cancellation.

Verified transfers use actual destination quantity changes, not result previews or requested amounts alone. Inventory receipts check the returned row, storage ownership and compatible item identity/level/rarity. Generated equipment does not need to stack or appear in native `GetCount` to be observed. Nested additions are excluded from enclosing deltas. Subscriber work cannot inherit a native scope enclosing callback dispatch; a genuinely new nested job operation has its own scope. Mixed unsupported Forge output shapes keep aggregate batch status unresolved even when some individual transfers are verified. Refined-material receipts preserve actual float-balance changes; a rounded-away addition does not satisfy its requested amount. Unsupported stack effects, currency conversion, unknown identity, exceptions or unresolvable destinations remain explicit. These snapshots are not full generated-item descriptions or a general inventory API. No previews are built by these observers.

Native Forge/refinery jobs already own their supported save data. The API reuses native serialization rather than adding a second serializer: recipe/ore identity, initial and remaining quantity, progress and captured Forge level are restored by the game. Consumers do not implement save callbacks for these fields. Query reconstructed jobs after gameplay initialization; they are not replayed as newly queued. Save-as, rollback and slot replacement may issue new handles while preserving the restored native job meaning. Custom recipe registration/missing-provider migrations are separate from this observer; the API neither invents replacement definitions nor silently filters unresolved queue rows.

Host tests exercise duplicate names, multiple producers, template identity, multi-output/fractional quantities, station-specific availability, registry rereads, unresolved outputs, conflicting IDs, session invalidation and failure isolation. Installed-metadata tests verify the declared native member shapes. These do not execute Unity; full in-game recipe/Forge/refining acceptance remains pending.
