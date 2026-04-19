# Emby Playlist Manager – Shadow Playlist System

## Context for a fresh session

Read these four documents together before implementing:

| Document | Path |
|---|---|
| Implementation Plan | `C:\Git\emby-playlist-manager\docs\reference\implementation_plan.md` |
| SDK Primary Guide | `C:\Git\emby-playlist-manager\docs\reference\emby-sdk-primary-guide.md` |
| SDK Supplemental Guide | `C:\Git\emby-playlist-manager\docs\reference\emby-sdk-supplemental-guide.md` |
| This document | `C:\Git\emby-playlist-manager\docs\reference\shadow_playlist_system.md` |

The primary guide contains confirmed namespaces, assembly map, and all interface signatures. The supplemental guide contains gotchas, patterns, and plugin-specific findings. Do not re-derive these from scratch.

---

## Overview

The shadow playlist system maintains a continuously updated JSON copy of every Emby playlist. It runs silently in the background and is the foundation for automatic playlist repair.

### How it works

1. When the plugin loads, it subscribes to Emby's playlist change events (`PlaylistItemsAdded`, `PlaylistItemsRemoved`, `PlaylistItemsMoved`)
2. Every time a playlist is modified, the shadow file for that playlist is rewritten with the current contents — capturing all provider IDs (IMDb, TMDb, TVDb, etc.) for every item
3. On first enable, all existing playlists are exported to shadow files to build the initial state
4. Shadow files live at `{DataPath}\PlaylistManager\shadows\{PlaylistName}-{PlaylistId}.json` and use the same `PlaylistExportDto` format as manual exports

### How repair works

When Sonarr renames a file, Emby re-scans and creates a new library item for it — but the old item that was in your playlist is now a dead link. The shadow file still has the provider IDs for every item that *should* be in the playlist.

Repair:
1. Compares each shadow file against the live playlist by provider ID
2. Items in the shadow but missing from the live playlist are "broken"
3. Re-resolves each broken item against the current library using its provider IDs
4. Adds the newly resolved item back to the playlist
5. Reports what was fixed and what is still missing (media not in library)

Repair can be triggered manually via the UI or run automatically on a schedule via Emby's scheduled task system.

### Enabling the shadow system

The shadow system is **off by default**. Enable it in the plugin UI under the **Shadow Playlists** section:

```
[Shadow Playlists]
  Enable Shadow Playlists  — toggle this on to activate background tracking and repair
```

When off: no events are subscribed, no files are written, scan/repair buttons are disabled, and the scheduled task exits immediately without doing anything.

When turned on for the first time: all existing playlists are exported to shadow files automatically.

---

## Architecture

### Shadow Storage

One JSON file per playlist, stored at:
```
%AppData%\Emby-Server\programdata\data\PlaylistManager\shadows\{PlaylistName}-{PlaylistId}.json
```

Each file is a single `PlaylistExportDto` object (not a list — one file per playlist):

```json
{
  "PlaylistName": "Christmas Shows",
  "PlaylistId": "1ec3d3ec-997d-bf88-f7ad-49ecaa506c43",
  "Items": [
    { "Name": "Home Alone", "ProviderIds": { "Imdb": "tt0099785", "Tmdb": "771" } },
    { "Name": "Elf", "ProviderIds": { "Imdb": "tt0319343", "Tmdb": "10719" } }
  ]
}
```

### Event Subscriptions

Subscribe in `IServerEntryPoint.Run()` — confirmed correct pattern (see supplemental guide):

| Event | Source | Confirmed In | Action |
|---|---|---|---|
| `PlaylistItemsAdded` | `IPlaylistManager` | `Emby.Server.Implementations.dll` | Re-export affected playlist to shadow |
| `PlaylistItemsRemoved` | `IPlaylistManager` | `Emby.Server.Implementations.dll` | Re-export affected playlist to shadow |
| `PlaylistItemsMoved` | `IPlaylistManager` | `Emby.Server.Implementations.dll` | Re-export affected playlist to shadow |
| `ItemAdded` | `ILibraryManager` | `Emby.Server.Implementations.dll` | Check if new item resolves any missing shadow entries |
| `ItemUpdated` | `ILibraryManager` | `Emby.Server.Implementations.dll` | Same — but fires very frequently, must debounce/filter |

Event args carry a `.Playlist` property (confirmed via XML docs). Additional properties (e.g. which item IDs changed) are undocumented — discover at runtime by logging the args object on first run.

Note: `Plugin.Run()` via constructor is **not** the right pattern — use `IServerEntryPoint.Run()` as a separate class.

### Shadow Service

New `ShadowPlaylistService` class. Constructor should receive `IApplicationHost` (to resolve `IApplicationPaths` and `IJsonSerializer`) and `ILogger` — see supplemental guide § *Resolve additional services at runtime* and § *Plugin paths* for the `DataPath` pattern.

```csharp
public class ShadowPlaylistService
{
    // Write shadow for a single playlist
    void UpdateShadow(User user, BaseItem playlist)

    // Write shadows for all playlists (called on startup)
    void InitializeAllShadows(User user)

    // Read all shadow files
    List<PlaylistExportDto> GetAllShadows()

    // Read shadow for a specific playlist by name
    PlaylistExportDto GetShadow(string playlistName)

    // Delete shadow when playlist is deleted
    void DeleteShadow(string playlistName, Guid playlistId)
}
```

---

## Repair Logic

### What "broken" means

An item is broken when it appears in the shadow but is missing from the live Emby playlist. This happens when:
- A file was moved or renamed and Emby re-scanned (item gets a new path but same provider IDs)
- Sonarr/Radarr renames a file as part of library organization
- A library root path changes

### Repair algorithm

```
For each shadow file:
  1. Find the matching live playlist by name
  2. If playlist doesn't exist → skip
  3. Get current live items via GetItemList(ListIds = [playlist.InternalId])
  4. Diff: shadow items not present in live items (by provider ID) = broken items
  5. For each broken item:
     a. Iterate ProviderIds (Imdb first, Tmdb second, then anything else)
     b. If found in library: AddToPlaylist
     c. If not found: log as still missing
  6. Report: fixed count, still missing count
```

### Identifying broken items

Compare shadow items to live items by provider ID, not by name or internal ID:

```csharp
bool IsPresent(PlaylistItemDto shadowItem, IEnumerable<BaseItem> liveItems)
{
    return liveItems.Any(live =>
        shadowItem.ProviderIds.Any(kvp =>
            live.ProviderIds.TryGetValue(kvp.Key, out var val) && val == kvp.Value));
}
```

---

## Fault Tolerance

The shadow system must never impact Emby's normal operation. It is a background observer — if it fails, Emby carries on.

- All event handlers wrap their entire body in `try/catch` — exceptions are logged as `Error` and swallowed
- All file I/O (shadow reads/writes) is wrapped in `try/catch` — a corrupt or locked shadow file must not abort the handler
- Shadow writes are fire-and-forget where possible — use `Task.Run(() => { try { ... } catch { ... } })` to get off the event thread immediately
- `InitializeAllShadows` on startup runs on a background thread — never blocks plugin load
- Repair operations (scan and execute) run on background threads — never block the UI or event thread
- `ItemUpdated` debounce must not accumulate unbounded work — cap the pending queue and drop excess events
- If the shadow folder is missing or unwritable, log a single `Error` on startup and disable the shadow system gracefully rather than retrying on every event

---

## Logging

All shadow and repair activity logs through Emby's `ILogger` under the plugin name. Levels:

### Shadow updates (on every playlist event)
```
Info  Shadow updated: '{PlaylistName}' ({N} items) [triggered by PlaylistItemsAdded]
Info    + Added:   'Item Name' [Imdb=tt1234567, Tmdb=12345]
Info    - Removed: 'Item Name' [Imdb=tt1234567]
Info    ~ Moved:   'Item Name' (index 2 → 5)
Info  Shadow initialized: {N} playlists written on startup
```

### Repair scan
```
Info  Repair scan started. {N} shadow files found.
Info  Playlist 'Christmas Shows': {N} live items, {N} shadow items
Info    Broken: 'Home Alone' — not in live playlist [Imdb=tt0099785, Tmdb=771]
Info    OK:     'Elf' — present in live playlist
Info  Playlist 'Christmas Shows': {broken} broken, {ok} ok
Info  Repair scan complete. {total_broken} broken items across {N} playlists.
```

### Repair execution
```
Info  Repair started. {N} broken items to fix.
Info    Fixed:   'Home Alone' — re-resolved via Imdb=tt0099785, added to 'Christmas Shows'
Warn    Missing: 'Rudolph' — not found in library [Imdb=tt0060345, Tmdb=11360]
Info  Repair complete. Fixed: {N}, Still missing: {N}.
```

### Event discovery (Phase 7a only — remove after confirming args)
```
Debug PlaylistItemsAdded args: Playlist='{name}' InternalId={id} [log any other visible properties]
```

---

## Scheduled Task

See supplemental guide § *IScheduledTask* for the full confirmed signature including `Key` property and `TaskTriggerInfo` constants. `IScheduledTask` is in `MediaBrowser.Model.dll`.

```csharp
public class PlaylistRepairTask : IScheduledTask
{
    public string Name => "Playlist Manager: Repair Broken Links";
    public string Description => "Scans all playlists for broken item links and repairs them using the shadow playlist store.";
    public string Category => "Playlist Manager";
    public string Key => "PlaylistManagerRepair";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerWeekly, DayOfWeek = DayOfWeek.Sunday, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }
    };

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress) { ... }
}
```

---

## UI

### Shadow section in PluginOptions

```
[Shadow Playlists]
  EnableShadow         — bool toggle (default: off) — master on/off for the entire shadow system
  RepairPreviewList    — GenericItemList showing broken items found across all playlists
  [ Scan for Issues ]  — button: scans without repairing, populates preview list
  [ Repair All ]       — button: repairs all broken links found
```

When `EnableShadow` is off:
- No event subscriptions are made
- No shadow files are written
- Scan and Repair buttons are disabled
- Scheduled task still appears in Dashboard but exits immediately if shadow is disabled

---

## Startup Behavior

On plugin load (in `IServerEntryPoint.Run()`):
1. Check `EnableShadow` — if off, return immediately
2. Subscribe to `PlaylistItemsAdded`, `PlaylistItemsRemoved`, `PlaylistItemsMoved`, `ItemAdded`, `ItemUpdated`
3. If shadow folder is empty, run `InitializeAllShadows` to build initial state from current playlists
4. Log shadow count

---

## Implementation Phases

### Phase 7a — Shadow Infrastructure ✅ Complete
- [x] `EnableShadow` bool toggle in `PluginOptions` (default: off)
- [x] `PluginEntryPoint.cs` implementing `IServerEntryPoint` — subscribes to events when enabled
- [x] `ShadowPlaylistService.cs` — read/write/delete shadow files, `InitializeAllShadows`
- [x] Shadow stored at `appPaths.DataPath\PlaylistManager\shadows\{PlaylistName}-{PlaylistId}.json`
- [x] All event handlers wrapped in `try/catch` — exceptions logged and swallowed, never rethrown
- [x] Shadow writes via `Task.Run` — off the event thread immediately
- [x] `InitializeAllShadows` runs on background thread — never blocks plugin load
- [x] Shadow folder missing/unwritable — logs single `Error`, disables gracefully
- [x] Log shadow update per event: playlist name, item count, trigger event name
- [x] Log `Debug` line on each event firing with playlist name and InternalId (event discovery)
- [ ] Deploy to live server — verify shadow files created and updated correctly
- [ ] After first live event: confirm args properties, update supplemental guide, remove debug logging

### Phase 7b — Repair Logic ✅ Complete
- [x] `HandleScan` in `PluginPageView` — diffs shadow vs live, populates `RepairPreviewList`
- [x] `HandleRepairAll` in `PluginPageView` — re-resolves broken items, adds back to playlist
- [x] `IsPresent` provider ID comparison (any key match)
- [x] Per-item logging: broken/ok/fixed/still-missing with all provider IDs
- [x] Per-playlist and overall summary logging
- [ ] HTTP API: `POST /PlaylistManager/Repair`

### Phase 7c — Scheduled Task ✅ Complete
- [x] `PlaylistRepairTask` implementing `IScheduledTask` (`MediaBrowser.Model.Tasks`)
- [x] Resolves `Plugin` via `IApplicationHost` — exits immediately if `EnableShadow` is off
- [x] Default trigger: weekly Sunday 3am
- [x] Per-playlist and per-item logging matching the logging spec
- [x] `CancellationToken` respected — per-playlist and per-item checks
- [x] `progress.Report` at each playlist step
- [x] Per-playlist `try/catch` — one bad playlist never aborts the whole run
- [x] `OperationCanceledException` re-thrown correctly

### Phase 7d — UI ✅ Complete
- [x] Shadow section in `PluginOptions` with `EnableShadow` toggle
- [x] `RepairPreviewList`, `ScanButton`, `RepairAllButton`, `ShadowStatus`
- [x] Scan and Repair All buttons disabled during in-progress operations
- [x] `OnSaveCommand` reflects `EnableShadow` state in `ShadowStatus`

### Phase 7e — Missing Items Report & Scan Intelligence ✅ Complete

#### Repair Mode
A global `Full Restore Mode` toggle in the Shadow section of `PluginOptions`:
- **Off (Safe, default)** — only adds missing items, never removes or replaces existing items
- **On (Full Restore)** — treats shadow as authoritative: clears live playlist contents and rebuilds from shadow

Per-playlist repair control is intentionally not implemented. The scan gives full visibility into each playlist's state. Playlists that need manual intervention should be handled directly in Emby (delete/recreate), then Repair All will pick them up from the shadow. This keeps the tool powerful without requiring interactive per-row UI controls that GenericEdit doesn't support natively.

#### Enhanced Scan List
Each scan result row shows the full situation per playlist:
- GUID match status (exact match, name-only match, GUID mismatch)
- Shadow item count vs live item count
- Number of items missing from live playlist
- In Full Restore mode: number of live items not in shadow (will be removed)
- Projected action: `→ Safe: add N missing items` or `→ Full Restore: replace live contents with shadow`

#### Missing Items Report
- [x] `MissingItemsList` — populated after Repair All with items that could not be resolved, persists across saves
- [x] Grouped by playlist, shows item name + all provider IDs
- [x] **Export Missing Items** button — writes `{ServerName}-missing-items-{timestamp}.json` to the export folder
- [x] Export format is `List<PlaylistExportDto>` — compatible with import if items are later found on another server
- [ ] Missing items report written automatically after scheduled repair task runs

---

## Open Questions

1. **Event args properties** — what properties beyond `.Playlist` do `PlaylistItemsAddedEventArgs` etc. carry? Log `e.Playlist.Name` and `e.Playlist.InternalId` on first live event. If additional item ID properties exist they'll appear — update the supplemental guide and switch from full re-export to targeted updates if possible.

2. **Playlist deletion** — `ItemRemoved` fires when a playlist is deleted. Should the shadow be deleted too, or retained as a recoverable backup? Leaning toward retain with a `.deleted` marker.

3. **ItemUpdated debounce** — fires on every library scan item. Filter to only items whose provider IDs appear in any shadow file before doing any work. Consider a short debounce window (e.g. 30s) to batch rapid-fire scan events.

4. **User context for repair** — `GetItemList` needs a `User`. Since playlists are now always imported as public (`IsPublic = true`), repair can use the first admin user. Confirmed: `OwnerUserId` does not exist on `Playlist` — there is no per-playlist owner to look up.

5. **Shadow backup copy** — resolved. Master store is `data\PlaylistManager\shadows\`. Backup copies written to `data\userplaylists\*.shadow.json` on every update — confirmed picked up by MBBackup. On startup, if master is empty but backup copies exist (post-restore), they are automatically promoted back to master. Sync also runs on startup to ensure all masters have a backup copy.
