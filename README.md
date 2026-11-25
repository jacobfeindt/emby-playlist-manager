# Emby Playlist Manager Plugin

An Emby Server plugin for managing playlists — export, import, and automatic repair using provider IDs (IMDb/TMDb/TVDb), making playlists fully portable across systems (e.g., Windows → Linux migrations).

## Features

### Export / Import
- Export all playlists to a single portable JSON file
- Import playlists from JSON, resolving items by any provider ID Emby has scraped (IMDb, TMDb, TVDb, etc.)
- All provider IDs captured at export — new providers added by Emby in future versions come for free
- Import resolution priority: IMDb → TMDb → any other provider ID, stopping on first match
- Episode fallback: resolves via series TMDb/TVDb ID + season/episode number if no direct item match
- Collision handling — toggle between auto-rename (e.g. `My Playlist (1)`) or overwrite existing
- Preview imported playlists before committing — shows item counts and what will happen to each
- Export filename includes the source server name and timestamp
- All imported playlists created as public (visible to all users)

### Shadow Playlist System
- Maintains a continuously updated JSON copy of every playlist in the background
- Updates automatically on every playlist change event — no manual exports needed
- On first enable, initializes shadow files from all existing playlists
- On every startup, automatically initializes shadows for any new playlists that don't have one yet
- Enables one-click repair after Sonarr/Radarr renames, library moves, or re-scans
- **Scan** shows every playlist — shadowed and unshadowed — with GUID, path, and shadow file info for easy debugging
- **Safe mode** (default): only adds missing items, never removes or replaces
- **Full Restore mode**: treats shadow as authoritative, rebuilds playlist from shadow
- **Repair All** also initializes shadows for any unshadowed playlists found during the run
- Unresolvable items listed with provider IDs for Radarr/Sonarr lookup
- Export missing items to JSON — compatible with Import if media is found later
- Weekly scheduled repair task (Dashboard → Scheduled Tasks → Playlist Manager)
- Shadow backup copies stored in `userplaylists` — included in MBBackup automatically
- On restore, shadow files auto-promoted from backup copies on startup
- Stale shadow files (from deleted/recreated playlists) cleaned up automatically on startup and on write
- Enable/disable toggle — off by default, zero performance impact when disabled

### Sort a Playlist
- Pick one playlist from a dropdown and sort it by **TV order** (series, season, episode; specials last), **air / release date**, **title**, **date added**, or **shuffle**, ascending or descending
- **Preview first**: shows the first 200 items in the new order, where each one is now, and how many moves it takes. Nothing changes until you click **Apply Sort**
- Items with no value for the chosen sort (no air date, no season) always go last, and sorting an already-sorted playlist changes nothing
- Uses the fewest `MoveItem` calls; very large reorders rebuild the playlist instead
- Safe by design: refuses if the playlist changed since the preview, verifies the real order while it works, saves a backup of the previous order (last 10 per playlist, under `PlaylistManager/sort-backups`), and writes the shadow file once at the end
- Only one long-running operation (import, repair, scan, sort, scheduled repair) runs at a time

### Convert a Playlist to a Collection
- A playlist only offers Play. A collection has its own page with artwork, details and cast, so this turns a playlist into a collection **item by item** (it does not put the playlist inside a collection)
- **Preview first**: shows the item types, how many items will be added, and the first 100 of them. Nothing changes until you click **Create Collection**
- The playlist is never changed. Repeated rows count once, because a collection is a set with no order of its own
- If a collection with that name already exists, only the missing items are added to it and nothing is removed, so running it again after the playlist grows is safe
- After adding, the collection is read back and any item Emby refused is listed by name and type
- Every collection the plugin creates or fills is tagged `playlist-theme`, so a themed list can be told apart from a movie-series box set

### Themes
- Organize collections into **named themes** — a theme is a tag applied to a collection, so themed lists (e.g. "Christmas", "Sci-Fi") can be told apart from movie-series box sets
- Manage theme names (add/remove as many as you like) and assign existing collections to a theme, or remove them
- The **Assign Collections** list shows user-created collections by default; tick **Include box sets** to also see auto-created movie-series collections
- Emby's box-set libraries cannot be filtered, so the tag is the reliable grouping mechanism rather than a separate virtual library

### Themes on the Home Screen
- **Shuffle Themes on Home** pins a random selection of your theme collections as Netflix-style rows on the home screen (each row scrolls horizontally)
- Configure the **number of theme rows** (default 8), and optionally **include box sets** in the shuffle pool
- **Randomize item order** (on by default) shows each collection's items in a random order, so the first visible items change every time
- **Shuffle when a user connects** re-shuffles automatically whenever a user or device signs in, so each user gets their own fresh random set

### General
- Native Emby plugin UI with folder/file pickers, action buttons, and live status feedback
- HTTP API endpoints for automation and scripting
- Per-item error handling — one unresolvable item never aborts an operation
- Full activity logging via Emby's logging framework
- Fault tolerant — shadow system never blocks or hangs Emby on failure

## Why Provider IDs?

Emby's internal item IDs and file paths change when you migrate to a new server or re-scan a library. By storing IMDb/TMDb/TVDb IDs in the exported JSON, playlists can be reliably reconstructed on any Emby instance that has the same media — regardless of where files are stored.

## Cross-Platform Migration (e.g. Windows → Linux)

The shadow playlist system makes server migration seamless — no manual export/import needed:

```
Old server
  1. Install plugin, enable Shadow Playlists, save
  2. Let it run — shadows build automatically from live playlists
  3. Run MBBackup

New server
  1. Restore from MBBackup
     - Plugin settings (EnableShadow=true) are restored automatically
     - Shadow backup copies (.shadow.json) are restored into userplaylists
  2. Install plugin — it picks up the restored settings
  3. Restart — shadow files are auto-promoted from backup copies on startup
  4. Click Repair All in the plugin UI
     - Re-resolves every playlist item against the new library by provider ID
     - Handles path changes, Linux vs Windows paths, re-scanned libraries
```

This works because shadow files store provider IDs (IMDb, TMDb, TVDb), not file paths or internal IDs — so they survive any library reorganization or platform change.

## Requirements

- Emby Server 4.9.3+
- .NET 8 runtime (provided by Emby Server)

## Project Structure

```
EmbyPlaylistManager/
├── EmbyPlaylistManager.csproj
├── Plugin.cs                    # Plugin entry point (BasePlugin + IHasUIPages)
├── PluginEntryPoint.cs          # IServerEntryPoint — shadow + home-screen event subscriptions
├── PluginOptions.cs             # Per-page options classes (export/import, shadow, sort, convert, themes)
├── PlaylistService.cs           # Core export/import logic
├── PlaylistQueries.cs           # The one place playlists are read (true order + entry ids)
├── PlaylistSortService.cs       # Sort preview/apply: backup, verify, move or rebuild
├── PlaylistConvertService.cs    # Playlist to collection: preview, add in batches, read back
├── HomeScreenService.cs         # Pin/shuffle theme collections onto the home screen
├── OperationGate.cs             # One long-running operation at a time
├── PlaylistRepairTask.cs        # IScheduledTask — weekly automated repair
├── Models/
│   ├── PlaylistExportDto.cs     # Top-level export wrapper (one per playlist)
│   └── PlaylistItemDto.cs       # Portable media item (ProviderIds dictionary)
├── Sorting/                     # Pure sorting logic, no Emby types (unit-tested)
│   ├── PlaylistSortMode.cs      # Sort modes and direction
│   ├── SortEntry.cs             # Plain-data snapshot of a playlist row
│   ├── NaturalStringComparer.cs # Ordinal natural compare ("Ep 2" before "Ep 10")
│   ├── PlaylistSorter.cs        # Builds the target order
│   └── MovePlanner.cs           # Fewest MoveItem calls to reach it
├── Conversion/
│   └── ConversionPlanner.cs     # Pure helpers: distinct ids, missing ids, batches, name (unit-tested)
├── Services/
│   ├── ExportPlaylistsRequest.cs
│   ├── ImportPlaylistsRequest.cs
│   └── PlaylistMigrationApi.cs  # HTTP API service
├── Storage/
│   ├── OptionsStore.cs          # Persists plugin settings to disk
│   └── ShadowPlaylistService.cs # Reads/writes shadow files
├── UI/                          # One page per feature, grouped under a tabbed main page
│   ├── MainPageController.cs    # Single "Playlist Manager" page with tabs
│   ├── PluginPageViewBase.cs    # Shared view plumbing (gate, user, playlist dropdown)
│   ├── ExportImportPageView.cs
│   ├── ShadowPageView.cs
│   ├── SortPageView.cs
│   ├── ConvertPageView.cs
│   ├── ThemesPageView.cs
│   └── PageController.cs        # One navigable page/tab (name + view factory)
├── UIBaseClasses/               # Boilerplate from Emby SDK UI template
└── ThumbImage.png               # Plugin icon (embedded resource)

tests/
└── PlaylistSorter.Tests/        # Dependency-free test runner for Sorting/ (dotnet run -c Release)
```

## Build Setup

This project references DLLs directly from an Emby Server `system` folder rather than NuGet packages. This is required because Emby Server 4.9.3 is built on .NET 8, while the NuGet packages (`MediaBrowser.Server.Core`) target `netstandard2.0` — the two are incompatible at compile time.

### Prerequisites

1. A copy of the Emby Server `system` folder at `C:\Git\Emby-Server\system`
   - Must contain `MediaBrowser.Common.dll`, `MediaBrowser.Controller.dll`, `MediaBrowser.Model.dll`, `Emby.Web.GenericEdit.dll`
   - Version 4.9.3.0 (.NET 8)
2. .NET 8 SDK

### Building

```bash
dotnet build EmbyPlaylistManager.csproj -c Debug     # copies to %AppData%\Emby-Server\programdata\plugins\
dotnet build EmbyPlaylistManager.csproj -c Release   # copies to U:\emby\plugins\ and U:\emby-remote\plugins\
```

Output: `bin\Debug\net8.0\EmbyPlaylistManager.dll` (Debug) / `bin\Release\net8.0\EmbyPlaylistManager.dll` (Release)

The PostBuild step copies the DLL to the configured plugin folder(s) automatically on build.

## Usage

### Via Plugin UI

1. Open Emby Dashboard → Plugins → Playlist Manager

**Export**
- Set the output folder, click **Export All Playlists**
- Creates `{ServerName}-playlists-{timestamp}.json` in the output folder

**Import**
- Select the JSON file using the file picker — preview loads automatically
- Preview shows each playlist, item count, and what will happen (import / rename / overwrite)
- Toggle **Overwrite Existing Playlists** to control collision behaviour
- Click **Import Playlists**

**Sort a Playlist**
- Choose the playlist (same-named playlists are told apart by the short id in brackets), the **Sort by** mode and the **Direction**
- Click **Preview Sort** — the page lists the first 200 items in the new order and how many items change position
- Click **Apply Sort** to make the change. Apply only works on a fresh preview; changing the playlist, mode or direction invalidates it
- A backup of the previous order is saved to `{DataPath}\PlaylistManager\sort-backups\{playlistGuid}-{timestamp}.json` (the 10 most recent per playlist are kept)
- Sort modes: *TV order* (series name, season, episode; specials after the regular seasons; non-episodes sort by name among the series), *Air / release date* (same-day episodes keep episode order), *Title* (natural order), *Date added*, *Shuffle*

**Convert a Playlist to a Collection**
- Choose the playlist in the **Convert a Playlist to a Collection** section. Leave **Collection name** empty to use the playlist's name
- Click **Preview Conversion**, check the summary, then click **Create Collection**. It only works on a fresh preview; changing the playlist or name invalidates it
- The collection is created with the first batch of items and filled in batches of 100, so large playlists show progress. If Emby refuses a batch, the items are retried one at a time

**Themes**
- Add theme names, then assign them to collections in the **Assign Collections** section (or convert a playlist to tag it automatically)
- Use **Shuffle Themes on Home** to pin random themed collections to the home screen, and toggle the home-screen options (row count, box sets, randomize item order, shuffle on connect) as desired

### Plugin folder locations

| Platform | Plugins folder |
|---|---|
| Windows | `%AppData%\Emby-Server\programdata\plugins\` |
| Linux | `/var/lib/emby/plugins/` |
| Docker | Depends on your volume mapping — typically wherever you map `/config`, e.g. `/config/plugins/` |
| Synology | `/volume1/@appstore/EmbyServer/plugins/` (varies by package) |

> The plugin itself is fully platform-agnostic — all file paths are resolved at runtime from Emby's own path configuration, so shadow files, backup copies, and settings land in the correct location automatically on any platform.

### Shadow Playlists / Repair
- Toggle **Enable Shadow Playlists** on and save — shadow files are written immediately for all playlists
- **Scan for Issues** — shows every playlist (shadowed and unshadowed) with:
  - Short GUID for unambiguous identification when duplicate names exist
  - Shadow filename and playlist folder path for debugging
  - Item counts, GUID match status, and projected action per playlist
  - Unshadowed playlists shown with `ℹ` — run Repair All to initialize, or they track automatically on next change
- **Full Restore Mode** toggle — off by default (Safe)
  - Safe: only adds missing items, never removes or replaces existing items
  - Full Restore: treats shadow as authoritative — clears and rebuilds playlist contents from shadow
- **Repair All** — fixes all issues and initializes shadows for any unshadowed playlists
- **Unresolvable Items** — items that could not be matched in the library after repair, shown with provider IDs for easy Radarr/Sonarr lookup
- **Export Missing Items** — saves unresolvable items to a JSON file in the export folder; compatible with the Import feature if the media is later found on another server
- Automated repair also runs weekly via Dashboard → Scheduled Tasks → Playlist Manager: Repair Broken Links

> **Per-playlist repair:** The scan gives full visibility into each playlist's state. For playlists that need manual intervention, handle them directly in Emby (delete/recreate), then run Repair All — the shadow will be used to restore contents automatically.

### Shadow file storage

Shadow files are stored in two locations:

| Location | Purpose |
|---|---|
| `{DataPath}\PlaylistManager\shadows\*.json` | Master store — read and written by the repair system |
| `{DataPath}\userplaylists\*.shadow.json` | Backup copies — picked up by MBBackup automatically |

On every startup the plugin syncs the two locations:
- If the master folder is empty but backup copies exist (e.g. after a restore), the backup copies are promoted back to master automatically
- Any master file that has no backup copy is copied to `userplaylists` so it will be included in the next backup

This means a full MBBackup → restore cycle preserves shadow history with no manual steps.

### Via HTTP API

**Export** — exports all playlists and writes a JSON file to the configured output folder:
```
GET /PlaylistManager/Export
GET /PlaylistManager/Export?OutputFolder=/exports
Authorization: MediaBrowser Token="your-api-key"
```

Response:
```json
{ "FilePath": "/exports/SERVERNAME-playlists-20260411-143022.json", "PlaylistCount": 3 }
```

**Import** — imports playlists from a JSON body:
```
POST /PlaylistManager/Import
Authorization: MediaBrowser Token="your-api-key"
Content-Type: application/json

[
  {
    "PlaylistName": "My Movies",
    "PlaylistId": "00000000-0000-0000-0000-000000000000",
    "Items": [
      {
        "Name": "Inception",
        "ProviderIds": { "Imdb": "tt1375666", "Tmdb": "27205" },
        "SeriesTmdbId": null, "SeriesTvdbId": null, "Season": null, "Episode": null
      }
    ]
  }
]
```

Response:
```json
{ "Imported": 1, "Skipped": 0 }
```

## Exported JSON Format

Each export file is an array of playlist objects. `ProviderIds` captures all IDs Emby has scraped for the item.

```json
[
  {
    "PlaylistName": "My Movies",
    "PlaylistId": "1ec3d3ec-997d-bf88-f7ad-49ecaa506c43",
    "Items": [
      {
        "Name": "Inception",
        "ProviderIds": { "Imdb": "tt1375666", "Tmdb": "27205" },
        "SeriesTmdbId": null, "SeriesTvdbId": null, "Season": null, "Episode": null
      }
    ]
  },
  {
    "PlaylistName": "TV Favourites",
    "PlaylistId": "1595e602-b5e3-8fe8-30f6-e0186641f2d6",
    "Items": [
      {
        "Name": "Pilot",
        "ProviderIds": { "Imdb": "tt0620288", "Tvdb": "349232" },
        "SeriesTmdbId": 1396, "SeriesTvdbId": 81189, "Season": 1, "Episode": 1
      }
    ]
  }
]
```

- `PlaylistId` is for reference only — never used during import
- Import iterates `ProviderIds` with Imdb first, Tmdb second, then anything else, stopping on first match
- Episode series fallback uses `SeriesTmdbId` or `SeriesTvdbId` + `Season` + `Episode` if no direct item match
- Shadow files use the same format — one file per playlist at `{DataPath}\PlaylistManager\shadows\`

## Known Limitations

- Items with no provider IDs in Emby (unscraped media) will not resolve on import or repair and are logged as missing
- Collision detection is name-based — renaming a playlist before import will allow it through
- File picker pre-populates with the full saved path including filename — clear the filename portion before browsing to a new file
- Shadow system requires the plugin to be running to track changes — playlists modified while the plugin is unloaded will be out of sync until the next Repair All or restart
- Sorting lists playlists visible to the dashboard user. A playlist containing rows that user cannot see is refused rather than risk misplacing items
- Very large reorders rebuild the playlist (its internal row ids change and Emby queues a metadata refresh for the re-added items) instead of moving items one at a time
- *Title* sort uses an ordinal natural comparison, so it can differ in edge cases from the order Emby's own UI shows
- Collections are a set: a playlist's order and repeated rows are not carried over, and Emby decides which item types a collection accepts (anything it refuses is reported after the conversion)
- **The sort feature has only been run on small playlists on a live server.** `docs/reference/sort_playlist.md` lists the behaviours to verify first, and the move-versus-rebuild threshold is provisional until then

## Developer Reference

Five documents in `docs/reference/` should be read together when working on this project:

| Document | Purpose |
|---|---|
| `docs/reference/implementation_plan.md` | Confirmed API signatures, phase completion status, corrections vs SDK assumptions |
| `docs/reference/shadow_playlist_system.md` | Architecture and implementation plan for the shadow/repair system (Phases 7a-7d) |
| `docs/reference/emby-sdk-primary-guide.md` | Confirmed namespaces, assembly map, and interface signatures for all Emby SDK types used |
| `docs/reference/emby-sdk-supplemental-guide.md` | Patterns, gotchas, and findings specific to this plugin that aren't in the SDK docs |
| `docs/reference/sort_playlist.md` | Sort feature design, the Emby playlist API facts it relies on, and the live-server verification checklist |
| `docs/reference/convert_to_collection.md` | Playlist-to-collection design, the Emby collection API facts it relies on, and what to verify on a live server |

### Key paths

| Resource | Path |
|---|---|
| This project | `C:\Git\emby-playlist-manager\emby-playlist-manager\` |
| Emby Server DLLs (4.9.3.0, .NET 8) | `C:\Git\Emby-Server\system\` |
| Emby SDK samples + OpenAPI spec | `C:\Git\Emby.SDK-4.9.3.0\Emby.SDK-4.9.3.0\` |

### Why direct DLL references instead of NuGet

Emby Server 4.9.3 runs on .NET 8. The NuGet packages (`MediaBrowser.Server.Core`) target `netstandard2.0` and ship assembly version `4.9.1.90` — incompatible at compile time. All references point directly to the server `system` folder. The plugin must be compiled against the same version the server is running or it will fail to load (`LoaderException`).
