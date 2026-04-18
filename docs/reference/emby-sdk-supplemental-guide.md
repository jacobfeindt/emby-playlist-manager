# Emby SDK Supplemental Guide

Covers only what is relevant to this plugin. When something is not documented here, go to the source:

```
C:\Git\Emby.SDK-4.9.3.0\Emby.SDK-4.9.3.0\
  SampleCode\Examples\EmbyPluginUiDemo\     ← full UI pattern reference
  SampleCode\Templates\EmbyPluginUiTemplate\ ← minimal UI plugin scaffold
  SampleCode\Templates\EmbyPluginMinimalTemplate\ ← bare plugin scaffold
  SampleCode\RestApi\Go\                    ← all REST endpoints + models
  Resources\OpenApi\openapi_v3.json         ← full OpenAPI spec
```

---

## Plugin Lifecycle

### Constructor injection (confirmed working)

```csharp
public Plugin(IServerApplicationHost applicationHost, ILogManager logManager)
```

`IServerApplicationHost` (not `IApplicationHost`) is what the SDK templates inject — it extends `IApplicationHost` and is what Emby actually resolves for plugins.

### Resolve additional services at runtime

```csharp
var libraryManager = _applicationHost.Resolve<ILibraryManager>();
var playlistManager = _applicationHost.Resolve<IPlaylistManager>();
var userManager = _applicationHost.Resolve<IUserManager>();
var jsonSerializer = _applicationHost.Resolve<IJsonSerializer>();
var fileSystem = _applicationHost.Resolve<IFileSystem>();
var appPaths = _applicationHost.Resolve<IApplicationPaths>();
```

### Plugin paths

```csharp
// Plugin config/data storage root
string configPath = appPaths.PluginConfigurationsPath;

// Shadow file storage (create subdirectory manually)
string shadowDir = Path.Combine(appPaths.DataPath, "PlaylistManager", "shadows");
```

### Startup hook — `IServerEntryPoint`

Not used in our current plugin (we use constructor + event subscription instead). If needed:

```csharp
public class PluginStartup : IServerEntryPoint
{
    public Task Run() { /* subscribe to events here */ return Task.CompletedTask; }
    public void Dispose() { /* unsubscribe */ }
}
```

Register by implementing the interface — Emby discovers and runs it automatically.

---

## IPlaylistManager Events (Phase 7a — unvalidated)

These events are the foundation of the shadow system. Signatures are inferred from SDK patterns — **validate against server DLLs before use**.

```csharp
// Subscribe in Plugin constructor or IServerEntryPoint.Run()
_playlistManager.PlaylistItemsAdded   += OnPlaylistChanged;
_playlistManager.PlaylistItemsRemoved += OnPlaylistChanged;
_playlistManager.PlaylistItemsMoved   += OnPlaylistChanged;

// ILibraryManager — fires very frequently during scans, debounce required
_libraryManager.ItemUpdated += OnItemUpdated;
```

Event args type is likely `GenericEventArgs<Playlist>` or similar — check `MediaBrowser.Controller.Playlists` in the server DLLs. The playlist's `InternalId` and `Id` will be on the args object.

> SDK source to check: no direct sample — inspect `IPlaylistManager` interface in server DLLs at `C:\Git\Emby-Server\system\MediaBrowser.Controller.dll`

---

## IScheduledTask (Phase 7c)

```csharp
public class PlaylistRepairTask : IScheduledTask
{
    public string Name        => "Playlist Manager: Repair Broken Links";
    public string Description => "Scans all playlists for broken item links and repairs them.";
    public string Category    => "Playlist Manager";
    public string Key         => "PlaylistManagerRepair";  // unique, no spaces

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type        = TaskTriggerInfo.TriggerWeekly,
            DayOfWeek   = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        }
    };

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        // report progress: progress.Report(0..100)
    }
}
```

### TaskTriggerInfo.Type constants (from SDK model)

| Constant | Meaning |
|---|---|
| `TaskTriggerInfo.TriggerWeekly` | Weekly on `DayOfWeek` at `TimeOfDayTicks` |
| `TaskTriggerInfo.TriggerDaily` | Daily at `TimeOfDayTicks` |
| `TaskTriggerInfo.TriggerInterval` | Every `IntervalTicks` |
| `TaskTriggerInfo.TriggerStartup` | On server startup |

Registration: implement `IScheduledTask` — Emby discovers it automatically via DI. No manual registration needed.

---

## REST API — Playlist Endpoints

All paths relative to server base URL (e.g. `http://localhost:8096/emby`). Auth via `?api_key=` or `X-Emby-Token` header.

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/Playlists?Name=&Ids=&MediaType=` | Create playlist |
| `GET` | `/Playlists/{Id}/Items` | Get playlist items |
| `POST` | `/Playlists/{Id}/Items?Ids=&UserId=` | Add items to playlist |
| `DELETE` | `/Playlists/{Id}/Items?EntryIds=` | Remove items (by entry ID) |
| `POST` | `/Playlists/{Id}/Items/{ItemId}/Move/{NewIndex}` | Reorder item |

Note: REST `{Id}` is the Guid string (same as `item.Id.ToString("N")` or the string from `PlaylistCreationResult.Id`). This differs from the plugin-side `InternalId` (long) used with `IPlaylistManager`.

### REST scheduled task endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/ScheduledTasks` | List all tasks |
| `GET` | `/ScheduledTasks/{Id}` | Get task by ID |
| `POST` | `/ScheduledTasks/Running/{Id}` | Start task |
| `DELETE` | `/ScheduledTasks/Running/{Id}` | Stop task |
| `POST` | `/ScheduledTasks/{Id}/Triggers` | Update triggers |

---

## GenericEdit UI Attributes (quick reference)

All in `MediaBrowser.Model.Attributes` unless noted.

| Attribute | Effect |
|---|---|
| `[DisplayName("...")]` | Label shown in UI |
| `[Description("...")]` | Help text below field |
| `[Required]` | Marks field required |
| `[EditFilePicker]` | Renders file browser |
| `[EditFolderPicker]` | Renders folder browser |
| `[EditMultiline(n)]` | Multiline text area, n rows |
| `[IsPassword]` | Masks input |
| `[AutoPostBack]` | Posts back to server on change (triggers `OnSaveCommand`) |
| `[Decimals(n)]` | Decimal places for double/float |
| `[TristateTrueText("")]` / `[TristateFalseText("")]` | Labels for nullable bool dropdown |

### Special property types (from `Emby.Web.GenericEdit.Elements`)

```csharp
public CaptionItem MySection { get; set; } = new CaptionItem("Section Title");
public SpacerItem  Gap       { get; set; } = new SpacerItem();
public LabelItem   Info      { get; set; } = new LabelItem("Static text");
public ButtonItem  GoButton  { get; set; } = new ButtonItem("Button Label");
```

`ButtonItem` triggers `OnSaveCommand(itemId, commandId, data)` in the page view — `commandId` matches the property name.

---

## SimpleFileStore Pattern

Used by our `OptionsStore`. Handles JSON serialization to `PluginConfigurationsPath`.

```csharp
// Inherit
public class MyStore : SimpleFileStore<MyOptions> 
{
    public MyStore(IApplicationHost host, ILogger logger, string pluginName)
        : base(host, logger, pluginName) { }
}

// Usage
var opts = _store.GetOptions();
opts.SomeField = "value";
_store.SetOptions(opts);
```

File is saved as `{pluginName}.json` in `PluginConfigurationsPath`. Options that should not persist (preview lists, status) must be excluded — mark with `[JsonIgnore]` or reset them in `GetOptions()`.

---

## Known Gotchas

| Issue | Detail |
|---|---|
| `AddToPlaylist` is void | Do not await — it returns void, not Task |
| `IsPublic` on `PlaylistCreationRequest` | `true` = lands in shared `Playlists` folder, visible to all users; `false`/omitted = lands in `UserPlaylists`, private to the `User` on the request. Not in XML docs but confirmed present in DLL. Always set `true` on import. |
| `PlaylistCreationResult.Id` is a string | Parse as Guid: `new Guid(result.Id)`, then `GetItemById(guid)` to get `InternalId` |
| `InternalId` vs `Id` | Playlist operations (`AddToPlaylist`, `RemoveFromPlaylist`) take `long` (InternalId); `GetItemById` takes `Guid` (Id) |
| `ParentIds` is plural | `InternalItemsQuery.ParentIds` is `long[]`, not `ParentId` singular |
| `ILibraryManager.ItemUpdated` fires constantly | Must debounce or filter during library scans — do not do heavy work inline |
| `netstandard2.0` won't work | Server is .NET 8; use `net8.0` target and reference server DLLs directly, not NuGet packages |
| File picker pre-population bug | `[EditFilePicker]` pre-fills with full path including filename; user must clear filename before browsing — no fix available in GenericEdit |
