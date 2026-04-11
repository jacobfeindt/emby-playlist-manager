using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmbyPlaylistMigration.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Logging;

namespace EmbyPlaylistMigration
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
                    if (seriesIds != null && seriesIds.TryGetValue("Tmdb", out var sTmdb) && int.TryParse(sTmdb, out var sTmdbId))
                        dto.SeriesTmdbId = sTmdbId;

                    var seasonProp = item.GetType().GetProperty("ParentIndexNumber");
                    var indexProp = item.GetType().GetProperty("IndexNumber");
                    if (seasonProp != null) dto.Season = (int?)seasonProp.GetValue(item);
                    if (indexProp != null) dto.Episode = (int?)indexProp.GetValue(item);
                }

                if (item.ProviderIds != null)
                {
                    if (item.ProviderIds.TryGetValue("Imdb", out var imdb))
                        dto.ImdbId = imdb;
                    if (item.ProviderIds.TryGetValue("Tmdb", out var tmdb) && int.TryParse(tmdb, out var tmdbId))
                        dto.TmdbId = tmdbId;
                }

                export.Items.Add(dto);
            }

            _logger.Info("Playlist '{0}': {1} items exported.", export.PlaylistName, export.Items.Count);
            return export;
        }

        public async Task ImportPlaylists(User user, List<PlaylistExportDto> playlists)
        {
            _logger.Info("Starting import of {0} playlists for user {1}.", playlists.Count, user.Name);

            foreach (var playlist in playlists)
            {
                await ImportPlaylist(user, playlist);
            }

            _logger.Info("Import complete for user {0}.", user.Name);
        }

        private async Task ImportPlaylist(User user, PlaylistExportDto playlist)
        {
            _logger.Info("Importing playlist '{0}' ({1} items).", playlist.PlaylistName, playlist.Items.Count);

            var createResult = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = playlist.PlaylistName,
                User = user
            });

            if (!long.TryParse(createResult.Id, out var playlistInternalId))
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
            int added = 0, missing = 0;

            foreach (var dto in playlist.Items)
            {
                try
                {
                    BaseItem item = null;

                    if (!string.IsNullOrEmpty(dto.ImdbId))
                    {
                        item = _libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            AnyProviderIdEquals = new[] { new KeyValuePair<string, string>("Imdb", dto.ImdbId) }
                        }).FirstOrDefault();
                    }
                    else if (dto.TmdbId.HasValue)
                    {
                        item = _libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            AnyProviderIdEquals = new[] { new KeyValuePair<string, string>("Tmdb", dto.TmdbId.Value.ToString()) }
                        }).FirstOrDefault();
                    }
                    else if (dto.SeriesTmdbId.HasValue && dto.Season.HasValue && dto.Episode.HasValue)
                    {
                        var series = _libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            IncludeItemTypes = new[] { "Series" },
                            AnyProviderIdEquals = new[] { new KeyValuePair<string, string>("Tmdb", dto.SeriesTmdbId.Value.ToString()) }
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
                        _logger.Warn("Could not resolve item: Name='{0}', ImdbId='{1}', TmdbId='{2}', SeriesTmdbId='{3}', S{4}E{5}",
                            dto.Name, dto.ImdbId, dto.TmdbId, dto.SeriesTmdbId, dto.Season, dto.Episode);
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
