using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

        public void UpdateShadow(User user, BaseItem playlistItem, string triggerEvent, bool cleanDuplicates = true)
        {
            try
            {
                var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    ListIds = new[] { playlistItem.InternalId }
                });

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
