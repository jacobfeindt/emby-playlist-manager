using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using EmbyPlaylistManager.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace EmbyPlaylistManager.Storage
{
    public class ShadowPlaylistService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationPaths _appPaths;
        private readonly ILogger _logger;

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        public ShadowPlaylistService(ILibraryManager libraryManager, IApplicationPaths appPaths, ILogger logger)
        {
            _libraryManager = libraryManager;
            _appPaths = appPaths;
            _logger = logger;
        }

        public string ShadowFolder => Path.Combine(_appPaths.DataPath, "PlaylistManager", "shadows");
        public string BackupCopyFolder => Path.Combine(_appPaths.DataPath, "userplaylists");

        public bool EnsureShadowFolder()
        {
            try
            {
                Directory.CreateDirectory(ShadowFolder);
                RestoreFromBackupCopiesIfNeeded();
                CleanAllStaleDuplicates();
                SyncBackupCopies();
                return true;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Shadow folder could not be created at {0}. Shadow system will be disabled.", ex, ShadowFolder);
                return false;
            }
        }

        private void CleanAllStaleDuplicates()
        {
            try
            {
                // Group all shadow files by playlist name, keep newest per name, delete the rest
                var files = Directory.GetFiles(ShadowFolder, "*.json")
                    .Select(f => new { Path = f, Written = File.GetLastWriteTime(f) })
                    .ToList();

                var byName = new Dictionary<string, (string Path, DateTime Written, Guid Id)>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var f in files)
                {
                    try
                    {
                        var dto = JsonSerializer.Deserialize<PlaylistExportDto>(File.ReadAllText(f.Path));
                        if (dto == null) continue;
                        if (!byName.TryGetValue(dto.PlaylistName, out var existing) ||
                            f.Written > existing.Written)
                        {
                            // Queue old one for deletion if we're replacing it
                            if (byName.ContainsKey(dto.PlaylistName))
                            {
                                DeleteStaleFile(existing.Path, dto.PlaylistName, existing.Id);
                            }
                            byName[dto.PlaylistName] = (f.Path, f.Written, dto.PlaylistId);
                        }
                        else
                        {
                            DeleteStaleFile(f.Path, dto.PlaylistName, dto.PlaylistId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("CleanAllStaleDuplicates: error reading '{0}'", ex, f.Path);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("CleanAllStaleDuplicates failed", ex);
            }
        }

        private void DeleteStaleFile(string path, string playlistName, Guid playlistId)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                var backup = BackupCopyFilePath(playlistName, playlistId);
                if (File.Exists(backup)) File.Delete(backup);
                _logger.Info("Cleaned stale shadow for '{0}' (ID: {1}).", playlistName, playlistId);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Could not delete stale shadow for '{0}'", ex, playlistName);
            }
        }

        private void RestoreFromBackupCopiesIfNeeded()
        {
            try
            {
                if (!ShadowFolderIsEmpty()) return;

                var backupCopies = Directory.GetFiles(BackupCopyFolder, "*.shadow.json");
                if (backupCopies.Length == 0) return;

                _logger.Info("Shadow folder is empty but {0} backup copies found in userplaylists. Restoring...", backupCopies.Length);

                foreach (var backupPath in backupCopies)
                {
                    try
                    {
                        var masterFileName = Path.GetFileNameWithoutExtension(backupPath)
                            .Replace(".shadow", "") + ".json";
                        var masterPath = Path.Combine(ShadowFolder, masterFileName);
                        File.Copy(backupPath, masterPath);
                        _logger.Info("Shadow restored from backup: '{0}'", masterFileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("Shadow restore failed for '{0}'", ex, backupPath);
                    }
                }

                _logger.Info("Shadow restore complete. {0} files restored.", backupCopies.Length);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Shadow restore from backup copies failed", ex);
            }
        }

        private void SyncBackupCopies()
        {
            try
            {
                foreach (var masterPath in Directory.GetFiles(ShadowFolder, "*.json"))
                {
                    try
                    {
                        var fileName = Path.GetFileNameWithoutExtension(masterPath);
                        var backupPath = Path.Combine(BackupCopyFolder, fileName + ".shadow.json");
                        if (!File.Exists(backupPath))
                        {
                            File.Copy(masterPath, backupPath);
                            _logger.Info("Shadow backup copy synced: '{0}'", fileName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("Shadow backup copy sync failed for '{0}'", ex, masterPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Shadow backup copy sync failed", ex);
            }
        }

        // ---- Update coalescing -------------------------------------------------------------------------------------
        // Playlist events arrive once per added/removed/moved row. Writing the shadow on every event rewrote the same
        // files from many threads at once (and N times for N rows), so events are collapsed into one write per playlist
        // after a short quiet period, and writes for one playlist are serialized.

        private static readonly TimeSpan CoalesceDelay = TimeSpan.FromSeconds(3);

        private sealed class PendingUpdate
        {
            public Timer Timer;
            public User User;
            public BaseItem Playlist;
            public string Trigger;
        }

        private readonly ConcurrentDictionary<Guid, PendingUpdate> _pending = new ConcurrentDictionary<Guid, PendingUpdate>();
        private readonly ConcurrentDictionary<Guid, object> _writeLocks = new ConcurrentDictionary<Guid, object>();
        private readonly ConcurrentDictionary<long, int> _suppressed = new ConcurrentDictionary<long, int>();

        private object GetWriteLock(Guid playlistId)
        {
            return _writeLocks.GetOrAdd(playlistId, _ => new object());
        }

        private bool IsSuppressed(long playlistInternalId)
        {
            return _suppressed.TryGetValue(playlistInternalId, out var n) && n > 0;
        }

        /// <summary>
        /// Schedules a shadow write for the playlist. Repeated calls within the coalescing window restart the timer, so a burst
        /// of events produces a single write. Ignored while the playlist is suppressed (see <see cref="Suppress"/>).
        /// </summary>
        public void RequestUpdate(User user, BaseItem playlistItem, string triggerEvent)
        {
            if (user == null || playlistItem == null) return;
            if (IsSuppressed(playlistItem.InternalId)) return;

            var pending = _pending.GetOrAdd(playlistItem.Id, id =>
            {
                var p = new PendingUpdate();
                p.Timer = new Timer(FirePending, id, Timeout.Infinite, Timeout.Infinite);
                return p;
            });
            lock (pending)
            {
                pending.User = user;
                pending.Playlist = playlistItem;
                pending.Trigger = triggerEvent;
                pending.Timer.Change(CoalesceDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private void FirePending(object state)
        {
            var id = (Guid)state;
            if (!_pending.TryGetValue(id, out var pending)) return;
            User user; BaseItem playlist; string trigger;
            lock (pending)
            {
                user = pending.User;
                playlist = pending.Playlist;
                trigger = pending.Trigger;
            }
            try
            {
                if (playlist == null || IsSuppressed(playlist.InternalId)) return;
                UpdateShadow(user, playlist, trigger);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Coalesced shadow update failed for playlist {0}", ex, id);
            }
        }

        /// <summary>
        /// Suppresses event-driven shadow writes for one playlist until the returned token is disposed. Used by operations that
        /// change a playlist many times (sorting) and then write the shadow once themselves.
        /// </summary>
        public IDisposable Suppress(long playlistInternalId)
        {
            _suppressed.AddOrUpdate(playlistInternalId, 1, (k, v) => v + 1);
            return new SuppressToken(this, playlistInternalId);
        }

        private sealed class SuppressToken : IDisposable
        {
            private readonly ShadowPlaylistService _owner;
            private readonly long _id;
            private int _disposed;

            public SuppressToken(ShadowPlaylistService owner, long id)
            {
                _owner = owner;
                _id = id;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                int remaining = _owner._suppressed.AddOrUpdate(_id, 0, (k, v) => Math.Max(0, v - 1));
                if (remaining == 0) _owner._suppressed.TryRemove(_id, out _);
            }
        }

        public void UpdateShadow(User user, BaseItem playlistItem, string triggerEvent, bool cleanDuplicates = true)
        {
            // Writes for one playlist are serialized so two callers can never interleave on the same shadow and backup files.
            lock (GetWriteLock(playlistItem.Id))
            {
                UpdateShadowCore(user, playlistItem, triggerEvent, cleanDuplicates);
            }
        }

        private void UpdateShadowCore(User user, BaseItem playlistItem, string triggerEvent, bool cleanDuplicates)
        {
            try
            {
                var items = PlaylistQueries.GetOrderedEntries(_libraryManager, user, playlistItem.InternalId, _logger);

                var dto = new PlaylistExportDto
                {
                    PlaylistName = playlistItem.Name,
                    PlaylistId = playlistItem.Id
                };

                foreach (var item in items)
                {
                    dto.Items.Add(PlaylistItemDto.FromItem(item, user, _libraryManager));
                }

                var path = ShadowFilePath(playlistItem.Name, playlistItem.Id);
                var json = JsonSerializer.Serialize(dto, _jsonOptions);
                File.WriteAllText(path, json);

                // Only clean duplicates when triggered by a live event — not during initialization
                // (multiple playlists can legitimately share a name)
                if (cleanDuplicates)
                    CleanStaleDuplicates(playlistItem.Name, playlistItem.Id);

                // Backup copy into userplaylists so MBBackup picks it up
                try
                {
                    var backupPath = BackupCopyFilePath(playlistItem.Name, playlistItem.Id);
                    File.WriteAllText(backupPath, json);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Shadow backup copy failed for '{0}'", ex, playlistItem.Name);
                }

                _logger.Info("Shadow updated: '{0}' ({1} items) [triggered by {2}]", playlistItem.Name, dto.Items.Count, triggerEvent);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Shadow update failed for playlist '{0}'", ex, playlistItem.Name);
            }
        }

        public void InitializeAllShadows(User user)
        {
            try
            {
                var playlists = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { "Playlist" }
                });

                foreach (var playlist in playlists)
                    UpdateShadow(user, playlist, "InitializeAllShadows", cleanDuplicates: false);

                _logger.Info("Shadow initialized: {0} playlists written on startup.", playlists.Length);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Shadow initialization failed", ex);
            }
        }

        public void InitializeMissingShadows(User user)
        {
            try
            {
                var existingIds = new HashSet<Guid>(
                    Directory.GetFiles(ShadowFolder, "*.json")
                        .Select(f =>
                        {
                            try
                            {
                                var dto = JsonSerializer.Deserialize<PlaylistExportDto>(File.ReadAllText(f));
                                return dto?.PlaylistId ?? Guid.Empty;
                            }
                            catch { return Guid.Empty; }
                        })
                        .Where(id => id != Guid.Empty));

                var playlists = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { "Playlist" }
                });

                var missing = playlists.Where(p => !existingIds.Contains(p.Id)).ToArray();
                foreach (var playlist in missing)
                    UpdateShadow(user, playlist, "InitializeMissingShadows", cleanDuplicates: false);

                if (missing.Length > 0)
                    _logger.Info("Shadow initialized {0} new playlists on startup.", missing.Length);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("InitializeMissingShadows failed", ex);
            }
        }

        public List<PlaylistExportDto> GetAllShadows()
        {
            var result = new List<PlaylistExportDto>();
            try
            {
                foreach (var file in Directory.GetFiles(ShadowFolder, "*.json"))
                {
                    try
                    {
                        var dto = JsonSerializer.Deserialize<PlaylistExportDto>(File.ReadAllText(file));
                        if (dto != null) result.Add(dto);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("Could not read shadow file '{0}'", ex, file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Could not enumerate shadow folder", ex);
            }
            return result;
        }

        public void DeleteShadow(string playlistName, Guid playlistId)
        {
            try
            {
                var path = ShadowFilePath(playlistName, playlistId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logger.Info("Shadow deleted for playlist '{0}'.", playlistName);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Could not delete shadow for playlist '{0}'", ex, playlistName);
            }

            try
            {
                var backupPath = BackupCopyFilePath(playlistName, playlistId);
                if (File.Exists(backupPath)) File.Delete(backupPath);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Could not delete shadow backup copy for playlist '{0}'", ex, playlistName);
            }
        }

        public bool ShadowFolderIsEmpty()
        {
            try { return !Directory.GetFiles(ShadowFolder, "*.json").Any(); }
            catch { return true; }
        }

        private void CleanStaleDuplicates(string playlistName, Guid currentId)
        {
            try
            {
                var safeName = string.Concat(playlistName.Split(Path.GetInvalidFileNameChars()));
                foreach (var file in Directory.GetFiles(ShadowFolder, $"{safeName}-*.json"))
                {
                    var fileId = Path.GetFileNameWithoutExtension(file)
                        .Substring(safeName.Length + 1); // strip "{name}-"
                    if (Guid.TryParse(fileId, out var guid) && guid != currentId)
                    {
                        try
                        {
                            File.Delete(file);
                            var backupPath = BackupCopyFilePath(playlistName, guid);
                            if (File.Exists(backupPath)) File.Delete(backupPath);
                            _logger.Info("Cleaned stale shadow for '{0}' (old ID: {1}).", playlistName, guid);
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorException("Could not delete stale shadow for '{0}'", ex, playlistName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("CleanStaleDuplicates failed for '{0}'", ex, playlistName);
            }
        }
        private string ShadowFilePath(string playlistName, Guid playlistId)
        {
            var safeName = string.Concat(playlistName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(ShadowFolder, $"{safeName}-{playlistId:N}.json");
        }

        private string BackupCopyFilePath(string playlistName, Guid playlistId)
        {
            var safeName = string.Concat(playlistName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(BackupCopyFolder, $"{safeName}-{playlistId:N}.shadow.json");
        }
    }
}
