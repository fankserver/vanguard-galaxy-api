# Mod information

The process-scoped `ModApi.Mods` catalog lists VGModAPI itself and loaded BepInEx plugins declaring a **direct hard or soft dependency** on `vgmodapi`. No extra registration is needed. GUID, name, installed `System.Version`, and dependencies come only from BepInEx. Presence is not successful initialization, gameplay health, compatibility, or update availability.

The API publishes the service in Awake but first snapshots in Start, after loader startup. A consumer explicitly refreshing during its own Awake can see a partial loader inventory; refresh again when opening the screen after startup. Call `Refresh()` on the Unity main thread when opening a view; `Snapshot` is immutable and sorted by GUID. Refresh replaces the snapshot; shutdown clears it and removes the service. Duplicate loader IDs or more than 4096 loader entries refuse refresh, preserving the previous snapshot. A caller must handle refresh failure rather than present an old snapshot as newly refreshed.

This catalog does not depend on inspected game hooks, a loaded save, or successful lifecycle initialization. It cannot discover undeclared/dynamic consumers, disabled plugins, plugins rejected before loading, or preloader patchers. It is not a complete installed-mod list and provides no enable/disable controls. The menu uses this catalog without requiring network access. The catalog itself performs **no network requests**. Optional, centralized update checks are separate and default to manual confirmation; automatic checks require opt-in.

## Offline main-menu screen

On the inspected assembly, one **Mods** button joins the actual main menu. The button caption is centered like the native menu entries. Opening it refreshes the local catalog and shows name/version rows, selected details and metadata notices. **Show diagnostics** reveals declared dependencies and API capability details; these are hidden by default. Disabled-by-configuration capability details are not presented as broken mods. Scroll the list/details or use Previous/Next to browse; Close or Escape dismisses it. Names are bounded in rows, with installed versions in a separate column and full bounded names in details. The list creates only viewport-sized pooled rows, not 4096 Unity objects. The validated ASCII HTTPS destination is a separate plain-text element above author details; only activating **Open project in browser** calls the browser, with the link revalidated at that moment. No remote images, startup popup, mod toggles, downloads, installations or save operations. Update actions are explicit and separate from project links.

`[ModInformation] MenuEnabled = false` disables attachment without disabling `ModApi.Mods`. The `mod-information-menu` capability and a local warning report binding/input/layout failures independently of lifecycle/save capabilities. A matching hash means attachment was enabled, **not runtime qualification**. Unsupported hashes retain the catalog but do not attach menu UI. Polling reads the exact `MainMenuUI.instance`, native `continueGame`/`versionNumber` style fields, and `AlertPopup.IsOpen`; it never uses a null session as menu detection and adds no Harmony patches.

The owned panel is anchored to the native **Gameview**, ignoring its horizontal layout, not to the fullscreen Canvas. The native `CanvasScaleUpdater`/`WindowMinimumY` keep control of sizing. The fresh button participates in the existing main-menu vertical layout; no native scripts, translations or click listeners are cloned. Font/sprite assets are shared read-only, all labels disable rich text and escape parsing, and only owned objects/listeners are destroyed. The panel raycast backdrop captures its Gameview rectangle. Owned explicit navigation and Tab cycling keep focus in the panel; an existing native popup closes it without stealing popup focus. Normal close restores prior menu focus only when the same menu/EventSystem and an active interactable target remain valid and no popup is open. Inactive/destroyed/replaced menus or viewport/canvas replacement dispose the old view before another attaches. Shutdown clears owned focus/callbacks without changing global input, EventSystem or time scale.

### Presentation and verification limits

UI-owned text uses ASCII punctuation supported by the native pixel font. Authored Unicode is preserved, except for the documented unsafe direction controls; glyph availability still depends on the installed font. Rich text is never enabled to compensate for missing glyphs.

Host tests cover bounded/stale inventory, explicit links and lifetime policies. Installed metadata checks pin reflected members and compile-only references. Controlled native checks cover virtual keyboard/mouse input, scrolling, modal coexistence, canvas replacement, resizing and shutdown; they do not establish physical gamepad, browser launch or complete in-game acceptance. Full in-game acceptance remains pending.

The package includes `vgmodapi.vgmod.json` beside the API DLL, providing the official project link and description. Missing author metadata is not a load error. The declared optional qualification-guard dependency is loader ordering metadata, not evidence that a guard DLL is installed; declared dependencies remain visible in diagnostics.

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

UTF-8 without BOM is required. Channel is `stable` (default) or `experimental`; a channel alone does not enable checking. Project/update URLs require HTTPS/default port, no credentials or fragments, no whitespace/backslashes, and a dotted DNS hostname rather than an IP literal or known local suffix. This is **syntax validation, not DNS isolation**: a DNS name can resolve to a private address. The update transport independently enforces the exact GitHub host allowlist, HTTPS redirects, TLS verification, bounds and consent described below. Metadata is plain text; views must disable rich text, show link destinations, and open links only on explicit user action. Unicode format characters are not globally forbidden here: the UI must safely present bidirectional text and destination hosts without allowing metadata to spoof link destinations (#77). Never log full metadata or signed URLs on parse errors.

The parser is a deliberately narrow, bounded flat-JSON implementation with no dependency on game-provided Newtonsoft.Json or System.Text.Json. Host tests cover it; native Mono/UI qualification remains tracked in #80.

## Installed versions

BepInEx uses two-to-four-component numeric `System.Version`, not arbitrary SemVer labels. The catalog preserves the installed representation. Comparisons normalize unspecified build/revision components to zero: `1.2`, `1.2.0`, and `1.2.0.0` compare equally. Do not silently convert `v1.2`, `1.2-beta`, or letter suffixes. Release labels/channels are separate; a higher published number is not proof of game/API/save compatibility.

## Optional update checks

`[ModUpdates] Enabled = true` allows manual checks; `Automatic = false` is the default. The **Check update** action first displays network disclosure; pressing it again confirms a request for the selected feed. **Auto: off** similarly requires a separate confirmation before enabling automatic checks globally. Closing or changing selection cancels pending confirmation. Configuration changes can also enable automatic checks explicitly. Disabling stops future checks; already-running requests may finish. Offline inventory and project links do not require the checker.

Requests expose the caller's IP address and requested feed path to configured GitHub hosts and permitted redirect destinations. No saves, profiles, machine identifiers or full mod inventory are transmitted. There are no author network callbacks, credentials, downloads or installations. The only supported feed/redirect hosts are `github.com`, `raw.githubusercontent.com`, `objects.githubusercontent.com` and `release-assets.githubusercontent.com`. Every hop must use validated HTTPS/default port without credentials. At most three redirects are followed. Platform TLS certificate verification stays enabled; cookies, default credentials, system proxy use and automatic redirects are disabled. DNS and OS trust remain external dependencies: this is not a network sandbox and does not defeat hostile DNS/OS configuration. Errors do not log feed contents or signed redirect queries.

The scheduler runs at most two background operations concurrently, with at most one pending request per tracked context and a catalog bound of 4096 entries. Repeat clicks coalesce. Requests have a ten-second total deadline, cancellation and a 16 KiB receiving/parsing limit, including chunked responses. Successful manual checks have a one-minute cooldown; automatic checks repeat after six hours. Failures retry no sooner than fifteen minutes unless rate-limit guidance applies; HTTP 429/503 honors bounded Retry-After (one minute to one day), including shared-origin redirect backoff. Per-mod failures remain separate from gameplay and inventory. Immutable completions are applied only on the Unity thread; disposed services and replaced contexts cannot publish stale results into UI.

Feeds are UTF-8 flat JSON with exactly these fields:

```json
{"schemaVersion":1,"pluginId":"example.mod","channel":"stable","version":"1.2.3","releaseUrl":"https://github.com/example/mod/releases/tag/v1.2.3"}
```

The GUID and channel must match the local metadata; the remote response cannot replace installed identity/version or the configured feed URL. Versions use the numeric comparison above. Channels are `stable` or `experimental`; GitHub's `latest` release excludes prereleases, so experimental feeds should use an explicit/static publication URL rather than assuming latest includes them. A feed version never overrides inspected game hashes or promises compatibility.

Successful data is cached outside saves, in BepInEx's cache directory under `VGModAPI-updates-v1`. SHA-256 context keys include plugin ID, exact configured feed URL, schema and channel, but not installed version: cached versions are compared with the current loader version. At most 128 entries of at most 16 KiB plus a timestamp are retained; entries older than 30 days, future-dated, malformed or context-mismatched are ignored. Cache I/O runs in the background and failures are non-fatal. The screen shows last-success timestamps, cached/stale status and next permitted checks. Failure never means up to date; installed-ahead results never recommend downgrade. Release destinations are shown separately as ASCII HTTPS hosts and open only through **Open release**, with URL validation repeated on activation.

Host fake-transport tests cover the parser, transport, scheduler, cache and confirmation boundaries. Native TLS/runtime and complete updated-control acceptance remain part of #80; host tests alone do not qualify them.

## Scope and verification

No content/save data is stored by this process inventory. It is independent of mission/POI persistence and cannot broaden game hash support. API, Core and Abstractions remain the only runtime assemblies; metadata files are optional per author. Host checks and builds do not qualify loader timing or Unity behavior. Native evidence belongs to #80, where full in-game acceptance remains pending.
