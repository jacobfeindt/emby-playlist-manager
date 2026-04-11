# Emby Playlist Export/Import Plugin – Implementation Plan

## Objective

A fully functional Emby Server plugin that exports and imports playlists using provider IDs (IMDb/TMDb), making playlists portable across systems (e.g., Windows → Linux migrations). Includes a native Emby plugin UI for configuration and triggering operations.

---

## Key Corrections vs. Original Plan

| Original Assumption | Correct SDK Pattern |
|---|---|
| `IServerEntryPoint` as plugin base | `BasePluginSimpleUI<T>` — in namespace `MediaBrowser.Controller.Plugins` |
| `IServerApplicationHost` injected | `IApplicationHost` from `MediaBrowser.Common` |
| `MediaBrowser.Controller.UserData` namespace | Does not exist — `IUserDataManager` is in `MediaBrowser.Controller.Library` |
| `ApiEntryPoint.RegisterEndpoint(...)` | Declare request DTOs with `[Route(...)]` attribute; implement `IService` |
| `_libraryManager.GetItemList(query, user)` | `_libraryManager.GetItemList(query)` — user passed via `InternalItemsQuery(user)` constructor |
| `_userDataManager.CreatePlaylist(...)` | `IPlaylistManager.CreatePlaylist(PlaylistCreationRequest)` |
| `playlist.GetLinkedChildren()` | Does not exist on `Playlist` — query items via `_libraryManager.GetItemList` with `ParentIds = new[] { playlist.InternalId }` |
| `new Playlist { UserId = ... }` | `PlaylistCreationRequest` has `User` (type `User`) not `UserId` |
| `item.Id` for playlist operations | `item.Id` is `Guid` — playlist operations use `item.InternalId` which is `long` |
| `await _playlistManager.AddToPlaylist(...)` | `AddToPlaylist` returns `void`, not `Task` — do not await |
| `MediaBrowser.Model.Playlists` namespace | Does not exist — `PlaylistCreationRequest` / `IPlaylistManager` are in `MediaBrowser.Controller.Playlists` |
| No UI | Plugin UI via `BasePluginSimpleUI<TOptions>` + `EditableOptionsBase` |

---

## Confirmed API Signatures (from MediaBrowser.Controller 4.9.1.90)

```csharp
// IPlaylistManager
Task<PlaylistCreationResult> CreatePlaylist(PlaylistCreationRequest options)
void AddToPlaylist(long playlistId, long[] itemIds, User user)
Task RemoveFromPlaylist(long playlistId, long[] entryIds)
Task MoveItem(long playlistId, long entryId, int newIndex)

// PlaylistCreationRequest properties
string Name
User User          // NOT UserId
long[] ItemIdList
string MediaType
bool IsPublic

// PlaylistCreationResult properties
string Id          // Guid as string — parse with new Guid(result.Id) to look up the item

// BaseItem
Guid Id            // Emby's public item GUID — used with GetItemById(Guid)
long InternalId    // Database row ID — used with playlist operations (AddToPlaylist etc.)

// ILibraryManager
BaseItem GetItemById(Guid id)
BaseItem[] GetItemList(InternalItemsQuery query)

// InternalItemsQuery relevant properties
long[] ParentIds           // NOT ParentId (singular)
long[] AncestorIds
string[] IncludeItemTypes
string[] HasAnyProviderId  // format: "ProviderId:value" e.g. "Imdb:tt1375666"
int? ParentIndexNumber     // season number
int? IndexNumber           // episode number
```

---

## Target Framework & Package

- Target: `net8.0` — Emby Server 4.9.3 is built on .NET 8; `netstandard2.0` does not work because `ReadOnlyMemory<>` lives in `System.Private.CoreLib` in .NET 8, which is not accessible from a `netstandard2.0` project
- References: direct DLL references to `C:\Git\Emby-Server\system\` (not NuGet packages)
- NuGet packages `MediaBrowser.Server.Core` / `MediaBrowser.Common` target `netstandard2.0` and ship assembly version `4.9.1.90` — the actual server runs `4.9.3.0` built on .NET 8; use the server DLLs directly
- Language version: `8.0`
- PostBuild deploy to `%AppData%\Emby-Server\programdata\plugins\` — commented out when no local server install present

---

## Project Structure

```
EmbyPlaylistMigration/
├── emby-playlist-export-import.csproj
├── Plugin.cs                        # BasePluginSimpleUI<PluginOptions>
├── PluginOptions.cs                 # EditableOptionsBase — UI config + action buttons
├── PlaylistService.cs               # Core export/import logic
├── Services/
│   ├── ExportPlaylistRequest.cs     # [Route] GET request DTO
│   ├── ImportPlaylistRequest.cs     # [Route] POST request DTO
│   └── PlaylistApi.cs               # IService implementation
├── Models/
│   └── PlaylistItemDto.cs           # Portable playlist item
└── ThumbImage.png                   # Embedded resource (plugin icon)
```

---

## Dependencies / Interfaces

Injected via constructor (Emby DI):

| Interface | Namespace | Purpose |
|---|---|---|
| `IApplicationHost` | `MediaBrowser.Common` | Resolve other services, paths |
| `ILibraryManager` | `MediaBrowser.Controller.Library` | Query library items |
| `IPlaylistManager` | `MediaBrowser.Controller.Playlists` | Create playlists, add items |
| `IUserManager` | `MediaBrowser.Controller.Library` | Resolve users |
| `ILogManager` | `MediaBrowser.Model.Logging` | Get named logger |

### Usings required in Plugin.cs
```csharp
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;   // BasePluginSimpleUI lives here
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
```

### Usings required in PluginOptions.cs
```csharp
using System.ComponentModel;
using Emby.Web.GenericEdit;              // EditableOptionsBase
using Emby.Web.GenericEdit.Validation;   // ValidationContext
using MediaBrowser.Model.Attributes;     // [Required], [EditFolderPicker]
```

---

## Plugin Entry Point

### Plugin.cs

```csharp
public class Plugin : BasePluginSimpleUI<PluginOptions>, IHasThumbImage
{
    public Plugin(IApplicationHost applicationHost, ILogManager logManager)
        : base(applicationHost)
    {
        _logger = logManager.GetLogger(this.Name);
    }

    public override string Name => "Playlist Export/Import";
    public override string Description => "...";
    public override Guid Id => new Guid("YOUR-GUID-HERE");
    public ImageFormat ThumbImageFormat => ImageFormat.Png;
    public Stream GetThumbImage() => GetType().Assembly
        .GetManifestResourceStream(GetType().Namespace + ".ThumbImage.png");

    protected override void OnOptionsSaved(PluginOptions options) { ... }
}
```

---

## Plugin UI (Options Page)

### PluginOptions.cs — `EditableOptionsBase` via `IHasUIPages` + `PluginPageView`

```
[Export]
  ExportFolder         — folder picker
  [ Export All Playlists ] — exports all playlists to {ServerName}-playlists-{timestamp}.json

[Import]
  ImportFilePath       — text field with [AutoPostBack] — triggers preview on change
  Playlists in File    — GenericItemList preview showing each playlist name, item count,
                         and whether it will be imported or skipped (collision detection)
  [ Import Playlists ] — imports all non-colliding playlists from the file

[Status]
  StatusItem           — live operation feedback
```

### Key UI behaviors
- No playlist name input on import — name comes from the export file
- Preview auto-loads when `ImportFilePath` changes (AutoPostBack)
- Collision detection: playlists already existing by name show as Warning/skipped in preview
- Export filename format: `{ServerFriendlyName}-playlists-{yyyyMMdd-HHmmss}.json`
- Both buttons disabled during in-progress operations
- Preview refreshes after import completes

---

## HTTP API Services

### ExportPlaylistRequest.cs

```csharp
[Route("/PlaylistMigration/Export", "GET")]
[Authenticated(Roles = "Admin")]
public class ExportPlaylistRequest : IReturn<string>
{
    public string PlaylistId { get; set; }
}
```

### ImportPlaylistRequest.cs

```csharp
[Route("/PlaylistMigration/Import", "POST")]
[Authenticated(Roles = "Admin")]
public class ImportPlaylistRequest : IReturn<string>
{
    public string PlaylistName { get; set; }
    public List<PlaylistItemDto> Items { get; set; }
}
```

### PlaylistApi.cs

```csharp
[Authenticated]
public class PlaylistApi : IService
{
    public string Get(ExportPlaylistRequest request) { ... }
    public Task<string> Post(ImportPlaylistRequest request) { ... }
}
```

---

## Data Model

### PlaylistItemDto
Portable representation of a single media item:

```csharp
public class PlaylistItemDto
{
    public string Name { get; set; }
    public string ImdbId { get; set; }
    public int? TmdbId { get; set; }
    public int? SeriesTmdbId { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
}
```

### PlaylistExportDto
Top-level wrapper — a single export file is always `List<PlaylistExportDto>` regardless of whether one or all playlists were exported:

```csharp
public class PlaylistExportDto
{
    public string PlaylistName { get; set; }  // used as the playlist name on import
    public Guid PlaylistId { get; set; }      // reference only, never used on import
    public List<PlaylistItemDto> Items { get; set; }
}
```

- Export single or all → always produces `List<PlaylistExportDto>`
- Import → iterates the list, creates a playlist per entry
- Collision detection: playlists whose name already exists on the target server are skipped
- `PlaylistName` from the export is used directly — no user input needed on import

---

## Core Logic Layer

### PlaylistService.cs

Constructor receives `ILibraryManager`, `IPlaylistManager`, `ILogger`.

#### Export

```
1. GetItemList(InternalItemsQuery { IncludeItemTypes = ["Playlist"] }) — get all playlists
2. For each playlist:
   a. GetItemList({ ParentIds = [playlist.InternalId] }) — get items
   b. Map each item's ProviderIds → PlaylistItemDto
   c. For episodes: reflect SeriesProviderIds["Tmdb"], ParentIndexNumber, IndexNumber
3. Return List<PlaylistExportDto>
```

#### Import

```
1. Caller performs collision detection — only non-colliding playlists passed in
2. For each PlaylistExportDto:
   a. CreatePlaylist({ Name = playlist.PlaylistName, User })
   b. For each PlaylistItemDto (per-item try/catch):
      - Resolve: HasAnyProviderId["Imdb:..."] → ["Tmdb:..."] → series+episode query
      - If found: AddToPlaylist(internalId, [item.InternalId], user)  ← void, no await
      - If not found: log warning
   c. Log per-playlist summary
3. Log overall summary
```

---

## Error Handling

- Never fail entire import due to one bad item — per-item try/catch
- Validate inputs at API layer
- Log all activity: start, per-item result, summary, exceptions

---

## Build & Deployment

- Build output: single DLL (`netstandard2.0`)
- Deploy to: `%AppData%\Emby-Server\programdata\plugins\`
- PostBuild event in `.csproj` copies DLL automatically

---

## Implementation Phases

### Phase 1 — Project Foundation ✅ Complete
- [x] `net8.0` + direct DLL references to Emby Server system folder
- [x] `Plugin.cs` as `BasePlugin` + `IHasUIPages` — GUID: `7b3d3243-e854-4aaf-9636-1505fdd78d6c`
- [x] Full `IHasUIPages` + `PluginPageView` UI pattern
- [x] `PluginOptions.cs` with export/import fields, buttons, status, preview list
- [x] `PlaylistService.cs` with correct API signatures
- [x] Clean build, plugin visible in Emby Dashboard

### Phase 2 — Core Logic ✅ Complete
- [x] `PlaylistExportDto` wrapper — unified format for single and multi-playlist export
- [x] `ExportAllPlaylists` — exports all playlists to `List<PlaylistExportDto>`
- [x] `ImportPlaylists` — creates playlists from `List<PlaylistExportDto>`
- [x] Collision detection — existing playlist names skipped on import
- [x] Preview list — auto-populates via `[AutoPostBack]` when file path changes
- [x] Export filename includes server friendly name: `{ServerName}-playlists-{timestamp}.json`
- [x] Item resolution via `AnyProviderIdEquals` — movies by IMDb/TMDb, episodes by IMDb or series TMDb + S/E
- [x] `createResult.Id` parsed as `long` (InternalId) with Guid fallback
- [x] Transient state (PreviewList, Status) not persisted to disk
- [x] Validated against live Emby instance — Added: 2/2 movies, Added: 1/1 episode, Missing: 0

### Phase 3 — HTTP API ✅ Complete
- [x] `Services/ExportPlaylistsRequest.cs` — GET with optional OutputFolder param
- [x] `Services/ImportPlaylistsRequest.cs` — POST with `List<PlaylistExportDto>` body
- [x] `Services/PlaylistMigrationApi.cs` — resolves Plugin from IApplicationHost
- [x] Collision detection reused from PlaylistService.GetExistingPlaylistNames
- [ ] Test via Emby API browser / Swagger

### Phase 4 — Polish ✅ Complete
- [x] Real `ThumbImage.png` embedded resource
- [x] README updated with full usage, API docs, JSON format, known limitations
