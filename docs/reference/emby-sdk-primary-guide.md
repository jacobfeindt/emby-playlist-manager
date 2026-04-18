# Emby SDK Primary Reference Guide

All information here is confirmed against Emby Server 4.9.3.0 DLLs and XML docs at `C:\Git\Emby-Server\system\`.
Do not re-derive from NuGet packages or online docs — they target an older version and will be wrong.

For patterns and gotchas specific to this plugin, see `emby-sdk-supplemental-guide.md`.
For the full SDK samples and OpenAPI spec, see `C:\Git\Emby.SDK-4.9.3.0\Emby.SDK-4.9.3.0\`.

---

## Assembly Map

| Type(s) | Assembly | XML Docs |
|---|---|---|
| `IScheduledTask`, `IConfigurableScheduledTask`, `TaskTriggerInfo`, `TaskState`, `TaskCompletionStatus` | `MediaBrowser.Model.dll` | `MediaBrowser.Model.xml` |
| `IServerEntryPoint` | `MediaBrowser.Controller.dll` | `MediaBrowser.Controller.xml` |
| `IPlaylistManager`, `PlaylistCreationRequest`, `PlaylistItemsAddedEventArgs`, `PlaylistItemsRemovedEventArgs`, `PlaylistItemsMovedEventArgs` | `MediaBrowser.Controller.dll` | `MediaBrowser.Controller.xml` |
| `ILibraryManager`, `IUserManager`, `InternalItemsQuery`, `ItemChangeEventArgs` | `MediaBrowser.Controller.dll` | `MediaBrowser.Controller.xml` |
| `IServerApplicationHost` | `MediaBrowser.Controller.dll` | `MediaBrowser.Controller.xml` |
| `IApplicationHost`, `IApplicationPaths`, `BasePlugin` | `MediaBrowser.Common.dll` | `MediaBrowser.Common.xml` |
| `ILogManager`, `IJsonSerializer`, `IFileSystem`, `TaskTriggerInfo` | `MediaBrowser.Model.dll` | `MediaBrowser.Model.xml` |
| `EditableOptionsBase` | `Emby.Web.GenericEdit.dll` | `Emby.Web.GenericEdit.xml` |
| `IPluginUIPageController`, `PluginPageInfo`, `IHasUIPages`, `IHasThumbImage` | `MediaBrowser.Model.dll` | not in XML docs |
| Event implementations (`add_PlaylistItemsAdded` etc.) | `Emby.Server.Implementations.dll` | none |

---

## Confirmed Namespaces

```csharp
// Tasks
using MediaBrowser.Model.Tasks;          // IScheduledTask, IConfigurableScheduledTask
                                          // TaskTriggerInfo, TaskState, TaskCompletionStatus

// Plugin infrastructure
using MediaBrowser.Controller.Plugins;   // IServerEntryPoint
using MediaBrowser.Common.Plugins;       // BasePlugin
using MediaBrowser.Model.Plugins;        // IHasPluginConfiguration, BasePluginConfiguration
using MediaBrowser.Model.Plugins.UI;     // IHasUIPages, IPluginUIPageController
using MediaBrowser.Model.Plugins.UI.Views; // IPluginUIView, IPluginPageView

// Playlists
using MediaBrowser.Controller.Playlists; // IPlaylistManager, PlaylistCreationRequest
                                          // PlaylistItemsAddedEventArgs
                                          // PlaylistItemsRemovedEventArgs
                                          // PlaylistItemsMovedEventArgs

// Library
using MediaBrowser.Controller.Library;   // ILibraryManager, IUserManager
                                          // ItemChangeEventArgs
using MediaBrowser.Controller.Entities;  // BaseItem, User, InternalItemsQuery

// Application host
using MediaBrowser.Controller;           // IServerApplicationHost
using MediaBrowser.Common;               // IApplicationHost
using MediaBrowser.Common.Configuration; // IApplicationPaths

// Services
using MediaBrowser.Model.Logging;        // ILogManager, ILogger
using MediaBrowser.Model.Serialization;  // IJsonSerializer
using MediaBrowser.Model.IO;             // IFileSystem

// UI
using Emby.Web.GenericEdit;              // EditableOptionsBase
using Emby.Web.GenericEdit.Elements;     // CaptionItem, SpacerItem, LabelItem, ButtonItem
                                          // StatusItem, IconNames, ItemStatus
using Emby.Web.GenericEdit.Elements.List; // GenericItemList, GenericListItem, ItemListIconMode
using MediaBrowser.Model.Attributes;     // [DisplayName], [Description], [Required]
                                          // [EditFilePicker], [EditFolderPicker], [AutoPostBack]
using MediaBrowser.Model.Drawing;        // ImageFormat (for IHasThumbImage)
```

---

## IPlaylistManager

Namespace: `MediaBrowser.Controller.Playlists`
Confirmed in: `MediaBrowser.Controller.dll`
Events implemented in: `Emby.Server.Implementations.dll`

```csharp
Task<PlaylistCreationResult> CreatePlaylist(PlaylistCreationRequest options)
void AddToPlaylist(long playlistId, long[] itemIds, User user)   // void — do NOT await
Task RemoveFromPlaylist(long playlistId, long[] entryIds)
Task MoveItem(long playlistId, long entryId, int newIndex)

// Events (subscribe in IServerEntryPoint.Run())
event EventHandler<PlaylistItemsAddedEventArgs>   PlaylistItemsAdded;
event EventHandler<PlaylistItemsRemovedEventArgs> PlaylistItemsRemoved;
event EventHandler<PlaylistItemsMovedEventArgs>   PlaylistItemsMoved;
```

### PlaylistCreationRequest

```csharp
string   Name
User     User        // the creating user — NOT a UserId string
long[]   ItemIdList
string   MediaType
bool     IsPublic    // true = shared Playlists folder; false = UserPlaylists (private)
                     // NOT in XML docs but confirmed present in DLL
```

### PlaylistCreationResult

```csharp
string Id            // Guid as string — parse with new Guid(result.Id)
                     // NOT in XML docs; confirmed via Go REST client model
```

### Event args

```csharp
// All three carry:
PlaylistItemsAddedEventArgs.Playlist    // the affected Playlist item (BaseItem)
PlaylistItemsRemovedEventArgs.Playlist
PlaylistItemsMovedEventArgs.Playlist

// Additional properties (e.g. which item IDs changed) — not documented
// Discover at runtime by logging args on first live event
```

---

## ILibraryManager

Namespace: `MediaBrowser.Controller.Library`

```csharp
BaseItem   GetItemById(Guid id)
BaseItem[] GetItemList(InternalItemsQuery query)

// Events
event EventHandler<ItemChangeEventArgs> ItemAdded;
event EventHandler<ItemChangeEventArgs> ItemRemoved;
event EventHandler<ItemChangeEventArgs> ItemUpdated;  // fires very frequently during scans
```

### InternalItemsQuery (key properties)

Namespace: `MediaBrowser.Controller.Entities`

```csharp
string[] IncludeItemTypes          // e.g. new[] { "Playlist" }, "Episode", "Series"
long[]   ParentIds                 // plural — NOT ParentId
long[]   AncestorIds
long[]   ListIds                   // get items belonging to a playlist by InternalId
KeyValuePair<string,string>[] AnyProviderIdEquals  // e.g. new[] { new KVP("Imdb", "tt1375666") }
int?     ParentIndexNumber         // season number
int?     IndexNumber               // episode number

// Constructor — pass User to scope results to that user's library access
new InternalItemsQuery(user)
```

---

## IScheduledTask

Namespace: `MediaBrowser.Model.Tasks`
Confirmed in: `MediaBrowser.Model.dll`

```csharp
public class MyTask : IScheduledTask
{
    public string Name        { get; }
    public string Description { get; }
    public string Category    { get; }
    public string Key         { get; }  // unique identifier, no spaces

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers();
    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress);
}
```

### TaskTriggerInfo

```csharp
// Type string constants (confirmed in MediaBrowser.Model.dll)
TaskTriggerInfo.TriggerWeekly    // use with DayOfWeek + TimeOfDayTicks
TaskTriggerInfo.TriggerDaily     // use with TimeOfDayTicks
TaskTriggerInfo.TriggerInterval  // use with IntervalTicks
TaskTriggerInfo.TriggerStartup   // no additional fields needed

// Properties
string   Type
long     TimeOfDayTicks
long     IntervalTicks
DayOfWeek DayOfWeek
long     MaxRuntimeTicks
```

Registration: implement `IScheduledTask` — Emby discovers it automatically via DI.
Concrete trigger classes also available: `IntervalTrigger`, `StartupTrigger`, `DailyTrigger`, `WeeklyTrigger` in `Emby.Server.Implementations.dll`.

---

## IServerEntryPoint

Namespace: `MediaBrowser.Controller.Plugins`
Confirmed in: `MediaBrowser.Controller.dll`

```csharp
public class PluginEntryPoint : IServerEntryPoint
{
    public void Run()   // void — NOT Task (confirmed by build)
    {
        // Subscribe to events here
    }

    public void Dispose() { /* unsubscribe */ }
}
```

Emby discovers and runs `IServerEntryPoint` implementations automatically — no manual registration.

---

## BaseItem — Id vs InternalId

```csharp
Guid Id          // public item GUID — used with GetItemById(Guid)
long InternalId  // database row ID — used with all IPlaylistManager operations
```

Never mix these up. `AddToPlaylist`, `RemoveFromPlaylist`, `MoveItem`, `ListIds`, `ParentIds`, `AncestorIds` all take `long` (InternalId). `GetItemById` takes `Guid` (Id).

---

## IApplicationPaths

Namespace: `MediaBrowser.Common.Configuration`

```csharp
string PluginConfigurationsPath  // where plugin .json config files live
string DataPath                  // server data root — use for shadow file storage
                                 // e.g. Path.Combine(appPaths.DataPath, "PlaylistManager", "shadows")
```

---

## Plugin Constructor Pattern

Emby injects `IServerApplicationHost` (not `IApplicationHost`) into plugin constructors:

```csharp
public Plugin(IServerApplicationHost applicationHost, ILogManager logManager)
    : base(applicationHost) { }
```

Resolve additional services at runtime:

```csharp
_applicationHost.Resolve<ILibraryManager>()
_applicationHost.Resolve<IPlaylistManager>()
_applicationHost.Resolve<IUserManager>()
_applicationHost.Resolve<IJsonSerializer>()
_applicationHost.Resolve<IFileSystem>()
_applicationHost.Resolve<IApplicationPaths>()
```

---

## Build Requirements

- Target framework: `net8.0` — Emby Server 4.9.3 is .NET 8; `netstandard2.0` will not work
- References: direct `<HintPath>` to `C:\Git\Emby-Server\system\*.dll` — not NuGet packages
- All Emby DLL references must have `<Private>false</Private>` — do not copy to output
- Plugin DLL must be compiled against the exact same version the server is running
  - Compiled against newer → `LoaderException` on older server (version mismatch)
  - Compiled against older → loads fine on newer (binding redirects handle it)
