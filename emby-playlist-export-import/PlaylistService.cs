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

        public List<PlaylistItemDto> ExportPlaylistItems(User user, Guid playlistId)
        {
            var playlist = _libraryManager.GetItemById(playlistId) as Playlist;
            if (playlist == null)
            {
                _logger.Warn("Playlist with ID {0} not found for user {1}.", playlistId, user.Name);
                return new List<PlaylistItemDto>();
            }

            _logger.Info("Exporting playlist '{0}' ({1}) for user {2}.", playlist.Name, playlistId, user.Name);

            var playlistItems = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ParentIds = new[] { playlist.InternalId }
            });

            var items = new List<PlaylistItemDto>();
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

                items.Add(dto);
            }

            _logger.Info("Exported {0} items from playlist '{1}'.", items.Count, playlist.Name);
            return items;
        }

        public async Task ImportPlaylistItems(User user, string playlistName, List<PlaylistItemDto> items)
        {
            _logger.Info("Starting import of playlist '{0}' for user {1}.", playlistName, user.Name);

            var createResult = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = playlistName,
                User = user
            });

            var playlistGuid = new Guid(createResult.Id);
            var playlist = _libraryManager.GetItemById(playlistGuid);
            if (playlist == null)
            {
                _logger.Warn("Failed to retrieve newly created playlist '{0}'.", playlistName);
                return;
            }

            var playlistInternalId = playlist.InternalId;
            int added = 0, missing = 0;

            foreach (var dto in items)
            {
                try
                {
                    BaseItem item = null;

                    if (!string.IsNullOrEmpty(dto.ImdbId))
                    {
                        item = _libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            HasAnyProviderId = new[] { "Imdb:" + dto.ImdbId }
                        }).FirstOrDefault();
                    }
                    else if (dto.TmdbId.HasValue)
                    {
                        item = _libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            HasAnyProviderId = new[] { "Tmdb:" + dto.TmdbId.Value }
                        }).FirstOrDefault();
                    }
                    else if (dto.SeriesTmdbId.HasValue && dto.Season.HasValue && dto.Episode.HasValue)
                    {
                        var series = _libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            IncludeItemTypes = new[] { "Series" },
                            HasAnyProviderId = new[] { "Tmdb:" + dto.SeriesTmdbId.Value }
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
                        _logger.Info("Added '{0}' to playlist '{1}'.", item.Name, playlistName);
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

            _logger.Info("Import complete for '{0}'. Added: {1}, Missing: {2}.", playlistName, added, missing);
        }
    }
}
