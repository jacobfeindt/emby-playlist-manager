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

### PluginOptions.cs — `EditableOptionsBase`

Fields rendered automatically by Emby's GenericEdit UI framework:

```
[Section: Export]
  ExportFolderPath     — [EditFolderPicker] folder picker
  ExportPlaylistId     — text field (GUID of playlist to export)
  ExportButton         — ButtonItem triggers export

[Section: Import]
  ImportFilePath       — text field (path to JSON file)
  ImportPlaylistName   — text field (name for new playlist)
  ImportButton         — ButtonItem triggers import

[Status]
  StatusItem           — shows last operation result
```

> For live status feedback, upgrade to the full `IHasUIPages` + `PluginPageView` pattern from `EmbyPluginUiTemplate`.

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

```csharp
public class PlaylistItemDto
{
    public string Name { get; set; }
    public string ImdbId { get; set; }
    public int? TmdbId { get; set; }

    // Episode-specific
    public int? SeriesTmdbId { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
}
```

- Movies/Series: resolve via `ImdbId` → `TmdbId`
- Episodes: resolve via `SeriesTmdbId` + `Season` + `Episode`
- Never store file paths or internal Emby IDs

---

## Core Logic Layer

### PlaylistService.cs

Constructor receives `ILibraryManager`, `IPlaylistManager`, `ILogger`.

#### Export

```
1. _libraryManager.GetItemById(playlistGuid) — cast to Playlist
2. _libraryManager.GetItemList(new InternalItemsQuery(user) { ParentIds = new[] { playlist.InternalId } })
3. For each item: map ProviderIds → PlaylistItemDto
4. For episodes: reflect to get SeriesProviderIds["Tmdb"], ParentIndexNumber, IndexNumber
5. Serialize to JSON, write to ExportFolderPath
6. Return List<PlaylistItemDto>
```

#### Import

```
1. await _playlistManager.CreatePlaylist(new PlaylistCreationRequest { Name, User })
2. Parse result.Id as Guid → _libraryManager.GetItemById(guid) → playlist.InternalId
3. For each PlaylistItemDto (wrapped in try/catch):
   a. Resolve: HasAnyProviderId["Imdb:..."] → ["Tmdb:..."] → series lookup + episode query
   b. If found: _playlistManager.AddToPlaylist(playlist.InternalId, new[] { item.InternalId }, user)
      NOTE: AddToPlaylist returns void — do NOT await
   c. If not found: log warning
4. Log summary
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
- [x] Recreate `.csproj` targeting `net8.0` with direct DLL references to Emby Server system folder
- [x] Implement `Plugin.cs` as `BasePluginSimpleUI<PluginOptions>` — GUID: `7b3d3243-e854-4aaf-9636-1505fdd78d6c`
- [x] Implement `PluginOptions.cs` with export/import fields
- [x] Implement `PlaylistService.cs` with correct API signatures
- [x] Clean build — 0 errors, 0 warnings
- [x] Plugin loads and appears in Emby Dashboard — confirmed in log and UI
- [x] Assembly version 1.0.0.0 visible in Emby plugins list

### Phase 2 — Core Logic
- [ ] Validate `PlaylistService` export/import against a live Emby instance
- [ ] Confirm `HasAnyProviderId` query format works for IMDb/TMDb lookups
- [ ] Confirm episode resolution via `ParentIds` + `ParentIndexNumber` + `IndexNumber`

### Phase 3 — HTTP API
- [ ] Implement `Services/ExportPlaylistRequest.cs`
- [ ] Implement `Services/ImportPlaylistRequest.cs`
- [ ] Implement `Services/PlaylistApi.cs`
- [ ] Test endpoints via Emby API browser

### Phase 4 — UI Enhancement
- [ ] Add `ButtonItem` fields to `PluginOptions` for triggering export/import
- [ ] Add `StatusItem` for operation feedback
- [ ] If live progress needed: upgrade to full `IHasUIPages` + `PluginPageView` pattern

### Phase 5 — Polish
- [ ] Add `ThumbImage.png` as embedded resource
- [ ] Generate a real plugin GUID
- [ ] Update README with usage instructions
