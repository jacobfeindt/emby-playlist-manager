using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmbyPlaylistManager.Conversion;
using MediaBrowser.Common;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace EmbyPlaylistManager
{
    /// <summary>One library item that a conversion will add.</summary>
    public sealed class ConvertItem
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
    }

    /// <summary>What a preview computed and what Apply will do.</summary>
    public sealed class ConvertPlan
    {
        public Guid PlaylistId { get; set; }
        public long PlaylistInternalId { get; set; }
        public string PlaylistName { get; set; }
        public string CollectionName { get; set; }

        /// <summary>Set when a collection with this name already exists. Apply then adds the missing items to it and never removes anything.</summary>
        public long? ExistingCollectionId { get; set; }

        public int PlaylistRows { get; set; }
        public int UniqueItems { get; set; }
        public int AlreadyInCollection { get; set; }

        /// <summary>Items still to add, in playlist order.</summary>
        public List<ConvertItem> ItemsToAdd { get; set; } = new List<ConvertItem>();

        /// <summary>How many of the distinct playlist items are of each type, for example Episode 2,500.</summary>
        public List<KeyValuePair<string, int>> TypeCounts { get; set; } = new List<KeyValuePair<string, int>>();

        /// <summary>When set, the conversion cannot run and this says why.</summary>
        public string Problem { get; set; }

        public string DescribeEffect()
        {
            if (Problem != null) return Problem;
            string dupes = PlaylistRows > UniqueItems ? $" {PlaylistRows - UniqueItems} repeated rows count once." : string.Empty;
            if (ExistingCollectionId.HasValue)
            {
                return ItemsToAdd.Count == 0
                    ? $"Collection '{CollectionName}' already has all {UniqueItems} items. Nothing to do."
                    : $"Collection '{CollectionName}' already exists and has {AlreadyInCollection} of these items. Apply will add the other {ItemsToAdd.Count}.{dupes}";
            }
            return $"Apply will create collection '{CollectionName}' with {ItemsToAdd.Count} items from '{PlaylistName}'.{dupes}";
        }
    }

    public sealed class ConvertPreviewRow
    {
        public string Text { get; set; }
        public string Detail { get; set; }
        public bool IsSummary { get; set; }
    }

    public sealed class ConvertResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public bool CreatedNew { get; set; }
        public int NotAdded { get; set; }
    }

    /// <summary>
    /// Turns a playlist into a collection, item by item. The playlist is never changed. A collection is a set with no order
    /// of its own, so the playlist's order is not carried over and repeated rows collapse to one item.
    /// </summary>
    public sealed class PlaylistConvertService
    {
        public const int MaxPreviewItems = 100;
        public const int ChunkSize = 100;

        /// <summary>Tag stamped on collections this feature creates or fills, so a theme list can be told apart from a movie-series box set.</summary>
        public const string ThemeCollectionTag = "playlist-theme";

        private readonly IApplicationHost _appHost;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        // Nothing here may throw or touch I/O: this is built inside the Plugin constructor, and an exception there stops the plugin loading.
        public PlaylistConvertService(IApplicationHost appHost, ILibraryManager libraryManager, ILogger logger)
        {
            _appHost = appHost;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public ConvertPlan BuildPlan(User user, Guid playlistId, string requestedName)
        {
            var playlist = _libraryManager.GetItemById(playlistId);
            if (playlist == null) throw new InvalidOperationException("That playlist no longer exists. Refresh the list.");

            var rows = PlaylistQueries.GetOrderedEntries(_libraryManager, user, playlist.InternalId, _logger);
            var plan = new ConvertPlan
            {
                PlaylistId = playlist.Id,
                PlaylistInternalId = playlist.InternalId,
                PlaylistName = playlist.Name,
                CollectionName = ConversionPlanner.ResolveName(requestedName, playlist.Name),
                PlaylistRows = rows.Length
            };

            if (plan.CollectionName.Length == 0) { plan.Problem = "Type a name for the collection."; return plan; }
            if (rows.Length == 0) { plan.Problem = $"'{playlist.Name}' is empty, so there is nothing to convert."; return plan; }

            // One entry per distinct library item, in playlist order.
            var byId = new Dictionary<long, ConvertItem>();
            var order = new List<long>();
            foreach (var row in rows)
            {
                if (byId.ContainsKey(row.InternalId)) continue;
                byId[row.InternalId] = new ConvertItem { Id = row.InternalId, Name = DisplayName(row), Type = row.GetType().Name };
                order.Add(row.InternalId);
            }
            plan.UniqueItems = order.Count;
            plan.TypeCounts = order.GroupBy(id => byId[id].Type)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new KeyValuePair<string, int>(g.Key, g.Count())).ToList();

            var existing = FindCollections(user, plan.CollectionName);
            if (existing.Count > 1)
            {
                plan.Problem = $"{existing.Count} collections are named '{plan.CollectionName}'. Type a different collection name.";
                return plan;
            }

            IEnumerable<long> alreadyThere = Enumerable.Empty<long>();
            if (existing.Count == 1)
            {
                plan.ExistingCollectionId = existing[0].InternalId;
                alreadyThere = CollectionMembers(user, existing[0].InternalId);
            }

            var missing = ConversionPlanner.Missing(order, alreadyThere);
            plan.AlreadyInCollection = plan.UniqueItems - missing.Count;
            plan.ItemsToAdd = missing.Select(id => byId[id]).ToList();

            _logger.Info("Convert: '{0}' has {1} rows, {2} distinct items, {3} to add to {4} collection '{5}'.",
                plan.PlaylistName, plan.PlaylistRows, plan.UniqueItems, plan.ItemsToAdd.Count,
                plan.ExistingCollectionId.HasValue ? "existing" : "new", plan.CollectionName);
            return plan;
        }

        public List<ConvertPreviewRow> BuildPreviewRows(ConvertPlan plan)
        {
            var rows = new List<ConvertPreviewRow>
            {
                new ConvertPreviewRow { IsSummary = true, Text = plan.DescribeEffect() }
            };
            if (plan.Problem != null) return rows;

            if (plan.TypeCounts.Count > 0)
            {
                rows.Add(new ConvertPreviewRow
                {
                    IsSummary = true,
                    Text = "Item types: " + string.Join(", ", plan.TypeCounts.Select(t => $"{t.Key} {t.Value}")),
                    Detail = "Emby decides which item types a collection accepts. Anything it refuses is listed after Apply."
                });
            }
            rows.AddRange(plan.ItemsToAdd.Take(MaxPreviewItems).Select(i => new ConvertPreviewRow { Text = i.Name, Detail = i.Type }));
            if (plan.ItemsToAdd.Count > MaxPreviewItems)
                rows.Add(new ConvertPreviewRow { IsSummary = true, Text = $"...and {plan.ItemsToAdd.Count - MaxPreviewItems} more." });
            return rows;
        }

        public async Task<ConvertResult> Apply(User user, ConvertPlan plan, Action<string> progress)
        {
            var result = new ConvertResult();
            string step = "starting";
            try
            {
                if (plan.Problem != null) return Fail(result, plan.Problem);
                if (plan.ItemsToAdd.Count == 0)
                {
                    result.Success = true;
                    result.Message = $"Collection '{plan.CollectionName}' already has all {plan.UniqueItems} items. Nothing was changed.";
                    return result;
                }

                step = "resolving Emby's collection service";
                var manager = ResolveCollectionManager();
                if (manager == null)
                    return Fail(result, "Emby's collection service is not available on this server.");
                _logger.Info("Convert: collection service resolved ({0}).", manager.GetType().Name);

                _logger.Info("Convert: applying '{0}' to collection '{1}' ({2} items to add, {3}).",
                    plan.PlaylistName, plan.CollectionName, plan.ItemsToAdd.Count, plan.ExistingCollectionId.HasValue ? "existing collection" : "new collection");

                var chunks = ConversionPlanner.Chunk(plan.ItemsToAdd.Select(i => i.Id).ToList(), ChunkSize);
                int firstChunkToAdd = 0;

                long collectionId;
                BoxSet createdBoxSet = null;
                if (plan.ExistingCollectionId.HasValue)
                {
                    collectionId = plan.ExistingCollectionId.Value;
                }
                else
                {
                    // Create the collection together with the first batch, the way Emby's own REST call does, and fill in every option
                    // the REST layer fills in. An empty collection and unset options are untested paths inside Emby.
                    step = "creating the collection";
                    var options = new CollectionCreationOptions
                    {
                        Name = plan.CollectionName,
                        IsLocked = false,
                        ParentId = 0,
                        ItemIdList = chunks[0],
                        ProviderIds = new ProviderIdDictionary(),
                        UserIds = user != null ? new[] { user.InternalId } : new long[0]
                    };
                    _logger.Info("Convert: creating collection '{0}' with {1} items for user {2}.", options.Name, options.ItemIdList.Length, user?.InternalId.ToString() ?? "none");
                    createdBoxSet = await manager.CreateCollection(options);
                    if (createdBoxSet == null)
                        return Fail(result, "Emby did not return the new collection, so nothing could be added. Check whether the collection exists in Emby before trying again.");

                    collectionId = createdBoxSet.InternalId;
                    result.CreatedNew = true;
                    firstChunkToAdd = 1;
                    _logger.Info("Convert: created collection '{0}' (id {1}, {2}).", plan.CollectionName, collectionId, createdBoxSet.GetType().Name);
                    progress?.Invoke($"Created collection '{plan.CollectionName}'. Adding the remaining items...");
                }

                int done = firstChunkToAdd == 0 ? 0 : chunks[0].Length;
                for (int c = firstChunkToAdd; c < chunks.Count; c++)
                {
                    step = $"adding items {done + 1} to {done + chunks[c].Length}";
                    await AddChunk(manager, collectionId, chunks[c]);
                    done += chunks[c].Length;
                    progress?.Invoke($"Added {done} of {plan.ItemsToAdd.Count} items...");
                }

                step = "tagging the collection as a theme list";
                MarkAsThemeCollection(collectionId, createdBoxSet);

                step = "checking the collection afterwards";
                ReportOutcome(user, plan, collectionId, result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Convert failed for '{0}' while {1}", ex, plan.PlaylistName, step);
                return Fail(result, $"Conversion failed while {step}: {ex.GetType().Name}: {ex.Message} Some items may already have been added to the collection.");
            }
        }

        // ------------------------------------------------------------------------------------------ helpers

        /// <summary>Emby's collection service, or null if this server does not provide one. Never throws.</summary>
        private ICollectionManager ResolveCollectionManager()
        {
            try
            {
                var manager = _appHost.TryResolve<ICollectionManager>();
                if (manager != null) return manager;
            }
            catch (Exception ex) { _logger.Warn("Convert: TryResolve of the collection service failed: {0}", ex.Message); }

            try { return _appHost.Resolve<ICollectionManager>(); }
            catch (Exception ex)
            {
                _logger.ErrorException("Convert: Emby's collection service could not be resolved", ex);
                return null;
            }
        }

        /// <summary>Reads the collection back, because Emby does not say which items it silently ignored, and writes the result message.</summary>
        private void ReportOutcome(User user, ConvertPlan plan, long collectionId, ConvertResult result)
        {
            var members = new HashSet<long>(CollectionMembers(user, collectionId));
            var notAdded = members.Count == 0 ? new List<ConvertItem>() : plan.ItemsToAdd.Where(i => !members.Contains(i.Id)).ToList();
            result.NotAdded = notAdded.Count;
            result.Success = true;

            int landed = plan.ItemsToAdd.Count - notAdded.Count;
            result.Message = result.CreatedNew
                ? $"Created collection '{plan.CollectionName}' with {landed} items."
                : $"Added {landed} items to collection '{plan.CollectionName}'.";

            if (members.Count == 0)
            {
                result.Message += " The collection could not be read back to confirm the items.";
                _logger.Warn("Convert: '{0}' could not be read back after adding {1} items.", plan.CollectionName, plan.ItemsToAdd.Count);
            }
            else if (notAdded.Count > 0)
            {
                var byType = string.Join(", ", notAdded.GroupBy(i => i.Type).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
                var sample = string.Join("; ", notAdded.Take(8).Select(i => i.Name));
                result.Message += $" Emby did not add {notAdded.Count} items ({byType}). First ones: {sample}.";
                _logger.Warn("Convert: Emby did not add {0} items to '{1}' ({2}). They are: {3}", notAdded.Count, plan.CollectionName, byType,
                    string.Join("; ", notAdded.Select(i => $"{i.Name} [{i.Type}]")));
            }
            _logger.Info("Convert: {0}", result.Message);
        }

        /// <summary>Adds a batch. If Emby refuses the batch, retries the items one at a time so one bad item cannot block the rest.</summary>
        private async Task AddChunk(ICollectionManager manager, long collectionId, long[] chunk)
        {
            try
            {
                await manager.AddToCollection(collectionId, chunk);
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn("Convert: adding {0} items in one call failed ({1}). Retrying one item at a time.", chunk.Length, ex.Message);
            }

            foreach (var id in chunk)
            {
                try { await manager.AddToCollection(collectionId, new[] { id }); }
                catch (Exception ex) { _logger.Warn("Convert: Emby refused item {0}: {1}", id, ex.Message); }
            }
        }

        /// <summary>
        /// Stamps the collection with <see cref="ThemeCollectionTag"/> so a theme list can be told apart from a movie-series box set.
        /// Best-effort: the items are already added, so a failure here only logs a warning and never fails the conversion.
        /// </summary>
        private void MarkAsThemeCollection(long collectionId, BoxSet created)
        {
            try
            {
                var boxSet = created ?? _libraryManager.GetItemById(collectionId) as BoxSet;
                if (boxSet == null)
                {
                    _logger.Warn("Convert: could not load collection {0} to tag it.", collectionId);
                    return;
                }

                var tags = boxSet.Tags ?? new string[0];
                if (tags.Contains(ThemeCollectionTag, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.Info("Convert: collection '{0}' already has the '{1}' tag.", boxSet.Name, ThemeCollectionTag);
                    return;
                }

                boxSet.Tags = tags.Concat(new[] { ThemeCollectionTag }).ToArray();
                var parent = boxSet.Parent ?? (boxSet.ParentId != 0 ? _libraryManager.GetItemById(boxSet.ParentId) as Folder : null);
                _libraryManager.UpdateItem(boxSet, parent, ItemUpdateType.MetadataEdit);
                _logger.Info("Convert: tagged collection '{0}' with '{1}'.", boxSet.Name, ThemeCollectionTag);
            }
            catch (Exception ex)
            {
                _logger.Warn("Convert: could not tag collection {0} with '{1}': {2}", collectionId, ThemeCollectionTag, ex.Message);
            }
        }

        private List<BaseItem> FindCollections(User user, string name)
        {
            var all = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { "BoxSet" },
                Recursive = true
            });
            return all.Where(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private IEnumerable<long> CollectionMembers(User user, long collectionInternalId)
        {
            return _libraryManager.GetItemList(new InternalItemsQuery(user) { CollectionIds = new[] { collectionInternalId } })
                .Select(i => i.InternalId);
        }

        private static string DisplayName(BaseItem item)
        {
            var episode = item as Episode;
            if (episode != null)
                return $"{episode.SeriesName} S{(episode.ParentIndexNumber?.ToString("00") ?? "?")}E{(episode.IndexNumber?.ToString("00") ?? "?")} - {item.Name}";
            return item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name;
        }

        private static ConvertResult Fail(ConvertResult result, string message)
        {
            result.Success = false;
            result.Message = message;
            return result;
        }
    }
}
