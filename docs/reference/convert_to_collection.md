# Converting a playlist to a collection: design, Emby API facts, and what still has to be verified

Status: implemented and compiled; the pure helpers are unit-tested; **not yet run against a live Emby server**.

## Why

A playlist only offers Play. A collection is a library item with its own page, artwork, details and cast. Adding a playlist to a collection was tested on a live server and works, but the playlist then shows as one playable item inside the collection. So this feature copies the playlist's items into a collection **one item at a time**, instead of nesting the playlist.

## What was built

| Piece | File | Notes |
|---|---|---|
| Pure helpers | `Conversion/ConversionPlanner.cs` | Distinct ids in first-occurrence order, ids missing from a collection, batching, name fallback. No Emby types. Tested by `tests/PlaylistSorter.Tests`. |
| Convert preview/apply | `PlaylistConvertService.cs` | `BuildPlan`, `BuildPreviewRows`, `Apply`. |
| UI | `PluginOptions.cs`, `UI/ConvertPageView.cs` | Convert page: playlist dropdown, collection name, Preview Conversion, Create Collection, preview list, status. |

## Emby 4.9.3 collection API facts the code relies on

From the SDK reference (`ICollectionManager`, `CollectionCreationOptions`, `InternalItemsQuery`) and its OpenAPI spec:

- `ICollectionManager.CreateCollection(CollectionCreationOptions)` returns `Task<BoxSet>`. Options: `Name`, `IsLocked`, `ItemIdList` (`long[]`), `ParentId`, `ProviderIds`, `UserIds`.
- `ICollectionManager.AddToCollection(long collectionId, long[] itemIds)` returns `Task`. `RemoveFromCollection(BoxSet, long[])` is synchronous.
- The REST `POST /Collections` treats `Ids` as optional, so an empty collection can be created. The plugin relies on this *(inferred from the spec, not tried)*.
- A collection's members are read with `InternalItemsQuery.CollectionIds`.
- Nothing in the SDK says which item types a collection accepts or what happens to ones it refuses. The plugin therefore reads the collection back and reports the difference.
- Collections are library-wide, not per user.

## How a conversion runs

1. **Preview** (`BuildPlan`): read the playlist in order, keep one entry per distinct library item, find an existing collection with the chosen name (a name used by more than one collection is refused), and compute the items still to add.
2. **Create Collection** (`Apply`): create the collection with the first batch of items (or reuse the existing one), add the remaining items in batches of 100, and retry a refused batch one item at a time. Then read the collection back and report anything Emby did not add, by name and type.
3. The playlist is never modified. The shadow system is not involved, because collections are not playlists.

`ICollectionManager` is resolved when a conversion starts, not injected into the `Plugin` constructor. A missing registration there would stop the whole plugin loading, which is exactly how an earlier startup bug broke a server.

Sets have no order, so the playlist's order is not carried over and repeated rows count once.

Every collection the plugin creates or fills is tagged `playlist-theme` (`PlaylistConvertService.ThemeCollectionTag`). Named themes are managed on a separate **Themes** page, which applies a theme's name as an additional tag and can assign existing collections. Emby's box-set libraries cannot be filtered (a `boxsets`-type virtual library shows every collection regardless of `ParentId`), so the **tag** is the reliable grouping mechanism — not a virtual library. The tag is applied best-effort and never fails a conversion.

## Verify on a live server before releasing

Use a small throwaway playlist first (movies and episodes), then a large one.

| # | Question | How to check | If the answer is "no" |
|---|---|---|---|
| 1 | Can an empty collection be created? | Click Create Collection on a new name. A failure shows in the status line and the log (`Convert failed`). | Create with the first batch of items instead. |
| 2 | Is an existing collection found by name? | Convert the same playlist twice. The second preview must say the collection exists and nothing is left to add. | Look at the `BoxSet` query in `FindCollections`. |
| 3 | Does the read-back return the members? | The result message should say how many items landed. "Could not be read back" means `CollectionIds` returned nothing. | Use `BoxSet` children another way. |
| 4 | Which item types does Emby accept? | Convert a playlist of movies, series and episodes and read the "did not add" list. | Decide what to do with refused types, for example add the series instead of the episode. |
| 5 | How long do 2,700 items take? | Read the `Convert: ... finished in N ms` log line. | Tune the batch size. |
| 6 | Does the collection page show details in the web app and a TV or phone app? | Open it from each. | Check the collection's metadata refresh. |
| 7 | Does the page keep working? | Change the playlist and name, preview, save, reopen. | Same fixes as the sort section. |

## Not built

- Removing items from the collection that are no longer in the playlist (the conversion only adds).
- A collection display order.
- Collection to playlist, or keeping a collection in sync on a schedule.

## Live test results

First live run, 2026-11-23, Emby Server 4.10.2.0 (.NET 8.0.32, Windows). Findings were relayed from the Emby log by a second session.

| Step | Result |
|---|---|
| Preview | Worked: 3 rows, 3 distinct items, 3 to add to a new collection. |
| Create Collection | **Failed** about 3 ms after the "applying" log line with a `NullReferenceException` whose stack had only the plugin's `Apply` frame. No items were added and no collection folder appeared on disk. |

The null was dereferenced inside `Apply` before the collection existed, so it was either the collection service returned by `Resolve<ICollectionManager>()` or the collection returned by `CreateCollection`. The options also left `UserIds` and `ProviderIds` unset, which Emby's REST layer fills in.

Changes made in response (not yet re-tested on the server):
- `ResolveCollectionManager` tries `TryResolve` first, then `Resolve`, and reports a clear message when neither gives a service.
- The collection is created together with the first batch of items, with `UserIds`, `ProviderIds`, `IsLocked` and `ParentId` all set, instead of creating an empty collection with unset options.
- A null result from `CreateCollection` is reported instead of dereferenced.
- `Apply` tracks its current step. A failure now says which step it happened in, and the log has a line for the service, the create call and each batch, so a failure without a PDB still points at a line of the code.

Still open: whether Emby accepts an empty collection (item 1 above) is no longer exercised, and the next run will show whether the failure was the service, the create call, or the unset options.
