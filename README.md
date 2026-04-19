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
├── PluginEntryPoint.cs          # IServerEntryPoint — shadow event subscriptions
├── PluginOptions.cs             # UI options page (export, import, shadow, repair)
├── PlaylistService.cs           # Core export/import logic
├── PlaylistRepairTask.cs        # IScheduledTask — weekly automated repair
├── Models/
│   ├── PlaylistExportDto.cs     # Top-level export wrapper (one per playlist)
│   └── PlaylistItemDto.cs       # Portable media item (ProviderIds dictionary)
├── Services/
│   ├── ExportPlaylistsRequest.cs
│   ├── ImportPlaylistsRequest.cs
│   └── PlaylistMigrationApi.cs  # HTTP API service
├── Storage/
│   ├── OptionsStore.cs          # Persists plugin settings to disk
│   └── ShadowPlaylistService.cs # Reads/writes shadow files
├── UI/
│   ├── PageController.cs        # Wires page view into Emby UI system
│   └── PluginPageView.cs        # Button handlers, preview, scan, repair, status
├── UIBaseClasses/               # Boilerplate from Emby SDK UI template
└── ThumbImage.png               # Plugin icon (embedded resource)
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
dotnet build EmbyPlaylistManager.csproj
```

Output: `bin\Debug\net8.0\EmbyPlaylistManager.dll`

The PostBuild step automatically copies the DLL to `%AppData%\Emby-Server\programdata\plugins\` on build.

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

## Developer Reference

Four documents in `docs/reference/` should be read together when working on this project:

| Document | Purpose |
|---|---|
| `docs/reference/implementation_plan.md` | Confirmed API signatures, phase completion status, corrections vs SDK assumptions |
| `docs/reference/shadow_playlist_system.md` | Architecture and implementation plan for the shadow/repair system (Phases 7a-7d) |
| `docs/reference/emby-sdk-primary-guide.md` | Confirmed namespaces, assembly map, and interface signatures for all Emby SDK types used |
| `docs/reference/emby-sdk-supplemental-guide.md` | Patterns, gotchas, and findings specific to this plugin that aren't in the SDK docs |

### Key paths

| Resource | Path |
|---|---|
| This project | `C:\Git\emby-playlist-manager\emby-playlist-manager\` |
| Emby Server DLLs (4.9.3.0, .NET 8) | `C:\Git\Emby-Server\system\` |
| Emby SDK samples + OpenAPI spec | `C:\Git\Emby.SDK-4.9.3.0\Emby.SDK-4.9.3.0\` |

### Why direct DLL references instead of NuGet

Emby Server 4.9.3 runs on .NET 8. The NuGet packages (`MediaBrowser.Server.Core`) target `netstandard2.0` and ship assembly version `4.9.1.90` — incompatible at compile time. All references point directly to the server `system` folder. The plugin must be compiled against the same version the server is running or it will fail to load (`LoaderException`).
