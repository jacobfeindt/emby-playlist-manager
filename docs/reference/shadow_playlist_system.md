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

### Phase 7a — Shadow Infrastructure
- [ ] `EnableShadow` bool toggle in `PluginOptions` (default: off)
- [ ] `PluginEntryPoint.cs` implementing `IServerEntryPoint` — subscribes to events when enabled
- [ ] `ShadowPlaylistService.cs` — read/write/delete shadow files, `InitializeAllShadows`
- [ ] Shadow stored at `appPaths.DataPath\PlaylistManager\shadows\{PlaylistName}-{PlaylistId}.json`
- [ ] Verify shadow files created and updated correctly on live server
- [ ] Discover actual event args properties at runtime — log `e.Playlist.Name`, `e.Playlist.InternalId`, and any other properties visible in the debugger/logs on first live event, then update the supplemental guide

### Phase 7b — Repair Logic
- [ ] `RepairPlaylists(User)` in `PlaylistService` — diff shadow vs live, re-resolve broken items
- [ ] `IsPresent` provider ID comparison
- [ ] Per-item and per-playlist logging
- [ ] HTTP API: `POST /PlaylistManager/Repair`

### Phase 7c — Scheduled Task
- [ ] `PlaylistRepairTask` implementing `IScheduledTask` (in `MediaBrowser.Model.dll`)
- [ ] Exits immediately if `EnableShadow` is off
- [ ] Default trigger: weekly Sunday 3am
- [ ] Report-only mode vs auto-repair mode

### Phase 7d — UI
- [ ] Shadow section in `PluginOptions` with `EnableShadow` toggle
- [ ] Scan and Repair All buttons (disabled when `EnableShadow` is off)
- [ ] `RepairPreviewList` populated by Scan

---

## Open Questions

1. **Event args properties** — what properties beyond `.Playlist` do `PlaylistItemsAddedEventArgs` etc. carry? Log `e.Playlist.Name` and `e.Playlist.InternalId` on first live event. If additional item ID properties exist they'll appear — update the supplemental guide and switch from full re-export to targeted updates if possible.

2. **Playlist deletion** — `ItemRemoved` fires when a playlist is deleted. Should the shadow be deleted too, or retained as a recoverable backup? Leaning toward retain with a `.deleted` marker.

3. **ItemUpdated debounce** — fires on every library scan item. Filter to only items whose provider IDs appear in any shadow file before doing any work. Consider a short debounce window (e.g. 30s) to batch rapid-fire scan events.

4. **User context for repair** — `GetItemList` needs a `User`. Since playlists are now always imported as public (`IsPublic = true`), repair can use the first admin user. Confirmed: `OwnerUserId` does not exist on `Playlist` — there is no per-playlist owner to look up.
