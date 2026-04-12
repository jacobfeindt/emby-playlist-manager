# Emby Playlist Manager – Shadow Playlist System

## Overview

The shadow playlist system maintains a continuously updated JSON copy of every Emby playlist. This enables playlist repair without requiring a manual export — the shadow is always current because it updates in response to live Emby events rather than on a timer.

This is the foundation for:
- **Automatic repair** — detect and fix broken playlist links after library reorganization (Sonarr renames, folder moves, etc.)
- **Scheduled repair** — run as an Emby scheduled task on a configurable interval
- **Cross-server migration** — shadow files are in the same `PlaylistExportDto` format as manual exports, so they work with the existing import feature

---

## Why Not Scheduled Export?

A scheduled export (daily/weekly) would cycle out of history after a Sonarr rename if the rename happens between exports. The shadow approach avoids this because it updates **on every playlist change event**, not on a timer. The shadow is always a current, accurate representation of each playlist's intended contents.

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
    { "Name": "Home Alone", "ImdbId": "tt0099785", "TmdbId": 771 },
    { "Name": "Elf", "ImdbId": "tt0319343", "TmdbId": 10719 }
  ]
}
```

### Event Subscriptions

Subscribe in `Plugin.Run()` (via `IServerEntryPoint` or constructor):

| Event | Source | Action |
|---|---|---|
| `PlaylistItemsAdded` | `IPlaylistManager` | Re-export affected playlist to shadow |
| `PlaylistItemsRemoved` | `IPlaylistManager` | Re-export affected playlist to shadow |
| `PlaylistItemsMoved` | `IPlaylistManager` | Re-export affected playlist to shadow |
| `ItemUpdated` | `ILibraryManager` | Check if item appears in any shadow; if so flag for repair scan |

### Shadow Service

New `ShadowPlaylistService` class:

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
  2. If playlist doesn't exist → skip (or optionally recreate via import)
  3. Get current live items via GetItemList(ListIds = [playlist.InternalId])
  4. Get shadow items
  5. Diff: shadow items not in live items = broken items
  6. For each broken item:
     a. Re-resolve by ImdbId → TmdbId → SeriesTmdbId+Season+Episode
     b. If found in library: AddToPlaylist
     c. If not found: log as still missing
  7. Report: fixed count, still missing count
```

### Identifying broken items

Compare shadow items to live items by provider ID, not by name or internal ID:

```csharp
// Item is present in live playlist if any live item shares a provider ID
bool IsPresent(PlaylistItemDto shadowItem, IEnumerable<BaseItem> liveItems)
{
    return liveItems.Any(live =>
        (!string.IsNullOrEmpty(shadowItem.ImdbId) && live.ProviderIds.TryGetValue("Imdb", out var id) && id == shadowItem.ImdbId) ||
        (shadowItem.TmdbId.HasValue && live.ProviderIds.TryGetValue("Tmdb", out var tid) && tid == shadowItem.TmdbId.Value.ToString()));
}
```

---

## Scheduled Task

Register as an Emby scheduled task so it appears in Dashboard → Scheduled Tasks alongside Backup/Restore.

```csharp
public class PlaylistRepairTask : IScheduledTask
{
    public string Name => "Playlist Manager: Repair Broken Links";
    public string Description => "Scans all playlists for broken item links and repairs them using the shadow playlist store.";
    public string Category => "Playlist Manager";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerWeekly, DayOfWeek = DayOfWeek.Sunday, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }
    };

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress) { ... }
}
```

---

## UI Additions

### New Repair section in PluginOptions

```
[Repair]
  Shadow Folder        — folder picker (where shadow files are stored, default = programdata)
  RepairPreviewList    — GenericItemList showing broken items found across all playlists
  [ Scan for Issues ]  — button: scans without repairing, populates preview list
  [ Repair All ]       — button: repairs all broken links found
```

### Preview list item format

```
playlist_add  Christmas Shows
              Home Alone — will be repaired (found in library)

warning       Christmas Shows  
              Rudolph — still missing (not found in library)
```

---

## Startup Behavior

On plugin load:
1. Subscribe to all playlist and library events
2. If shadow folder is empty, run `InitializeAllShadows` to build initial state from current playlists
3. Log shadow count

---

## Implementation Phases

### Phase 7a — Shadow Infrastructure
- [ ] `ShadowPlaylistService.cs` — read/write/delete shadow files
- [ ] Subscribe to `PlaylistItemsAdded`, `PlaylistItemsRemoved`, `PlaylistItemsMoved` in `Plugin.cs`
- [ ] `InitializeAllShadows` on startup
- [ ] Verify shadow files are created and updated correctly

### Phase 7b — Repair Logic
- [ ] `RepairPlaylists(User)` in `PlaylistService` — diff shadow vs live, re-resolve broken items
- [ ] `IsPresent` provider ID comparison
- [ ] Per-item and per-playlist logging
- [ ] HTTP API: `POST /PlaylistManager/Repair`

### Phase 7c — Scheduled Task
- [ ] `PlaylistRepairTask` implementing `IScheduledTask`
- [ ] Register in Emby scheduled tasks
- [ ] Configurable trigger (default: weekly Sunday 3am)
- [ ] Report-only mode vs auto-repair mode

### Phase 7d — UI
- [ ] Repair section in `PluginOptions`
- [ ] Scan button populates `RepairPreviewList`
- [ ] Repair All button
- [ ] Shadow folder picker in settings

---

## Open Questions

1. **User context for repair** — repair needs a `User` to call `GetItemList`. Should it use the admin user, or the user who owns the playlist? Need to check if playlists are per-user or server-wide in Emby 4.9.3.

2. **Playlist deletion** — when a playlist is deleted in Emby, should the shadow be deleted too, or retained as a recoverable backup?

3. **Shadow on ItemUpdated** — `ILibraryManager.ItemUpdated` fires very frequently during library scans. Need to debounce or filter to only items that appear in shadow playlists to avoid performance impact.

4. **Conflict between repair and import** — if a user imports a playlist with the same name as an existing shadow, should the shadow be updated to reflect the import?
