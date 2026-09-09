# Owned items

`ModApi.Services.Items` registers authenticated, provider-scoped **plain stackable trade goods**. Acquire a provider lease directly from the loaded plugin assembly, then register immutable `OwnedItemDefinition` records. Duplicate local IDs within one provider are refused; different providers may use the same local ID.

## Supported shape

Items have a name, description, vanilla-item icon reference, positive volume, nonnegative base cost and explicit materials/armory storage routing. Native category is TradeGoods with standard rarity. No vanilla enum value or global category translation is reserved for a provider. They are sellable/jettisonable goods, not equippable modules or executable usable-item components. They can participate in native inventory/storage and later recipe inputs/outputs. Equipment, custom use scripts, arbitrary components, custom rarity and mutable per-item gameplay fields are unsupported.

Unity construction, category/storage backing fields, component initialization and catalog hooks remain inside the adapter. Registering before the native catalog loads queues the declaration. Catalog clears reinsert the existing owned hosts rather than allocating duplicate objects. Missing icon assets or conflicting catalog entries refuse construction.

## Persistence and revisions

Supported immutable fields are encoded in a bounded, canonical native item identifier. Vanilla's ordinary item serializer saves that identifier; vanilla inventory serialization retains quantities and favourite/stack distinctions. There is no second quantity ledger or author-provided save callback. Deserialization reconstructs the exact stored definition before vanilla resolves the item.

The missing-content policy for this non-executable shape is **API reconstruction**: the provider may be absent, but the API and referenced vanilla icon must be available. No provider code is invoked from saved data. Invalid/unsupported encodings refuse instead of falling through to vanilla's unknown-item skipping.

Revisions coexist. A new registration can produce a new definition after its old provider lease is released; existing items retain their original fields and identity. Older saves and slot changes therefore retain their own item values, not whichever revision was most recently registered. There is no automatic quantity-merging, aliasing or retroactive rewrite of existing inventory. Migration to executable or different item shapes is not supported.

Any snapshot containing owned item references receives the API-required save-version barrier, including saves with no owned world sites. The load guard validates/reconstructs item references before removing the barrier in memory. It does not change the saved file or bypass a genuinely newer game version. Missing API support must not silently turn these items into vanilla goods. Safe uninstall is not promised.

Provider disposal stops new declarations; it does not destroy objects retained by live inventories. Shutdown retains reserved-identifier refusal hooks and does not destroy live-session hosts. Public references are provider/local identities, not Unity objects or permission to mutate inventory. Recipe authoring and inventory operations use their respective services.
