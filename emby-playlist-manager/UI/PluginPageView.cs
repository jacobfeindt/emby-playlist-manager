using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using EmbyPlaylistManager.Models;
using EmbyPlaylistManager.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;

namespace EmbyPlaylistManager.UI
{
    internal class PluginPageView : UIBaseClasses.Views.PluginPageView
    {
        private readonly OptionsStore store;
        private readonly IServerApplicationHost appHost;
        private readonly IUserManager userManager;
        private readonly ILibraryManager libraryManager;
        private readonly PlaylistService playlistService;
        private readonly ILogger logger;

        public PluginPageView(
            PluginInfo pluginInfo,
            OptionsStore store,
            IServerApplicationHost appHost,
            IUserManager userManager,
            ILibraryManager libraryManager,
            PlaylistService playlistService,
            ILogger logger)
            : base(pluginInfo.Id)
        {
            this.store = store;
            this.appHost = appHost;
            this.userManager = userManager;
            this.libraryManager = libraryManager;
            this.playlistService = playlistService;
            this.logger = logger;
            this.ContentData = store.GetOptions();
        }

        private PluginOptions Options => this.ContentData as PluginOptions;

        public override bool IsCommandAllowed(string commandKey)
        {
            if (commandKey == "Preview") return true;
            return base.IsCommandAllowed(commandKey);
        }

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            switch (commandId)
            {
                case "Export":
                    Task.Run(HandleExport);
                    return Task.FromResult<IPluginUIView>(this);
                case "Import":
                    Task.Run(HandleImport);
                    return Task.FromResult<IPluginUIView>(this);
                case "Preview":
                    HandlePreview();
                    return Task.FromResult<IPluginUIView>(this);
            }
            return base.RunCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            // Clear transient display state before persisting
            this.Options.PreviewList.Clear();
            this.Options.Status.StatusText = "No operation started yet.";
            this.Options.Status.Status = ItemStatus.Unavailable;
            store.SetOptions(this.Options);
            return base.OnSaveCommand(itemId, commandId, data);
        }

        private void HandlePreview()
        {
            this.Options.PreviewList.Clear();

            if (string.IsNullOrWhiteSpace(this.Options.ImportFilePath) || !File.Exists(this.Options.ImportFilePath))
            {
                RaiseUIViewInfoChanged();
                return;
            }

            try
            {
                var json = File.ReadAllText(this.Options.ImportFilePath);
                var playlists = JsonSerializer.Deserialize<List<PlaylistExportDto>>(json);

                if (playlists == null || playlists.Count == 0)
                {
                    this.Options.PreviewList.Add(new GenericListItem { PrimaryText = "No playlists found in file.", Status = ItemStatus.Warning });
                    RaiseUIViewInfoChanged();
                    return;
                }

                var existingNames = GetExistingPlaylistNames();
                var overwrite = this.Options.OverwriteExisting;

                foreach (var playlist in playlists)
                {
                    var exists = existingNames.Contains(playlist.PlaylistName, StringComparer.OrdinalIgnoreCase);
                    string secondaryText;
                    ItemStatus status;
                    string icon;

                    if (!exists)
                    {
                        secondaryText = $"{playlist.Items.Count} items — will be imported";
                        status = ItemStatus.Succeeded;
                        icon = IconNames.playlist_add;
                    }
                    else if (overwrite)
                    {
                        secondaryText = $"{playlist.Items.Count} items — will be overwritten";
                        status = ItemStatus.Warning;
                        icon = IconNames.warning;
                    }
                    else
                    {
                        secondaryText = $"{playlist.Items.Count} items — will be imported as '{GetPreviewUniqueName(playlist.PlaylistName, existingNames)}'";
                        status = ItemStatus.Succeeded;
                        icon = IconNames.playlist_add;
                    }

                    this.Options.PreviewList.Add(new GenericListItem
                    {
                        PrimaryText = playlist.PlaylistName,
                        SecondaryText = secondaryText,
                        Status = status,
                        Icon = icon,
                        IconMode = ItemListIconMode.SmallRegular
                    });
                }
            }
            catch (Exception ex)
            {
                this.Options.PreviewList.Add(new GenericListItem
                {
                    PrimaryText = $"Could not read file: {ex.Message}",
                    Status = ItemStatus.Failed,
                    Icon = IconNames.error,
                    IconMode = ItemListIconMode.SmallRegular
                });
            }

            RaiseUIViewInfoChanged();
        }

        private async Task HandleExport()
        {
            SetStatus("Exporting...", ItemStatus.InProgress);

            try
            {
                if (string.IsNullOrWhiteSpace(this.Options.ExportFolder))
                {
                    SetStatus("Export failed: Output folder is required.", ItemStatus.Failed);
                    return;
                }

                var user = GetUser();
                var playlists = playlistService.ExportAllPlaylists(user);

                if (playlists.Count == 0)
                {
                    SetStatus("No playlists found to export.", ItemStatus.Warning);
                    return;
                }

                var serverName = string.Concat(appHost.FriendlyName.Split(Path.GetInvalidFileNameChars()));
                var json = JsonSerializer.Serialize(playlists, new JsonSerializerOptions { WriteIndented = true });
                var filePath = Path.Combine(this.Options.ExportFolder, $"{serverName}-playlists-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                await File.WriteAllTextAsync(filePath, json);

                SetStatus($"Exported {playlists.Count} playlists to {filePath}", ItemStatus.Succeeded);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Export failed", ex);
                SetStatus($"Export failed: {ex.Message}", ItemStatus.Failed);
            }
        }

        private async Task HandleImport()
        {
            SetStatus("Importing...", ItemStatus.InProgress);

            try
            {
                if (string.IsNullOrWhiteSpace(this.Options.ImportFilePath))
                {
                    SetStatus("Import failed: File path is required.", ItemStatus.Failed);
                    return;
                }

                if (!File.Exists(this.Options.ImportFilePath))
                {
                    SetStatus($"Import failed: File not found: {this.Options.ImportFilePath}", ItemStatus.Failed);
                    return;
                }

                var json = await File.ReadAllTextAsync(this.Options.ImportFilePath);
                var playlists = JsonSerializer.Deserialize<List<PlaylistExportDto>>(json);

                if (playlists == null || playlists.Count == 0)
                {
                    SetStatus("Import failed: No playlists found in file.", ItemStatus.Failed);
                    return;
                }

                var existingNames = GetExistingPlaylistNames();
                var overwrite = this.Options.OverwriteExisting;

                var user = GetUser();
                await playlistService.ImportPlaylists(user, playlists, overwrite);

                var msg = $"Import complete: {playlists.Count} playlists processed.";

                SetStatus(msg, ItemStatus.Succeeded);
                HandlePreview();
            }
            catch (Exception ex)
            {
                logger.ErrorException("Import failed", ex);
                SetStatus($"Import failed: {ex.Message}", ItemStatus.Failed);
            }
        }

        private string GetPreviewUniqueName(string baseName, HashSet<string> existingNames)
        {
            var candidate = baseName;
            var i = 1;
            while (existingNames.Contains(candidate))
                candidate = $"{baseName} ({i++})";
            return candidate;
        }

        private HashSet<string> GetExistingPlaylistNames()
        {
            return playlistService.GetExistingPlaylistNames(GetUser());
        }

        private User GetUser()
        {
            return this.User != null
                ? userManager.GetUserById(this.User.Id)
                : userManager.GetUserList(null)[0];
        }

        private void SetStatus(string message, ItemStatus status)
        {
            this.Options.Status.StatusText = message;
            this.Options.Status.Status = status;
            this.Options.ExportButton.IsEnabled = status != ItemStatus.InProgress;
            this.Options.ImportButton.IsEnabled = status != ItemStatus.InProgress;
            RaiseUIViewInfoChanged();
        }
    }
}
