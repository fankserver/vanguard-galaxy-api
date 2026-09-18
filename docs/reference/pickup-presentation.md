# Item pickup presentation

`ModApi.Services.PickupPresentation` styles existing vanilla item pickup notifications.
This surface is unreleased; it is not available in the public 0.2.8 package.

```csharp
// Keep and dispose the registration when your plugin is destroyed.
registration = ModApi.Services.PickupPresentation.Register(
    "my.mod", pickup => pickup.RarityColor);
```

The payload supplies the actual item's identifier, display name and notification
count. `RarityColor` uses the game's palette; standard rarity returns null. A
resolver returns a `UiColor` or null to abstain. First non-null result wins in
registration order. Duplicate plugin registrations are refused.

Resolvers execute synchronously on the main thread when the float has been shown,
before its first rendered frame. They only select presentation; gameplay mutations
are not permitted in these callbacks. Exceptions are isolated. Removal takes effect
immediately, additions take effect on the next notification, and recursive resolution
falls back to vanilla. Registrations persist across game sessions until disposed.

Only floats produced inside vanilla item pickup are eligible. Vanilla decides
whether the player's pickup is visible, including station-interior suppression.
Credits floats outside item pickup are excluded. The API preserves both the visible
text color and its fade color. It does not change notification merging, cargo amounts,
item-use behavior, or save data.

An unsupported game assembly or failed binding leaves the stable service unavailable;
resolvers do not run. The service does not provide a consumer-side Harmony fallback.
