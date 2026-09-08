# Dungeon panel integration

`ModApi.DungeonPanel` exposes Unity-free panel opening, status sections and contextual actions when boarding observation is installed. Check its independent `Capabilities` and the lifecycle capabilities `dungeon-panel-opening`, `dungeon-panel-sections` and `dungeon-panel-actions`. Availability describes installed integration, not in-game qualification.

`Open(target)` resolves the current observed target generation and requires a live target. Opening a native location panel can resume native operations; experimental dungeon recovery must be writable and ready before this API opens it. Ambiguous panel instances, unavailable targets and busy contexts are refused rather than substituted.

## Contributions

Register with a plugin/local identity and retain the returned disposable lease. Identities must be nonblank and at most 128 characters; duplicate pairs are refused. Registrations survive view/session replacement until disposed, but view IDs, revisions and activation contexts do not. Use the presentation callback to return null outside the supported phase. Snapshot compartments contain only already-observed information.

```csharp
IDisposable status = panel.RegisterSection("my.mod", "status", view =>
    new DungeonPanelSection("Operation", view.Operation?.Phase.ToString() ?? "Available"));

// Dispose during plugin shutdown or when withdrawing the contribution.
status.Dispose();
```

Action presenters are reevaluated on activation. A changed view, target generation, snapshot revision, disposed lease or disabled/hidden action refuses dispatch. Gameplay commands must independently validate their current conditions; an enabled button is never authorization. Contributor exceptions are isolated and reported. Calls and lease disposal require the main thread; nested activation/navigation is refused.

Sections accept titles up to 128 characters and text up to 4096. Actions accept labels up to 128 and tooltip text up to 1024. Rendering disables rich-text parsing, wraps text and uses a scrollable region beside, above or below the native panel without covering its controls. Native panel scale and position are respected; keyboard/controller selection scrolls into view. If no unobstructed region fits, contributions are hidden until the window is moved or resized. This constrained-layout limitation is not proof of full viewport acceptance.

## Authored choices and estimates

Supported authored choices appear only after native choice validation permits their discovered compartment and crew context. Event text is shown in full; long choice labels retain their full text beneath the shortened label. Clicking routes through the registered provider's ordinary choice path, including its veto, persistence readiness and current native validation. Applied choices disappear and cannot replay their effects. See [authored content](dungeon-content.md).

The native panel retains its own estimates. Its inspected estimate boundary is already integrated with [boarding tuning rules](boarding-contract.md); the contribution renderer does not create another simulation or apply modifiers again. Native manual/autonomous gameplay continues independently of whether the panel is open.

## Verification boundary

Host tests cover stale views, session changes, registration disposal, callback reentrancy/failure, multiple contributors, long text, authored-choice routing and geometric placement. Installed-binding checks verify panel members and the native estimate boundary. These checks are not Unity acceptance of input, scrolling, scaling, destroyed targets or full restoration. Controlled native qualification remains required; no deployment follows merely from enabling the API.
