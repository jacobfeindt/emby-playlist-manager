# Emby Playlist Manager Plugin

An Emby Server plugin for managing playlists — export, import, and repair using provider IDs (IMDb/TMDb), making playlists fully portable across systems (e.g., Windows → Linux migrations).

## Features

- Export all playlists to a single portable JSON file
- Import playlists from JSON, resolving items against the local library by IMDb/TMDb ID
- Handles movies, TV series, and individual episodes
- Collision detection — playlists that already exist on the target server are skipped
- Preview imported playlists before committing — shows item counts and collision status
- Export filename includes the source server name and timestamp
- Native Emby plugin UI with folder/file pickers, action buttons, and live status feedback
- HTTP API endpoints for automation and scripting
- Per-item error handling — one unresolvable item never aborts the whole import
- Full activity logging via Emby's logging framework

## Why Provider IDs?

Emby's internal item IDs and file paths change when you migrate to a new server or re-scan a library. By storing IMDb/TMDb IDs in the exported JSON, playlists can be reliably reconstructed on any Emby instance that has the same media — regardless of where files are stored.

## Requirements

- Emby Server 4.9.3+
- .NET 8 runtime (provided by Emby Server)

## Project Structure

```
emby-playlist-export-import/
├── emby-playlist-export-import.csproj
├── Plugin.cs                    # Plugin entry point (BasePlugin + IHasUIPages)
├── PluginOptions.cs             # UI options page
├── PlaylistService.cs           # Core export/import logic
├── Models/
│   ├── PlaylistExportDto.cs     # Top-level export wrapper (one per playlist)
│   └── PlaylistItemDto.cs       # Portable media item (provider IDs)
├── Services/
│   ├── ExportPlaylistsRequest.cs
│   ├── ImportPlaylistsRequest.cs
│   └── PlaylistMigrationApi.cs  # HTTP API service
├── Storage/
│   └── OptionsStore.cs          # Persists plugin settings to disk
├── UI/
│   ├── PageController.cs        # Wires page view into Emby UI system
│   └── PluginPageView.cs        # Button handlers, preview, status updates
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
2. **Export**: Set the output folder, click **Export All Playlists**
   - Creates `{ServerName}-playlists-{timestamp}.json` in the output folder
3. **Import**: Select the JSON file using the file picker
   - A preview list loads automatically showing each playlist, item count, and whether it will be imported or skipped
   - Click **Import Playlists** to create all non-colliding playlists

### Via HTTP API

**Export** — exports all playlists and writes a JSON file to the configured output folder:
```
GET /PlaylistManager/Export
GET /PlaylistManager/Export?OutputFolder=C:\exports
Authorization: MediaBrowser Token="your-api-key"
```

Response:
```json
{ "FilePath": "C:\\exports\\SERVERNAME-playlists-20260411-143022.json", "PlaylistCount": 3 }
```

**Import** — imports playlists from a JSON body, skipping any that already exist by name:
```
POST /PlaylistManager/Import
Authorization: MediaBrowser Token="your-api-key"
Content-Type: application/json

[
  {
    "PlaylistName": "My Movies",
    "PlaylistId": "00000000-0000-0000-0000-000000000000",
    "Items": [
      { "Name": "Inception", "ImdbId": "tt1375666", "TmdbId": 27205 },
      { "Name": "RoboCop", "ImdbId": "tt0093870", "TmdbId": 5548 }
    ]
  }
]
```

Response:
```json
{ "Imported": 1, "Skipped": 0 }
```

## Exported JSON Format

Each export file is an array of playlist objects. A single export covers all playlists on the server.

```json
[
  {
    "PlaylistName": "My Movies",
    "PlaylistId": "1ec3d3ec-997d-bf88-f7ad-49ecaa506c43",
    "Items": [
      {
        "Name": "Inception",
        "ImdbId": "tt1375666",
        "TmdbId": 27205,
        "SeriesTmdbId": null,
        "Season": null,
        "Episode": null
      }
    ]
  },
  {
    "PlaylistName": "TV Favourites",
    "PlaylistId": "1595e602-b5e3-8fe8-30f6-e0186641f2d6",
    "Items": [
      {
        "Name": "Pilot",
        "ImdbId": "tt0620288",
        "TmdbId": null,
        "SeriesTmdbId": 1396,
        "Season": 1,
        "Episode": 1
      }
    ]
  }
]
```

- `PlaylistId` is for reference only — it is never used during import
- Movies/series resolve via `ImdbId` first, then `TmdbId`
- Episodes resolve via `ImdbId` if present, otherwise via `SeriesTmdbId` + `Season` + `Episode`

## Known Limitations

- Export captures provider IDs that Emby has scraped — items without IMDb or TMDb IDs will not resolve on import and will be logged as missing
- Episode `SeriesTmdbId` is only populated if the series was scraped with TMDb; if only IMDb is present the episode still resolves via its own `ImdbId`
- Collision detection is name-based — renaming a playlist before import will allow it through

## SDK Reference

Built against Emby Server 4.9.3.0 (.NET 8) using patterns from the [Emby SDK 4.9.3.0](https://emby.media/community/index.php?/forum/147-developer-api/) sample templates.
