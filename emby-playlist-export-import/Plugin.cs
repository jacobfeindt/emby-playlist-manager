using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using EmbyPlaylistMigration.Models;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.UserData;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.System;

namespace EmbyPlaylistMigration
{
    public class Plugin : IServerEntryPoint
    {
        private readonly IServerApplicationHost _appHost;
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private PlaylistService _playlistService;

        public Plugin(
            IServerApplicationHost appHost,
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            ILogManager logManager)
        {
            _appHost = appHost;
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger("PlaylistMigrationPlugin");
        }

        public void Run()
        {
            _playlistService = new PlaylistService(_userDataManager, _libraryManager, _logger);
            // Register HTTP endpoints
            ApiEntryPoint.RegisterEndpoint("GET", "/playlists/export", ExportPlaylist);
            ApiEntryPoint.RegisterEndpoint("POST", "/playlists/import", ImportPlaylist);
            _logger.Info("PlaylistMigrationPlugin started.");
        }

        public void Dispose()
        {
            _logger.Info("PlaylistMigrationPlugin stopped.");
        }

        private object ExportPlaylist(IRequest request)
        {
            try
            {
                var user = request.GetAuthenticatedUser();
                if (user == null)
                    return new HttpResult { StatusCode = 401, Response = "Unauthorized" };
                if (!Guid.TryParse(request.QueryString["playlistId"], out var playlistId))
                    return new HttpResult { StatusCode = 400, Response = "Invalid playlistId" };
                var items = _playlistService.ExportPlaylistItems(user, playlistId);
                var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
                var filePath = Path.Combine(_appHost.ServerApplicationPaths.PluginsPath, $"playlist-{playlistId}.json");
                File.WriteAllText(filePath, json);
                _logger.Info($"Exported playlist {playlistId} to {filePath}.");
                return new HttpResult { StatusCode = 200, Response = json, ContentType = "application/json" };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error exporting playlist", ex);
                return new HttpResult { StatusCode = 500, Response = "Internal Server Error" };
            }
        }

        private async Task<object> ImportPlaylist(IRequest request)
        {
            try
            {
                var user = request.GetAuthenticatedUser();
                if (user == null)
                    return new HttpResult { StatusCode = 401, Response = "Unauthorized" };
                var playlistName = request.QueryString["name"] ?? $"Imported Playlist {DateTime.Now:yyyyMMddHHmmss}";
                using var reader = new StreamReader(request.RequestStream);
                var body = await reader.ReadToEndAsync();
                var items = JsonSerializer.Deserialize<List<PlaylistItemDto>>(body);
                if (items == null || items.Count == 0)
                    return new HttpResult { StatusCode = 400, Response = "No playlist items provided" };
                await _playlistService.ImportPlaylistItems(user, playlistName, items);
                return new HttpResult { StatusCode = 200, Response = "Import successful" };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error importing playlist", ex);
                return new HttpResult { StatusCode = 500, Response = "Internal Server Error" };
            }
        }
    }
}
