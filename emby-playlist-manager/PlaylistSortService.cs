using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Common;
using EmbyPlaylistManager.Sorting;
using EmbyPlaylistManager.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Logging;

namespace EmbyPlaylistManager
{
    /// <summary>What a Preview computed and what Apply will do. Holds a snapshot of the playlist so Apply can refuse to act if it changed.</summary>
    public sealed class SortPlan
    {
        public Guid PlaylistId { get; set; }
        public long PlaylistInternalId { get; set; }
        public string PlaylistName { get; set; }
        public PlaylistSortMode Mode { get; set; }
        public SortDirection Direction { get; set; }
        public int Seed { get; set; }

        /// <summary>Entry ids in the playlist's order when the plan was made.</summary>
        public List<long> SnapshotEntryIds { get; set; }

        /// <summary>The new order.</summary>
        public List<SortEntry> Target { get; set; }

        public List<MoveOp> Moves { get; set; }

        /// <summary>True when there are too many moves to make one at a time; the playlist is then emptied and refilled in the new order.</summary>
        public bool UseRebuild { get; set; }

        public int ChangedPositions { get; set; }

        public string DescribeEffect()
        {
            if (ChangedPositions == 0) return "Already in this order. Nothing to do.";
            return UseRebuild
                ? $"{ChangedPositions} of {Target.Count} items change position. The playlist will be rebuilt in the new order (its internal row ids change)."
                : $"{ChangedPositions} of {Target.Count} items change position, using {Moves.Count} moves.";
        }
    }

    public sealed class SortApplyResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int MovesMade { get; set; }
        public bool Rebuilt { get; set; }
        public string BackupPath { get; set; }
    }

    public sealed class SortPreviewRow
    {
        public int NewPosition { get; set; }
        public int OldPosition { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Sorts one playlist. Preview builds a <see cref="SortPlan"/> without touching anything. Apply re-checks that the playlist is unchanged,
    /// saves a backup of its order, suppresses event-driven shadow writes, reorders it, verifies the result and writes the shadow once.
    /// The ordering and move planning live in the Emby-free <c>Sorting</c> folder.
    /// </summary>
    public class PlaylistSortService
    {
        /// <summary>
        /// Above this many moves the playlist is rebuilt instead (remove all rows, add them back in the new order), because every MoveItem
        /// rewrites the whole playlist. PROVISIONAL: tune from the per-move timing measured on a real server (see the plan's Step 0).
        /// </summary>
        public const int MaxMoveOps = 250;

        private const int VerifyEvery = 25;
        private const int KeepBackups = 10;
        private const int MaxPreviewRows = 200;

        private readonly ILibraryManager _libraryManager;
        private readonly IPlaylistManager _playlistManager;
        private readonly ShadowPlaylistService _shadow;
        private readonly IApplicationPaths _appPaths;
        private readonly ILogger _logger;

        public PlaylistSortService(
            ILibraryManager libraryManager,
            IPlaylistManager playlistManager,
            ShadowPlaylistService shadow,
            IApplicationPaths appPaths,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _playlistManager = playlistManager;
            _shadow = shadow;
            _appPaths = appPaths;
            _logger = logger;
        }

        public string BackupFolder => Path.Combine(_appPaths.DataPath, "PlaylistManager", "sort-backups");

        /// <summary>Playlists for the dropdown. The short id keeps same-named playlists distinguishable.</summary>
        public List<EditorSelectOption> ListPlaylists(User user)
        {
            var playlists = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { "Playlist" }
            });
            return playlists
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new EditorSelectOption
                {
                    Value = p.Id.ToString("N"),
                    Name = $"{p.Name} [{p.Id.ToString("N").Substring(0, 8)}]"
                })
                .ToList();
        }

        public SortPlan BuildPlan(User user, Guid playlistId, PlaylistSortMode mode, SortDirection direction)
        {
            var playlist = _libraryManager.GetItemById(playlistId);
            if (playlist == null) throw new InvalidOperationException("That playlist no longer exists. Refresh the list.");

            var rows = PlaylistQueries.GetOrderedEntries(_libraryManager, user, playlist.InternalId, _logger, strict: true);
            var entryIds = PlaylistQueries.GetEntryIds(rows).ToList();

            var seriesNames = new Dictionary<long, string>();
            var entries = rows.Select((item, i) => ToSortEntry(item, i, seriesNames)).ToList();

            int seed = Environment.TickCount;
            var target = PlaylistSorter.BuildTarget(entries, mode, direction, seed);
            var targetIds = target.Select(t => t.EntryId).ToList();
            var moves = MovePlanner.Plan(entryIds, targetIds);

            return new SortPlan
            {
                PlaylistId = playlist.Id,
                PlaylistInternalId = playlist.InternalId,
                PlaylistName = playlist.Name,
                Mode = mode,
                Direction = direction,
                Seed = seed,
                SnapshotEntryIds = entryIds,
                Target = target,
                Moves = moves,
                UseRebuild = moves.Count > MaxMoveOps,
                ChangedPositions = MovePlanner.CountChangedPositions(entryIds, targetIds)
            };
        }

        /// <summary>First rows of the new order, each with where it was before. Capped so a 3,000-item playlist does not flood the page.</summary>
        public List<SortPreviewRow> BuildPreviewRows(SortPlan plan)
        {
            var oldIndex = new Dictionary<long, int>();
            for (int i = 0; i < plan.SnapshotEntryIds.Count; i++) oldIndex[plan.SnapshotEntryIds[i]] = i;
            return plan.Target
                .Take(MaxPreviewRows)
                .Select((t, i) => new SortPreviewRow { NewPosition = i + 1, OldPosition = oldIndex[t.EntryId] + 1, Text = t.DisplayName ?? t.SortName })
                .ToList();
        }

        public async Task<SortApplyResult> Apply(User user, SortPlan plan, Action<string> progress, bool updateShadow, CancellationToken ct)
        {
            var result = new SortApplyResult();
            string backupPath = null;
            try
            {
                var playlist = _libraryManager.GetItemById(plan.PlaylistId);
                if (playlist == null) return Fail(result, "The playlist no longer exists.");

                if (plan.ChangedPositions == 0)
                {
                    result.Success = true;
                    result.Message = "Already in this order. Nothing was changed.";
                    return result;
                }

                // 1. The playlist must be exactly what the preview saw.
                var current = PlaylistQueries.GetOrderedEntries(_libraryManager, user, plan.PlaylistInternalId, _logger, strict: true);
                var currentIds = PlaylistQueries.GetEntryIds(current).ToList();
                if (!currentIds.SequenceEqual(plan.SnapshotEntryIds))
                    return Fail(result, "The playlist changed since the preview. Run Preview again.");

                // 2. Rows this user cannot see would shift every index, so refuse rather than guess.
                int? allRows = CountAllRows(plan.PlaylistInternalId);
                if (allRows.HasValue && allRows.Value > currentIds.Count)
                    return Fail(result, $"The playlist has {allRows.Value - currentIds.Count} rows this user cannot see. Sorting would misplace items, so nothing was changed.");

                // 3. Backup of the current order.
                backupPath = WriteBackup(plan, current);
                result.BackupPath = backupPath;
                progress?.Invoke("Backup saved. Reordering...");

                using (_shadow.Suppress(plan.PlaylistInternalId))
                {
                    if (plan.UseRebuild)
                    {
                        await Rebuild(user, plan, current, backupPath, progress);
                        result.Rebuilt = true;
                    }
                    else
                    {
                        result.MovesMade = await MoveRows(user, plan, progress, ct);
                    }
                }

                if (updateShadow)
                {
                    try { _shadow.UpdateShadow(user, playlist, "Sort"); }
                    catch (Exception ex) { _logger.ErrorException("Sort: shadow update failed for '{0}'", ex, plan.PlaylistName); }
                }

                result.Success = true;
                result.Message = result.Rebuilt
                    ? $"Sorted '{plan.PlaylistName}' by rebuilding it in the new order."
                    : $"Sorted '{plan.PlaylistName}' with {result.MovesMade} moves.";
                _logger.Info("Sort: {0} (mode {1}, {2}).", result.Message, plan.Mode, plan.Direction);
                return result;
            }
            catch (OperationCanceledException)
            {
                return Fail(result, "Cancelled. The playlist may be partly reordered; it still contains every item." + BackupHint(backupPath));
            }
            catch (SortAbortedException ex)
            {
                _logger.Warn("Sort aborted for '{0}': {1}", plan.PlaylistName, ex.Message);
                return Fail(result, ex.Message + BackupHint(backupPath));
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Sort failed for '{0}'", ex, plan.PlaylistName);
                return Fail(result, "Sort failed: " + ex.Message + BackupHint(backupPath));
            }
        }

        // ------------------------------------------------------------------------------------------ apply paths

        private async Task<int> MoveRows(User user, SortPlan plan, Action<string> progress, CancellationToken ct)
        {
            var expected = new List<long>(plan.SnapshotEntryIds);
            int foreignChanges = 0;
            EventHandler<PlaylistItemsAddedEventArgs> onAdded = (s, e) => { if (e?.Playlist?.InternalId == plan.PlaylistInternalId) Interlocked.Increment(ref foreignChanges); };
            EventHandler<PlaylistItemsRemovedEventArgs> onRemoved = (s, e) => { if (e?.Playlist?.InternalId == plan.PlaylistInternalId) Interlocked.Increment(ref foreignChanges); };
            _playlistManager.PlaylistItemsAdded += onAdded;
            _playlistManager.PlaylistItemsRemoved += onRemoved;
            try
            {
                int done = 0;
                var lastReport = DateTime.UtcNow;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                foreach (var op in plan.Moves)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref foreignChanges) > 0)
                        throw new SortAbortedException("Items were added to or removed from the playlist while it was being sorted. Stopped; run Preview again.");

                    await _playlistManager.MoveItem(plan.PlaylistInternalId, op.EntryId, op.NewIndex);

                    int old = expected.IndexOf(op.EntryId);
                    expected.RemoveAt(old);
                    expected.Insert(op.NewIndex, op.EntryId);
                    done++;

                    // Check the real order after the first move (does MoveItem's index mean what the plan assumes?) and every few moves.
                    if (done == 1 || done % VerifyEvery == 0) VerifyOrder(user, plan, expected, done);

                    if ((DateTime.UtcNow - lastReport).TotalSeconds >= 1)
                    {
                        progress?.Invoke($"Sorting... {done} of {plan.Moves.Count} moves");
                        lastReport = DateTime.UtcNow;
                    }
                }

                VerifyOrder(user, plan, expected, done);
                if (!expected.SequenceEqual(plan.Target.Select(t => t.EntryId)))
                    throw new SortAbortedException("Internal check failed: the planned moves do not produce the requested order.");

                // Timing is logged so MaxMoveOps (the move-versus-rebuild threshold) can be tuned from real data.
                _logger.Info("Sort: {0} moves on '{1}' ({2} items) took {3} ms ({4} ms per move, including verification).",
                    done, plan.PlaylistName, plan.SnapshotEntryIds.Count, timer.ElapsedMilliseconds, done == 0 ? 0 : timer.ElapsedMilliseconds / done);
                return done;
            }
            finally
            {
                _playlistManager.PlaylistItemsAdded -= onAdded;
                _playlistManager.PlaylistItemsRemoved -= onRemoved;
            }
        }

        private void VerifyOrder(User user, SortPlan plan, List<long> expected, int movesDone)
        {
            var actual = PlaylistQueries.GetEntryIds(PlaylistQueries.GetOrderedEntries(_libraryManager, user, plan.PlaylistInternalId, _logger, strict: true));
            if (!actual.SequenceEqual(expected))
                throw new SortAbortedException($"After {movesDone} moves the playlist is not in the expected order, so sorting was stopped. Nothing is lost: every item is still in the playlist.");
        }

        private async Task Rebuild(User user, SortPlan plan, BaseItem[] before, string backupPath, Action<string> progress)
        {
            var targetItemIds = plan.Target.Select(t => t.ItemId).ToArray();
            var originalItemIds = before.Select(b => b.InternalId).ToArray();

            progress?.Invoke("Rebuilding playlist...");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await _playlistManager.RemoveFromPlaylist(plan.PlaylistInternalId, PlaylistQueries.GetEntryIds(before));
            try
            {
                _playlistManager.AddToPlaylist(plan.PlaylistInternalId, targetItemIds, user);
                var after = PlaylistQueries.GetOrderedEntries(_libraryManager, user, plan.PlaylistInternalId, _logger, strict: true);
                if (!after.Select(a => a.InternalId).SequenceEqual(targetItemIds))
                    throw new SortAbortedException($"After rebuilding, the playlist has {after.Length} items but {targetItemIds.Length} were expected, or they are out of order.");
                _logger.Info("Sort: rebuilt '{0}' ({1} items) in {2} ms.", plan.PlaylistName, targetItemIds.Length, timer.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Sort: rebuild failed for '{0}'; restoring the previous order", ex, plan.PlaylistName);
                try
                {
                    var partial = PlaylistQueries.GetOrderedEntries(_libraryManager, user, plan.PlaylistInternalId, _logger, strict: true);
                    if (partial.Length > 0) await _playlistManager.RemoveFromPlaylist(plan.PlaylistInternalId, PlaylistQueries.GetEntryIds(partial));
                    _playlistManager.AddToPlaylist(plan.PlaylistInternalId, originalItemIds, user);
                }
                catch (Exception restoreEx)
                {
                    _logger.ErrorException("Sort: restore after failed rebuild also failed for '{0}'", restoreEx, plan.PlaylistName);
                    throw new SortAbortedException("Rebuilding failed and the previous order could not be restored automatically." + BackupHint(backupPath));
                }
                throw new SortAbortedException("Rebuilding failed (" + ex.Message + "). The previous order was restored.");
            }
        }

        // ------------------------------------------------------------------------------------------ helpers

        private SortEntry ToSortEntry(BaseItem item, int index, Dictionary<long, string> seriesNames)
        {
            var entry = new SortEntry
            {
                EntryId = item.ListItemEntryId,
                ItemId = item.InternalId,
                OriginalIndex = index,
                SortName = item.SortName ?? item.Name ?? string.Empty,
                DisplayName = item.Name,
                Date = item.PremiereDate.HasValue
                    ? item.PremiereDate.Value.UtcDateTime
                    : (item.ProductionYear.HasValue ? new DateTime(item.ProductionYear.Value, 1, 1) : (DateTime?)null),
                DateAdded = item.DateCreated.UtcDateTime
            };

            var episode = item as Episode;
            if (episode != null)
            {
                entry.IsEpisode = true;
                entry.Season = episode.ParentIndexNumber;
                entry.Episode = episode.IndexNumber;
                entry.SeriesKey = SeriesSortName(episode, seriesNames);
                entry.DisplayName = $"{episode.SeriesName} S{entry.Season?.ToString("00") ?? "?"}E{entry.Episode?.ToString("00") ?? "?"} - {item.Name}";
            }
            return entry;
        }

        private string SeriesSortName(Episode episode, Dictionary<long, string> cache)
        {
            long seriesId = episode.SeriesId;
            if (seriesId == 0) return episode.SeriesName;
            if (!cache.TryGetValue(seriesId, out var name))
            {
                var series = _libraryManager.GetItemById(seriesId);
                name = series?.SortName ?? episode.SeriesName;
                cache[seriesId] = name;
            }
            return name;
        }

        /// <summary>Number of rows in the playlist with no user filter, or null if that query is not possible (the verification after each move still guards the order).</summary>
        private int? CountAllRows(long playlistInternalId)
        {
            try
            {
                return _libraryManager.GetItemList(new InternalItemsQuery { ListIds = new[] { playlistInternalId } }).Length;
            }
            catch (Exception ex)
            {
                _logger.Warn("Sort: could not count all rows without a user filter ({0}); relying on order verification.", ex.Message);
                return null;
            }
        }

        private string WriteBackup(SortPlan plan, BaseItem[] before)
        {
            Directory.CreateDirectory(BackupFolder);
            var path = Path.Combine(BackupFolder, $"{plan.PlaylistId:N}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            var backup = new
            {
                plan.PlaylistName,
                PlaylistId = plan.PlaylistId,
                Mode = plan.Mode.ToString(),
                Direction = plan.Direction.ToString(),
                CreatedUtc = DateTime.UtcNow,
                Items = before.Select((b, i) => new { Position = i + 1, EntryId = b.ListItemEntryId, ItemId = b.InternalId, b.Name }).ToList()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
            PruneBackups(plan.PlaylistId);
            return path;
        }

        private void PruneBackups(Guid playlistId)
        {
            try
            {
                var old = Directory.GetFiles(BackupFolder, $"{playlistId:N}-*.json")
                    .OrderByDescending(f => f, StringComparer.Ordinal)   // timestamp in the name sorts chronologically
                    .Skip(KeepBackups);
                foreach (var f in old) File.Delete(f);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Sort: could not prune old backups", ex);
            }
        }

        private static string BackupHint(string backupPath)
        {
            return string.IsNullOrEmpty(backupPath) ? string.Empty : " The previous order is saved in " + backupPath;
        }

        private static SortApplyResult Fail(SortApplyResult result, string message)
        {
            result.Success = false;
            result.Message = message;
            return result;
        }

        private sealed class SortAbortedException : Exception
        {
            public SortAbortedException(string message) : base(message) { }
        }
    }
}
