using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EmbyPlaylistMigration.Models;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbyPlaylistMigration.Services
{
    [Authenticated]
    public class PlaylistMigrationApi : IService
    {
        private readonly IApplicationHost _appHost;
        private readonly IUserManager _userManager;

        public PlaylistMigrationApi(IApplicationHost appHost, IUserManager userManager)
        {
            _appHost = appHost;
            _userManager = userManager;
        }

        private Plugin GetPlugin() =>
            _appHost.Plugins.OfType<Plugin>().FirstOrDefault()
            ?? throw new InvalidOperationException("Playlist Export Import plugin is not loaded.");

        public async Task<ExportPlaylistsResponse> Get(ExportPlaylistsRequest request)
        {
            var plugin = GetPlugin();
            var user = _userManager.GetUserList(null)[0];

            var playlists = plugin.PlaylistService.ExportAllPlaylists(user);

            var outputFolder = !string.IsNullOrWhiteSpace(request.OutputFolder)
                ? request.OutputFolder
                : plugin.OptionsStore.GetOptions().ExportFolder;

            if (string.IsNullOrWhiteSpace(outputFolder))
                throw new ArgumentException("No output folder specified and none configured in plugin settings.");

            var serverName = string.Concat(
                (_appHost as MediaBrowser.Controller.IServerApplicationHost)?.FriendlyName?.Split(Path.GetInvalidFileNameChars()) ?? new[] { "Emby" });

            var json = JsonSerializer.Serialize(playlists, new JsonSerializerOptions { WriteIndented = true });
            var filePath = Path.Combine(outputFolder, $"{serverName}-playlists-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(filePath, json);

            return new ExportPlaylistsResponse
            {
                FilePath = filePath,
                PlaylistCount = playlists.Count
            };
        }

        public async Task<ImportPlaylistsResponse> Post(ImportPlaylistsRequest request)
        {
            if (request.Playlists == null || request.Playlists.Count == 0)
                throw new ArgumentException("No playlists provided in request body.");

            var plugin = GetPlugin();
            var user = _userManager.GetUserList(null)[0];

            var existingNames = plugin.PlaylistService.GetExistingPlaylistNames(user);
            var toImport = request.Playlists
                .Where(p => !existingNames.Contains(p.PlaylistName, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var skipped = request.Playlists.Count - toImport.Count;

            if (toImport.Count > 0)
                await plugin.PlaylistService.ImportPlaylists(user, toImport);

            return new ImportPlaylistsResponse
            {
                Imported = toImport.Count,
                Skipped = skipped
            };
        }
    }
}
