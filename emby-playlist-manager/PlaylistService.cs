using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmbyPlaylistManager.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Logging;

namespace EmbyPlaylistManager
{
    public class PlaylistService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IPlaylistManager _playlistManager;
        private readonly ILogger _logger;

        public PlaylistService(
            ILibraryManager libraryManager,
            IPlaylistManager playlistManager,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _playlistManager = playlistManager;
            _logger = logger;
        }

        public void AddToPlaylist(long playlistInternalId, BaseItem item, User user)
        {
            _playlistManager.AddToPlaylist(playlistInternalId, new[] { item.InternalId }, user);
        }

        public HashSet<string> GetExistingPlaylistNames(User user)
        {
            var existing = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { "Playlist" }
            });
            return new HashSet<string>(existing.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        }

        public List<PlaylistExportDto> ExportAllPlaylists(User user)
        {
            var playlists = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { "Playlist" }
            });

            _logger.Info("Exporting {0} playlists for user {1}.", playlists.Length, user.Name);

            var result = new List<PlaylistExportDto>();
            foreach (var p in playlists)
            {
                var export = ExportPlaylist(user, p);
                result.Add(export);
            }

            _logger.Info("Export complete. {0} playlists exported.", result.Count);
            return result;
        }

        private PlaylistExportDto ExportPlaylist(User user, BaseItem playlistItem)
        {
            var export = new PlaylistExportDto
            {
                PlaylistName = playlistItem.Name,
                PlaylistId = playlistItem.Id
            };

            var playlistItems = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ListIds = new[] { playlistItem.InternalId }
            });

            foreach (var item in playlistItems)
            {
                var dto = new PlaylistItemDto { Name = item.Name };

                if (item.GetType().Name == "Episode")
                {
                    var seriesProp = item.GetType().GetProperty("SeriesProviderIds");
                    var seriesIds = seriesProp?.GetValue(item) as IDictionary<string, string>;
                    if (seriesIds != null)
                    {
                        if (seriesIds.TryGetValue("Tmdb", out var sTmdb) && int.TryParse(sTmdb, out var sTmdbId))
                            dto.SeriesTmdbId = sTmdbId;
                        if (seriesIds.TryGetValue("Tvdb", out var sTvdb) && int.TryParse(sTvdb, out var sTvdbId))
                            dto.SeriesTvdbId = sTvdbId;
                    }

                    // Fallback: walk up to the Series item directly
                    if (dto.SeriesTmdbId == null && dto.SeriesTvdbId == null)
                    {
                        var seriesIdProp = item.GetType().GetProperty("SeriesId");
                        if (seriesIdProp?.GetValue(item) is long seriesInternalId && seriesInternalId > 0)
                        {
                            var seriesItem = _libraryManager.GetItemList(new InternalItemsQuery(user)
                            {
                                IncludeItemTypes = new[] { "Series" },
                                Limit = 1
                            }).FirstOrDefault(s => s.InternalId == seriesInternalId)
                            ?? _libraryManager.GetItemList(new InternalItemsQuery(user)
                            {
                                AncestorIds = new[] { seriesInternalId },
                                IncludeItemTypes = new[] { "Series" },
                                Limit = 1
                            }).FirstOrDefault();

                            if (seriesItem?.ProviderIds != null)
                            {
                                if (seriesItem.ProviderIds.TryGetValue("Tmdb", out var st) && int.TryParse(st, out var stId))
                                    dto.SeriesTmdbId = stId;
                                if (seriesItem.ProviderIds.TryGetValue("Tvdb", out var sv) && int.TryParse(sv, out var svId))
                                    dto.SeriesTvdbId = svId;
                            }
                        }
                    }

                    var seasonProp = item.GetType().GetProperty("ParentIndexNumber");
                    var indexProp = item.GetType().GetProperty("IndexNumber");
                    if (seasonProp != null) dto.Season = (int?)seasonProp.GetValue(item);
                    if (indexProp != null) dto.Episode = (int?)indexProp.GetValue(item);
                }

                if (item.ProviderIds != null)
                {
                    foreach (var kvp in item.ProviderIds)
                        dto.ProviderIds[kvp.Key] = kvp.Value;
                }

                _logger.Info("  Item '{0}' [{1}]: ProviderIds={2}, SeriesTmdbId={3}, SeriesTvdbId={4}, S{5}E{6}",
                    dto.Name, item.GetType().Name,
                    dto.ProviderIds.Count > 0 ? string.Join(", ", dto.ProviderIds.Select(k => $"{k.Key}={k.Value}")) : "none",
                    dto.SeriesTmdbId?.ToString() ?? "none", dto.SeriesTvdbId?.ToString() ?? "none",
                    dto.Season, dto.Episode);

                export.Items.Add(dto);
            }

            _logger.Info("Playlist '{0}': {1} items exported.", export.PlaylistName, export.Items.Count);
            return export;
        }

        public async Task ImportPlaylists(User user, List<PlaylistExportDto> playlists, bool overwrite = false)
        {
            _logger.Info("Starting import of {0} playlists for user {1} (overwrite: {2}).", playlists.Count, user.Name, overwrite);

            foreach (var playlist in playlists)
            {
                await ImportPlaylist(user, playlist, overwrite);
            }

            _logger.Info("Import complete for user {0}.", user.Name);
        }

        private string GetUniqueName(string baseName, User user)
        {
            var existing = new HashSet<string>(
                _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { "Playlist" }
                }).Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);

            var candidate = baseName;
            var i = 1;
            while (existing.Contains(candidate))
                candidate = $"{baseName} ({i++})";
            return candidate;
        }

        private async Task ImportPlaylist(User user, PlaylistExportDto playlist, bool overwrite)
        {
            _logger.Info("Importing playlist '{0}' ({1} items).", playlist.PlaylistName, playlist.Items.Count);

            long playlistInternalId;

            var existing = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { "Playlist" }
            }).FirstOrDefault(p => string.Equals(p.Name, playlist.PlaylistName, StringComparison.OrdinalIgnoreCase));

            if (existing != null && overwrite)
            {
                _logger.Info("Overwriting existing playlist '{0}'.", playlist.PlaylistName);
                var existingItems = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    ListIds = new[] { existing.InternalId }
                });
                if (existingItems.Length > 0)
                {
                    var entryIds = existingItems.Select(i => i.InternalId).ToArray();
                    await _playlistManager.RemoveFromPlaylist(existing.InternalId, entryIds);
                }
                playlistInternalId = existing.InternalId;
            }
            else
            {
                var name = existing != null ? GetUniqueName(playlist.PlaylistName, user) : playlist.PlaylistName;

                var createResult = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
                {
                    Name = name,
                    User = user,
                    IsPublic = true
                });

                if (!long.TryParse(createResult.Id, out playlistInternalId))
                {
                    _logger.Warn("Could not parse playlist ID '{0}' for '{1}'. Trying Guid parse.", createResult.Id, playlist.PlaylistName);
                    if (Guid.TryParse(createResult.Id, out var playlistGuid))
                    {
                        var playlistItem = _libraryManager.GetItemById(playlistGuid);
                        if (playlistItem == null)
                        {
                            _logger.Warn("Could not find playlist '{0}' by Guid {1}.", playlist.PlaylistName, playlistGuid);
                            return;
                        }
                        playlistInternalId = playlistItem.InternalId;
                    }
                    else
                    {
                        _logger.Warn("Unrecognized playlist ID format '{0}' for '{1}'.", createResult.Id, playlist.PlaylistName);
                        return;
                    }
                }
            }
            int added = 0, missing = 0;

            foreach (var dto in playlist.Items)
            {
                try
                {
                    BaseItem item = null;

                    // Try each provider ID in priority order: Imdb first, Tmdb second, then anything else
                    var prioritized = dto.ProviderIds
                        .OrderBy(k => k.Key.Equals("Imdb", StringComparison.OrdinalIgnoreCase) ? 0 :
                                      k.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase) ? 1 : 2);

                    foreach (var providerId in prioritized)
                    {
                        item = _libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            AnyProviderIdEquals = new[] { new KeyValuePair<string, string>(providerId.Key, providerId.Value) }
                        }).FirstOrDefault();

                        if (item != null) break;
                    }

                    // Fallback: episode lookup via series provider ID + season/episode number
                    if (item == null && (dto.SeriesTmdbId.HasValue || dto.SeriesTvdbId.HasValue) && dto.Season.HasValue && dto.Episode.HasValue)
                    {
                        var seriesProviderPair = dto.SeriesTmdbId.HasValue
                            ? new KeyValuePair<string, string>("Tmdb", dto.SeriesTmdbId.Value.ToString())
                            : new KeyValuePair<string, string>("Tvdb", dto.SeriesTvdbId.Value.ToString());

                        var series = _libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            IncludeItemTypes = new[] { "Series" },
                            AnyProviderIdEquals = new[] { seriesProviderPair }
                        }).FirstOrDefault();

                        if (series != null)
                        {
                            item = _libraryManager.GetItemList(new InternalItemsQuery(user)
                            {
                                AncestorIds = new[] { series.InternalId },
                                IncludeItemTypes = new[] { "Episode" },
                                ParentIndexNumber = dto.Season,
                                IndexNumber = dto.Episode
                            }).FirstOrDefault();
                        }
                    }

                    if (item != null)
                    {
                        _playlistManager.AddToPlaylist(playlistInternalId, new[] { item.InternalId }, user);
                        _logger.Info("Added '{0}' to '{1}'.", item.Name, playlist.PlaylistName);
                        added++;
                    }
                    else
                    {
                        _logger.Warn("Could not resolve item: Name='{0}', ProviderIds={1}, SeriesTmdbId='{2}', SeriesTvdbId='{3}', S{4}E{5}",
                            dto.Name,
                            dto.ProviderIds.Count > 0 ? string.Join(", ", dto.ProviderIds.Select(k => $"{k.Key}={k.Value}")) : "none",
                            dto.SeriesTmdbId, dto.SeriesTvdbId, dto.Season, dto.Episode);
                        missing++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error importing item '{0}'", ex, dto.Name);
                }
            }

            _logger.Info("Playlist '{0}' import done. Added: {1}, Missing: {2}.", playlist.PlaylistName, added, missing);
        }
    }
}
