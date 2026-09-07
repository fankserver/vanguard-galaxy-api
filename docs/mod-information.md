# Mod information

The process-scoped `ModApi.Mods` catalog lists VGModAPI itself and loaded BepInEx plugins declaring a **direct hard or soft dependency** on `vgmodapi`. No extra registration is needed. GUID, name, installed `System.Version`, and dependencies come only from BepInEx. Presence is not successful initialization, gameplay health, compatibility, or update availability.

The API publishes the service in Awake but first snapshots in Start, after loader startup. A consumer explicitly refreshing during its own Awake can see a partial loader inventory; refresh again when opening the screen after startup. Call `Refresh()` on the Unity main thread when opening a view; `Snapshot` is immutable and sorted by GUID. Refresh replaces the snapshot; shutdown clears it and removes the service. Duplicate loader IDs or more than 4096 loader entries refuse refresh, preserving the previous snapshot. A caller must handle refresh failure rather than present an old snapshot as newly refreshed.

This catalog does not depend on inspected game hooks, a loaded save, or successful lifecycle initialization. It cannot discover undeclared/dynamic consumers, disabled plugins, plugins rejected before loading, or preloader patchers. It is not a complete installed-mod list and provides no enable/disable controls. The menu UI and networking are separate delivery issues #77/#78; this catalog performs **no network requests**.

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
