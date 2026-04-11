using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmbyPlaylistMigration.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.UserData;
using MediaBrowser.Model.Logging;

namespace EmbyPlaylistMigration
{
    public class PlaylistService
    {
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public PlaylistService(
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            ILogger logger)
        {
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public List<PlaylistItemDto> ExportPlaylistItems(User user, Guid playlistId)
        {
            var playlists = _userDataManager.GetPlaylists(user.Id);
            var playlist = playlists.FirstOrDefault(p => p.Id == playlistId);
            if (playlist == null)
            {
                _logger.Warn($"Playlist with ID {playlistId} not found for user {user.Name}.");
                return new List<PlaylistItemDto>();
            }
            _logger.Info($"Exporting playlist '{playlist.Name}' ({playlistId}) for user {user.Name}.");
            var items = new List<PlaylistItemDto>();
            foreach (var itemId in playlist.Items)
            {
                var item = _libraryManager.GetItemById(itemId);
                if (item == null) continue;
                var dto = new PlaylistItemDto
                {
                    Name = item.Name
                };
                if (item is Episode episode)
                {
                    dto.SeriesTmdbId = episode.Series?.ProviderIds != null && episode.Series.ProviderIds.TryGetValue("Tmdb", out var sTmdb) && int.TryParse(sTmdb, out var sTmdbId) ? sTmdbId : (int?)null;
                    dto.Season = episode.SeasonNumber;
                    dto.Episode = episode.IndexNumber;
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
            _logger.Info($"Exported {items.Count} items from playlist '{playlist.Name}'.");
            return items;
        }

        public async Task ImportPlaylistItems(User user, string playlistName, List<PlaylistItemDto> items)
        {
            _logger.Info($"Starting import of playlist '{playlistName}' for user {user.Name}.");
            var playlist = _userDataManager.CreatePlaylist(user.Id, playlistName, new List<Guid>());
            int added = 0, missing = 0;
            foreach (var dto in items)
            {
                try
                {
                    BaseItem item = null;
                    if (!string.IsNullOrEmpty(dto.ImdbId))
                    {
                        item = _libraryManager.GetItemByProviderId("Imdb", dto.ImdbId);
                    }
                    else if (dto.TmdbId.HasValue)
                    {
                        item = _libraryManager.GetItemByProviderId("Tmdb", dto.TmdbId.Value.ToString());
                    }
                    else if (dto.SeriesTmdbId.HasValue && dto.Season.HasValue && dto.Episode.HasValue)
                    {
                        var series = _libraryManager.GetItemByProviderId("Tmdb", dto.SeriesTmdbId.Value.ToString());
                        if (series != null)
                        {
                            item = series.GetChildren().FirstOrDefault(e =>
                                e is Episode ep &&
                                ep.SeasonNumber == dto.Season &&
                                ep.IndexNumber == dto.Episode);
                        }
                    }
                    if (item != null)
                    {
                        _userDataManager.AddItemsToPlaylist(user.Id, playlist.Id, new List<Guid> { item.Id });
                        _logger.Info($"Added item '{item.Name}' to playlist '{playlistName}'.");
                        added++;
                    }
                    else
                    {
                        _logger.Warn($"Could not resolve item: Name='{dto.Name}', ImdbId='{dto.ImdbId}', TmdbId='{dto.TmdbId}', SeriesTmdbId='{dto.SeriesTmdbId}', Season='{dto.Season}', Episode='{dto.Episode}'");
                        missing++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"Error importing item: Name='{dto.Name}'", ex);
                }
            }
            _logger.Info($"Import complete. Added: {added}, Missing: {missing}.");
        }
    }
}
