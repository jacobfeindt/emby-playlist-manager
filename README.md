# Emby Playlist Export/Import Plugin

An Emby Server plugin that exports and imports playlists using provider IDs (IMDb/TMDb) instead of file paths, making playlists fully portable across systems (e.g., Windows → Linux migrations).

## Status

> **Phase 1 Complete** — Project foundation builds cleanly. Core logic and HTTP API in progress.
> See [implementation plan](docs/reference/implementaiton_plan.md) for full phase breakdown.

## Features

- Export playlists to portable JSON (by IMDb/TMDb ID, not file path)
- Import playlists from JSON, resolving items against the local library
- Handles movies, TV series, and individual episodes
- Native Emby plugin UI for configuring export/import paths and triggering operations
- HTTP API endpoints for automation
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
├── Plugin.cs                  # Plugin entry point (BasePluginSimpleUI<PluginOptions>)
├── PluginOptions.cs           # UI options page (export/import config)
├── PlaylistService.cs         # Core export/import logic
├── Services/                  # HTTP API (Phase 3)
│   ├── ExportPlaylistRequest.cs
│   ├── ImportPlaylistRequest.cs
│   └── PlaylistApi.cs
├── Models/
│   └── PlaylistItemDto.cs     # Portable playlist item DTO
└── ThumbImage.png             # Plugin icon (embedded resource)
```

## Build Setup

This project references DLLs directly from an Emby Server `system` folder rather than NuGet packages. This is required because Emby Server 4.9.3 is built on .NET 8, while the NuGet packages (`MediaBrowser.Server.Core`) target `netstandard2.0` — the two are incompatible at compile time.

### Prerequisites

1. A copy of the Emby Server `system` folder at `C:\Git\Emby-Server\system`
   - Contains `MediaBrowser.Common.dll`, `MediaBrowser.Controller.dll`, `MediaBrowser.Model.dll`, `Emby.Web.GenericEdit.dll`
   - Version 4.9.3.0 (.NET 8)

2. .NET 8 SDK

### Building

```bash
dotnet build emby-playlist-export-import.csproj
```

Output: `bin\Debug\net8.0\EmbyPlaylistMigration.dll`

### Deployment

Copy `EmbyPlaylistMigration.dll` to the Emby Server plugins folder:

```
%AppData%\Emby-Server\programdata\plugins\
```

The PostBuild copy step in the `.csproj` is commented out by default since this project is developed on a workstation without a local Emby Server install. Uncomment it to enable automatic deploy on build.

## Usage

### Via Plugin UI

1. Open Emby Dashboard → Plugins → Playlist Export/Import
2. **Export**: Enter the playlist ID and output folder path, save
3. **Import**: Enter the path to a previously exported JSON file and a name for the new playlist, save

### Via HTTP API (Phase 3)

**Export:**
```
GET /PlaylistMigration/Export?PlaylistId={guid}
Authorization: MediaBrowser Token="..."
```

**Import:**
```
POST /PlaylistMigration/Import?PlaylistName=MyPlaylist
Content-Type: application/json
Authorization: MediaBrowser Token="..."
```

## Exported JSON Format

```json
[
  {
    "Name": "Inception",
    "ImdbId": "tt1375666",
    "TmdbId": 27205,
    "SeriesTmdbId": null,
    "Season": null,
    "Episode": null
  },
  {
    "Name": "Ozymandias",
    "ImdbId": null,
    "TmdbId": null,
    "SeriesTmdbId": 1396,
    "Season": 5,
    "Episode": 14
  }
]
```

## SDK Reference

Built against Emby Server 4.9.3.0 (.NET 8) using patterns from the [Emby SDK 4.9.3.0](https://emby.media/community/index.php?/forum/147-developer-api/) sample templates.
