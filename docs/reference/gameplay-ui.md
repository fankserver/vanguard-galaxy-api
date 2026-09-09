# Consumer-owned gameplay UI

Requires API **0.2.8** or later. `ModApi.Services.GameplayUi` is a stable,
Unity-free `IGameplayUiService`. It reports the lifetime of the gameplay UI layer,
not its visibility or universal world readiness. Integration initializes automatically
and refuses uninspected game builds; `Availability` describes binding health.
Session tracking is a required dependency: if it becomes unavailable, the host and
its containers are revoked. A running native UI alone is not sufficient to attach,
so consumers replacing a native-only readiness patch also take on this dependency.

## Readiness and teardown

Subscribe to `Changed`, then query `Current`; subscriptions do not replay. A
`GameplayUiSnapshot` identifies one UI lifetime with `Id` and its tracked `SessionId`.
`Current == null` means there is no usable observed host. It is normal while loading
or at the menu, even with available bindings. No timeout or polling is required to
attach, and no signal is synthesized if native initialization never completes.

- `GameplayUiChange(null, current)` announces readiness.
- `GameplayUiChange(previous, null)` revokes that host. Drop references to its content.
- Replacement delivers teardown before readiness, with a new host identity even
  within the same session. Session replacement invalidates the old host immediately;
  the new session must observe its own UI initialization.
- Late subscribers can use `Current`. Getters do not dispatch callbacks. Live checks
  can make it null before the next teardown notification; old containers refuse access
  immediately rather than waiting for an update.
- All access, creation, disposal and callbacks are main-thread-only. Handlers are
  individually isolated, removals take effect before a handler's turn, and reentrant
  notifications queue. A payload can describe an earlier state: creation always
  validates the expected host against current state.

The inspected boundary is a successful, actually-run `SidePanel.Start`, after its
native transform and idle-status initialization, with the matching singleton, a
screen-space root canvas and a tracked PlayerReady or GameplayInitialized session.
It does not wait for `GameplayManager.Start`, which is a different readiness signal.
Singleton replacement in `SidePanel.Awake`, panel/canvas destruction, session loss,
service faults and shutdown revoke the host. Native lifetime watchers and teardown-only
update checks cover target loss; finding a singleton in an update never creates a host.
Canvas reparenting/replacement without another observed initialization revokes the old
host but does not guess readiness for the replacement.

Hiding or disabling the existing UI does not create a new lifetime. This service
is not a visibility notification; [shared HUD](hud.md) has its own visibility rules.
Directly loading the API mid-session, unsupported session paths and hooks that replace
native behavior do not manufacture UI readiness. See [lifecycle](lifecycle-contract.md)
and [compatibility](compatibility.md).

## Typed Unity containers

UI consumers additionally reference `VGModAPI.Unity.dll` and Unity's CoreModule as
compile-only, importing `VGModAPI.Unity`. The bridge is included in the API package;
do not bundle another copy in your mod. Non-UI consumers only need Abstractions.
No vanilla component or private field is exposed.

```csharp
using VGModAPI.Unity;

var ui = ModApi.Services.GameplayUi;
var host = ui.Current;
if (host == null) return;
var status = ui.CreateContainer(host, pluginId, "windows", out var container);
if (status != GameplayUiContainerStatus.Created) return;

// Retain the lease; this can also happen later, e.g. in a button callback.
if (container!.IsValid)
    myWindow.transform.SetParent(container.Root, false);
// Dispose the container when this mod no longer needs it.
```

A container is an empty, full-stretch `RectTransform` under the existing native root
canvas. It inherits canvas scaling and rendering; it has no graphic, raycast target,
layout group or independent canvas. These constraints apply to the API-owned root,
not its consumer-defined children. Children may add their own canvases,
`GraphicRaycaster` components and `overrideSorting`, including for modal dialogs;
they remain subject to the container's lifetime and cleanup rules.
Do not reparent, resize, destroy or mark the API-owned root persistent. Parent and
manage your content beneath it, including on-demand dialogs. The container does not
provide widgets, modal behavior, focus management, cross-mod window positioning or
automatic restoration of consumer content.

Provider/local identifiers jointly identify a container within one host. Duplicate
identities are refused; independent providers may reuse local IDs. At most 64 live
containers coexist. Disposing a lease frees only its own identity and content. Provider
namespaces coordinate ownership, not security between same-process mods.

| Result | Meaning |
|---|---|
| `Created` | Non-null owned container; all other results return null |
| `Unavailable` | Binding/dependency fault, stopped service or missing native bridge |
| `NoHost` | No current gameplay UI host |
| `StaleHost` | The supplied snapshot no longer identifies the live host, including replacement during creation |
| `DuplicateIdentity` | That provider/local identity is already reserved on this host |
| `LimitReached` | Container capacity reached |
| `CreationFailed` | Construction failed; any partially created owned root is cleaned up |

Null snapshots and malformed identifiers are programming errors and throw. An
unavailable service does not queue creation. A retained snapshot cannot recreate a
lost host; acquire the new `Current` after readiness instead.

`GameplayUiContainer.IsValid` rechecks the host and container. `Root` throws
`ObjectDisposedException` after revocation. Disposal is idempotent, closes access and
disables the root immediately, then uses Unity's deferred destruction for it and all
children. Host invalidation does this for all its containers **before** teardown
notification. A saved raw `RectTransform` cannot be made safe by the API; do not use it
after revocation or move children elsewhere to evade lifetime cleanup. Containers and
snapshots are transient and are never written to saves.

## Launchers and example

Register window launchers with `ModApi.Services.Hud`, rather than assigning custom
icons fixed screen coordinates. The shared corner layouts handle ordering and placement across
mods. Clear session-specific presentation at teardown; HUD registrations and UI
containers deliberately have different lifetimes.

The compiling [GameplayWindow example](https://github.com/fankserver/vanguard-galaxy-api/tree/main/examples/GameplayWindow)
shows subscribe-then-query, a container retained across player actions, a window created
only when its shared HUD button is clicked, and cleanup on host loss and plugin unload.
No Harmony patch, native component lookup or polling is needed in the consumer.

Focused host tests cover lifecycle, callback isolation, reentrancy, ownership and
creation/refusal paths. Build/package checks cover the optional bridge and compiling
consumer, and installed-binding checks verify native member shapes. These checks do
not execute Unity rendering or promise compatibility with arbitrary consumer hooks.
