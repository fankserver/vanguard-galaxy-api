# Mod information

The process-scoped `ModApi.Mods` catalog lists VGModAPI itself and loaded BepInEx plugins declaring a **direct hard or soft dependency** on `vgmodapi`. No extra registration is needed. GUID, name, installed `System.Version`, and dependencies come only from BepInEx. Presence is not successful initialization, gameplay health, compatibility, or update availability.

The API publishes the service in Awake but first snapshots in Start, after loader startup. A consumer explicitly refreshing during its own Awake can see a partial loader inventory; refresh again when opening the screen after startup. Call `Refresh()` on the Unity main thread when opening a view; `Snapshot` is immutable and sorted by GUID. Refresh replaces the snapshot; shutdown clears it and removes the service. Duplicate loader IDs or more than 4096 loader entries refuse refresh, preserving the previous snapshot. A caller must handle refresh failure rather than present an old snapshot as newly refreshed.

This catalog does not depend on inspected game hooks, a loaded save, or successful lifecycle initialization. It cannot discover undeclared/dynamic consumers, disabled plugins, plugins rejected before loading, or preloader patchers. It is not a complete installed-mod list and provides no enable/disable controls. The offline menu adapter (#77) uses this catalog; networking (#78) is not implemented. This catalog and screen perform **no network requests**.

## Offline main-menu screen — native qualification pending

On the inspected assembly, one **Mods** button joins the actual main menu. Opening it refreshes the local catalog and shows name/version rows, selected details, metadata notices, and API capabilities separately from update status. Scroll the list/details or use Previous/Next to browse; Close or Escape dismisses it. Names are bounded/ellipsized in rows, with installed versions in a separate column and full bounded names in details. The list creates only viewport-sized pooled rows, not 4096 Unity objects. The validated ASCII HTTPS destination is a separate plain-text element above author details; only activating **Open project in browser** calls the browser, with the link revalidated at that moment. No remote images, update requests, startup popup, mod toggles, or save operations.

`[ModInformation] MenuEnabled = false` disables attachment without disabling `ModApi.Mods`. The `mod-information-menu` capability and a local warning report binding/input/layout failures independently of lifecycle/save capabilities. A matching hash means attachment was enabled, **not runtime qualification**. Unsupported hashes retain the catalog but do not attach menu UI. Polling reads the exact `MainMenuUI.instance`, native `continueGame`/`versionNumber` style fields, and `AlertPopup.IsOpen`; it never uses a null session as menu detection and adds no Harmony patches.

The owned panel is anchored to the native **Gameview**, ignoring its horizontal layout, not to the fullscreen Canvas. The native `CanvasScaleUpdater`/`WindowMinimumY` keep control of sizing. The fresh button participates in the existing main-menu vertical layout; no native scripts, translations or click listeners are cloned. Font/sprite assets are shared read-only, all labels disable rich text and escape parsing, and only owned objects/listeners are destroyed. The panel raycast backdrop captures its Gameview rectangle. Owned explicit navigation and Tab cycling keep focus in the panel; an existing native popup closes it without stealing popup focus. Normal close restores prior menu focus only when the same menu/EventSystem and an active interactable target remain valid and no popup is open. Inactive/destroyed/replaced menus or viewport/canvas replacement dispose the old view before another attaches. Shutdown clears owned focus/callbacks without changing global input, EventSystem or time scale.

### Evidence and remaining acceptance gates

Read-only native menu census **qa96** passed on the unchanged inspected hash: 1920×1080 display, Gameview approximately 2160×324 canvas units at scale 0.8889, 400×244 menu column with five active buttons, pixel16/font size 16, and `InputSystemUIInputModule`. Inspected menu source confirms Settings/Load deactivate the menu, menu scene unload replaces it, and startup/exit dialogs use `AlertPopup.IsOpen`. This census did **not** execute this adapter. Raw census/receipt remain private; no decompiled source or proprietary DLL is distributed.

Host regressions cover menu/viewport/canvas identity replacement, modal deferral/closure, inactive menu return, shutdown, bounded virtual-row windows, large/empty/stale catalog presentation, plain text and explicit validated links. Installed metadata checks pin all reflected menu fields/modal getter plus installed UI reference types. The UI modules are compile-only local references with explicit hashes in `make provenance`; packages still contain only the three API assemblies.

Issue #77 is **not complete from host checks**. Controlled native qualification and owner acceptance must still exercise: discoverability and six-button layout in bottom/windowed/fullscreen modes; pointer click-through and keyboard/gamepad/Tab/Escape focus; native startup/exit/modal coexistence; scroll clipping/long names/long ASCII hosts; repeated open/close and menu return, menu/canvas replacement, shutdown and partial construction failure; missing/invalid metadata, unsupported hash/config refusal and UI failure isolation. No further game run or deployment was performed for this implementation. Networking/release work (#78/#79) remains out of scope.

## Optional author file

Place `<plugin-guid>.vgmod.json` beside that plugin's **loaded DLL**, resolved from BepInEx `PluginInfo.Location`. For a DLL containing multiple plugins, each GUID has its own file. No directory recursion or assembly loading is performed. Missing metadata leaves a normal name/version row; invalid/unreadable files affect only that row. A replacement DLL location is re-resolved at each refresh.

```json
{
  "schemaVersion": 1,
  "pluginId": "example.author.mod",
  "author": "Example author",
  "description": "An optional plain-text description.",
  "projectUrl": "https://github.com/example/mod",
  "updateUrl": "https://example.org/mod/stable.json",
  "channel": "stable"
}
```

Only `schemaVersion` (the JSON integer `1`) and `pluginId` (exact loader GUID) are required. Other fields, if present, must be nonempty strings. Unknown or duplicate fields, nested objects/arrays, comments, trailing content, invalid UTF-8/Unicode, and unsupported schema versions are rejected. **Do not include installed version**: that is authoritative loader data, not author metadata. Feed versions belong to the separate release publication contract in #79, not this local file.

Limits: 16 KiB while reading, author 256 UTF-16 code units, description 4096, URLs 2048, channel 32. GUID filenames allow 1–128 ASCII letters/digits/dots/underscores/hyphens. Unsafe GUID filenames or unavailable/nonabsolute DLL locations produce a metadata diagnostic, not path traversal or exclusion of the loaded row. Case-only GUID distinctions cannot provide two differently cased sidecar files on a case-insensitive filesystem; the exact `pluginId` match still prevents one owner's metadata being used for the other.

UTF-8 without BOM is required. Channel is `stable` (default) or `experimental`; a channel alone does not enable checking. Project/update URLs require HTTPS/default port, no credentials or fragments, no whitespace/backslashes, and a dotted DNS hostname rather than an IP literal or known local suffix. This is **syntax validation, not DNS isolation**: a DNS name can resolve to a private address. The future transport must independently enforce redirect/host policy, TLS, bounds, consent and privacy. Metadata is plain text; views must disable rich text, show link destinations, and open links only on explicit user action. Unicode format characters are not globally forbidden here: the UI must safely present bidirectional text and destination hosts without allowing metadata to spoof link destinations (#77). Never log full metadata or signed URLs on parse errors.

The parser is a deliberately narrow, bounded flat-JSON implementation with no dependency on game-provided Newtonsoft.Json or System.Text.Json. Host tests cover it; native Mono/UI qualification remains tracked in #80.

## Installed versions

BepInEx uses two-to-four-component numeric `System.Version`, not arbitrary SemVer labels. The catalog preserves the installed representation. Comparisons normalize unspecified build/revision components to zero: `1.2`, `1.2.0`, and `1.2.0.0` compare equally. Do not silently convert `v1.2`, `1.2-beta`, or letter suffixes. Release labels/channels are separate; a higher published number is not proof of game/API/save compatibility.

## Scope and verification

No content/save data is stored by this process inventory. It is independent of mission/POI persistence and cannot broaden game hash support. API, Core and Abstractions remain the only runtime assemblies; metadata files are optional per author. Host checks and builds do not qualify loader timing or Unity behavior. Native evidence belongs to #80, which also retains the owner's full milestone acceptance gate.
