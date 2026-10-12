# Playlist sorting: design, Emby API facts, and what still has to be verified

Status: implemented and compiled; unit-tested for the pure logic; **not yet run against a live Emby server**.

## What was built

| Piece | File | Notes |
|---|---|---|
| Pure ordering and move planning | `Sorting/*.cs` | No Emby types. Tested by `tests/PlaylistSorter.Tests` (`dotnet run -c Release`). |
| Playlist reads | `PlaylistQueries.cs` | Every read asks for `ItemSortBy.ListItemOrder` and exposes entry ids. |
| Sort preview/apply | `PlaylistSortService.cs` | Snapshot check, hidden-row check, backup, shadow suppression, move or rebuild, verification. |
| One operation at a time | `OperationGate.cs` | Import, repair, scan, sort, scheduled repair task, HTTP import. |
| Shadow write coalescing | `Storage/ShadowPlaylistService.cs` | `RequestUpdate` (3 s debounce per playlist), per-playlist write lock, `Suppress(...)`. |
| UI | `PluginOptions.cs`, `UI/PluginPageView.cs` | Sort section: playlist dropdown, mode, direction, Preview, Apply. |

## Emby 4.9.3 playlist API facts the code relies on

Read from the server assemblies (`C:\Git\Emby-Server\system`) and the SDK (`openapi_v3.json`, `reference\pluginapi\MediaBrowser.Controller.Playlists.IPlaylistManager.html`). Items marked *(inferred)* came from reading IL, not from documentation.

- `IPlaylistManager.MoveItem(long playlistId, long entryId, int newIndex)` exists and can be called in-process. The REST `POST /Playlists/{Id}/Items/{ItemId}/Move/{NewIndex}` is a thin wrapper over it.
- `entryId` is the playlist **row** id (`BaseItem.ListItemEntryId`), not the library item's `InternalId`. `RemoveFromPlaylist(long playlistId, long[] entryIds)` takes entry ids as well.
- Entry ids are returned on items from a query with `ListIds` set. The same library item can be in a playlist several times, each with its own entry id.
- `AddToPlaylist` only appends (`ListItemOrder = max + 1`). The synchronous `AddToPlaylist(long, long[], User)` blocks and allows duplicates *(inferred)*.
- A bare `ListIds` query has no guaranteed order; ask for it with `OrderBy = new[] { (ItemSortBy.ListItemOrder, SortOrder.Ascending) }`.
- `MoveItem` reads all rows in order, removes the row, inserts it at `newIndex`, then deletes and rewrites every row in one transaction (O(n) per move). Entry ids stay the same. `newIndex` is the row's final position *(inferred)*.
- Rebuilding (remove all, add back) changes entry ids and queues a metadata refresh per added item *(inferred)*.
- Events: `PlaylistItemsAdded`, `PlaylistItemsRemoved`, `PlaylistItemsMoved` (one per call).

### Bugs in existing code that this work fixed

- `ClearPlaylist` and the import-overwrite path passed item `InternalId`s to `RemoveFromPlaylist`, which expects entry ids. They now pass `ListItemEntryId` and throw if any is 0.
- Playlist reads in `PlaylistService`, `ShadowPlaylistService`, `PluginPageView` and `PlaylistRepairTask` had no order, so shadows, exports and repairs might not have kept playlist order. They all go through `PlaylistQueries` now.
- Every playlist event used to start its own `Task.Run(UpdateShadow)` with no lock, so N events meant N concurrent rewrites of the same files. Events are now coalesced and writes are serialized.

## How a sort runs

1. **Preview** (`BuildPlan`): read the playlist in order, build `SortEntry` rows, compute the target order (`PlaylistSorter`), then the minimum `MoveItem` calls (`MovePlanner`). More than `PlaylistSortService.MaxMoveOps` (250, provisional) means rebuild instead.
2. **Apply**: re-read the playlist and refuse if it differs from the preview; refuse if the unfiltered row count is larger than what the user can see; write the backup; suppress shadow writes for the playlist; move (checking the real order after the first move and every 25th) or rebuild; verify the final order; write the shadow once.
3. A failed rebuild restores the previous order from memory; the backup file is the second line of defence.

Sort rules: nulls last in both directions, direction applies to the key only, final tie-break is the original position (so a second sort plans zero moves), specials (season 0) after regular seasons, non-episodes use their own sort name as the series key. Shuffle uses a seeded Fisher-Yates.

## Verify on a live server before releasing

Use a throwaway playlist and note the results in this file.

| # | Question | How to check | If the answer is "no" |
|---|---|---|---|
| 1 | Does `ListItemEntryId` come back populated from a `ListIds` query? | Sort a 5-item playlist; a zero id makes the plugin throw "returned without entry ids". | Find the right way to read entry ids before anything else. |
| 2 | Is the ordered query (`ListItemOrder`) accepted by `GetItemList`? | Preview a playlist; a failure is reported in the status line and the log. | Use `Playlist.GetItems` as the REST layer does. |
| 3 | Does `MoveItem`'s `newIndex` mean the final position? | Apply a sort that needs 2-3 moves. The first-move check aborts with a clear message if not. | Adjust `MovePlanner` index semantics. |
| 4 | Time per move at about 2,700 items | Sort a shuffled copy of a playlist of about 2,700 items; read the log line `Sort: N moves ... ms per move`. | Set `MaxMoveOps` from it. At 2,700 items a full reorder needs about 2,600 moves. |
| 5 | Does a rebuild keep order and duplicates? | Force a rebuild (shuffle a large playlist) and check for the `Sort: rebuilt` log line. | Rebuild path needs rework. |
| 6 | Does a user-less `ListIds` count work? | Look for the warning "could not count all rows without a user filter" in the log. | Order verification is the only guard; consider counting as the admin user. |
| 7 | Does the dropdown fill, refresh and survive **Save**? | Open the page, click Refresh Playlists, change the selection, click Save, reopen. | Revisit how `SortPlaylistChoices` is cleared and refilled. |
| 8 | Do the sorted order and the shadow agree? | Apply a sort with shadows on, then run Repair in Full Restore mode and restart. The order must hold. | Check that `UpdateShadow` runs after the sort. |
| 9 | Non-admin user and hidden items | Sort a playlist containing items one user cannot see. Expect the refusal message. | Revisit the hidden-row check. |
| 10 | Is `[Browsable(false)]` `SortPlaylistChoices` kept out of the saved options JSON? | Inspect `Playlist Manager.json` in the plugin configuration folder after a Save. | Mark it `[JsonIgnore]` or keep clearing it before save. |

## Live test results

First live run, 2026-10-11, Emby 4.9.3 (one 4-item playlist; from the plugin's own log lines):

| Checklist item | Result |
|---|---|
| 1. Entry ids returned by a `ListIds` query | Passed. `GetEntryIds` throws on an id of 0 and did not. |
| 2. Ordered query accepted | Passed. 4 rows read in 2 ms, 2 moves planned. |
| 7. Dropdown fills | Passed. 5 playlists listed. Selecting a different one did **not** reach the plugin (no `SortChanged` command was ever logged). Still open. |
| Apply | Not tested. Apply stayed disabled because the page kept showing "Reading the playlist..." although the log shows the preview finished in 13 ms. |

Defects found by this run:
- `appHost.Resolve<Plugin>()` creates a new `Plugin` per call, so the operation gate, shadow debounce timers and suppression counts were not shared. They are now `static`, and the page resolves the plugin once.
- The preview completed before the page was listening for the update. It now runs inside the command, and background operations wait 300 ms before their first status update.

## Phase C (not built): persistent sort rules

Store a rule per playlist (GUID, mode, direction) in `PluginOptions`. Re-apply it after `PlaylistItemsAdded` and `PlaylistItemsMoved` and after imports, using the existing coalescing and `OperationGate`, with an "in progress" flag so the plugin's own moves never retrigger it. Decide how it interacts with Safe-mode repair (which appends at the end) and Full Restore (which uses shadow order) before building.
