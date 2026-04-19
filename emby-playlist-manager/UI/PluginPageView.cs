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
        private readonly ShadowPlaylistService shadowService;
        private readonly ILogger logger;

        public PluginPageView(
            PluginInfo pluginInfo,
            OptionsStore store,
            IServerApplicationHost appHost,
            IUserManager userManager,
            ILibraryManager libraryManager,
            PlaylistService playlistService,
            ShadowPlaylistService shadowService,
            ILogger logger)
            : base(pluginInfo.Id)
        {
            this.store = store;
            this.appHost = appHost;
            this.userManager = userManager;
            this.libraryManager = libraryManager;
            this.playlistService = playlistService;
            this.shadowService = shadowService;
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
                    if (string.IsNullOrWhiteSpace(this.Options.ExportFolder))
                    {
                        SetStatus("Export failed: Output folder is required.", ItemStatus.Failed);
                        return Task.FromResult<IPluginUIView>(this);
                    }
                    if (!Directory.Exists(this.Options.ExportFolder))
                    {
                        SetStatus("Export failed: Output folder does not exist.", ItemStatus.Failed);
                        return Task.FromResult<IPluginUIView>(this);
                    }
                    if (!IsDirectoryWritable(this.Options.ExportFolder))
                    {
                        SetStatus("Export failed: Output folder is not writable.", ItemStatus.Failed);
                        return Task.FromResult<IPluginUIView>(this);
                    }
                    Task.Run(HandleExport);
                    return Task.FromResult<IPluginUIView>(this);
                case "Import":
                    if (string.IsNullOrWhiteSpace(this.Options.ImportFilePath))
                    {
                        SetStatus("Import failed: File path is required.", ItemStatus.Failed);
                        return Task.FromResult<IPluginUIView>(this);
                    }
                    Task.Run(HandleImport);
                    return Task.FromResult<IPluginUIView>(this);
                case "Preview":
                    HandlePreview();
                    return Task.FromResult<IPluginUIView>(this);
                case "Scan":
                    if (!this.Options.EnableShadow)
                    {
                        SetShadowStatus("Shadow system is disabled. Enable it first.", ItemStatus.Warning);
                        return Task.FromResult<IPluginUIView>(this);
                    }
                    Task.Run(HandleScan);
                    return Task.FromResult<IPluginUIView>(this);
                case "RepairAll":
                    if (!this.Options.EnableShadow)
                    {
                        SetShadowStatus("Shadow system is disabled. Enable it first.", ItemStatus.Warning);
                        return Task.FromResult<IPluginUIView>(this);
                    }
                    Task.Run(HandleRepairAll);
                    return Task.FromResult<IPluginUIView>(this);
                case "ExportMissing":
                    if (this.Options.MissingItemsList.Count == 0)
                    {
                        SetShadowStatus("No missing items to export. Run Repair All first.", ItemStatus.Warning);
                        return Task.FromResult<IPluginUIView>(this);
                    }
                    Task.Run(HandleExportMissing);
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
            this.Options.RepairPreviewList.Clear();
            this.Options.MissingItemsList.Clear();
            this.Options.ShadowStatus.StatusText = this.Options.EnableShadow ? "Shadow system enabled." : "Shadow system is disabled.";
            this.Options.ShadowStatus.Status = this.Options.EnableShadow ? ItemStatus.Succeeded : ItemStatus.Unavailable;
            store.SetOptions(this.Options);
            return base.OnSaveCommand(itemId, commandId, data);
        }

        private void HandlePreview()
        {
            this.Options.PreviewList.Clear();
            this.Options.SelectedFileLabel.Text = string.IsNullOrEmpty(this.Options.ImportFilePath)
                ? "No file selected."
                : this.Options.ImportFilePath;

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
                    IconNames? icon;

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

        private BaseItem FindLivePlaylist(User user, PlaylistExportDto shadow)
        {
            var allPlaylists = libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { "Playlist" }
            });

            // Match by GUID first — exact, unambiguous
            if (shadow.PlaylistId != Guid.Empty)
            {
                var byId = allPlaylists.FirstOrDefault(p => p.Id == shadow.PlaylistId);
                if (byId != null) return byId;
            }

            // Fall back to name — used after restore/migration where GUIDs change
            return allPlaylists.FirstOrDefault(p =>
                string.Equals(p.Name, shadow.PlaylistName, StringComparison.OrdinalIgnoreCase));
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
                : userManager.GetUserList(new MediaBrowser.Model.Querying.UserQuery())[0];
        }

        private void SetStatus(string message, ItemStatus status)
        {
            this.Options.Status.StatusText = message;
            this.Options.Status.Status = status;
            this.Options.ExportButton.IsEnabled = status != ItemStatus.InProgress;
            this.Options.ImportButton.IsEnabled = status != ItemStatus.InProgress;
            RaiseUIViewInfoChanged();
        }

        private void SetShadowStatus(string message, ItemStatus status)
        {
            this.Options.ShadowStatus.StatusText = message;
            this.Options.ShadowStatus.Status = status;
            this.Options.ScanButton.IsEnabled = status != ItemStatus.InProgress;
            this.Options.RepairAllButton.IsEnabled = status != ItemStatus.InProgress;
            this.Options.ExportMissingButton.IsEnabled = status != ItemStatus.InProgress;
            RaiseUIViewInfoChanged();
        }

        private async Task HandleExportMissing()
        {
            SetShadowStatus("Exporting missing items...", ItemStatus.InProgress);
            try
            {
                if (string.IsNullOrWhiteSpace(this.Options.ExportFolder))
                {
                    SetShadowStatus("Export failed: set an Export Output Folder in the Export section first.", ItemStatus.Failed);
                    return;
                }

                // Build List<PlaylistExportDto> from MissingItemsList entries
                // Each PrimaryText is playlist name, SecondaryText is "{name} [{providerIds}]"
                // Re-read from shadow files to get full PlaylistItemDto objects
                var shadows = shadowService.GetAllShadows();
                var user = GetUser();
                var missingByPlaylist = new Dictionary<string, PlaylistExportDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in this.Options.MissingItemsList)
                {
                    var playlistName = item.PrimaryText;
                    var shadow = shadows.FirstOrDefault(s =>
                        string.Equals(s.PlaylistName, playlistName, StringComparison.OrdinalIgnoreCase));
                    if (shadow == null) continue;

                    if (!missingByPlaylist.TryGetValue(playlistName, out var dto))
                    {
                        dto = new PlaylistExportDto { PlaylistName = playlistName, PlaylistId = shadow.PlaylistId };
                        missingByPlaylist[playlistName] = dto;
                    }

                    // Match shadow item by secondary text prefix (name)
                    var itemName = item.SecondaryText?.Split('[')[0].Trim();
                    var shadowItem = shadow.Items.FirstOrDefault(i =>
                        string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase));
                    if (shadowItem != null) dto.Items.Add(shadowItem);
                }

                if (missingByPlaylist.Count == 0)
                {
                    SetShadowStatus("No missing items to export.", ItemStatus.Warning);
                    return;
                }

                var serverName = string.Concat(appHost.FriendlyName.Split(Path.GetInvalidFileNameChars()));
                var json = JsonSerializer.Serialize(missingByPlaylist.Values.ToList(),
                    new JsonSerializerOptions { WriteIndented = true });
                var filePath = Path.Combine(this.Options.ExportFolder,
                    $"{serverName}-missing-items-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                await File.WriteAllTextAsync(filePath, json);

                SetShadowStatus($"Exported {missingByPlaylist.Values.Sum(d => d.Items.Count)} missing items to {filePath}",
                    ItemStatus.Succeeded);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Export missing items failed", ex);
                SetShadowStatus($"Export failed: {ex.Message}", ItemStatus.Failed);
            }
        }

        private async Task HandleScan()
        {
            SetShadowStatus("Scanning...", ItemStatus.InProgress);
            this.Options.RepairPreviewList.Clear();
            RaiseUIViewInfoChanged();

            try
            {
                var user = GetUser();
                var shadows = shadowService.GetAllShadows();
                var fullRestore = this.Options.FullRestoreMode;

                if (shadows.Count == 0)
                {
                    SetShadowStatus("No shadow files found. Save settings with shadow enabled to initialize.", ItemStatus.Warning);
                    return;
                }

                var allPlaylists = libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { "Playlist" }
                });

                // Track which live playlists are covered by a shadow
                var shadowedIds = new HashSet<Guid>(shadows.Select(s => s.PlaylistId));
                var unshadowed = allPlaylists
                    .Where(p => !shadowedIds.Contains(p.Id))
                    .ToList();

                int totalIssues = 0;

                foreach (var shadow in shadows)
                {
                    var byGuid = shadow.PlaylistId != Guid.Empty
                        ? allPlaylists.FirstOrDefault(p => p.Id == shadow.PlaylistId)
                        : null;
                    var byName = allPlaylists
                        .Where(p => string.Equals(p.Name, shadow.PlaylistName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var multipleMatches = byName.Count > 1;
                    var shortId = shadow.PlaylistId.ToString("N").Substring(0, 8);

                    if (byGuid == null && byName.Count == 0)
                    {
                        logger.Info("Scan: '{0}' [{1}] — missing entirely. {2} shadow items.", shadow.PlaylistName, shortId, shadow.Items.Count);
                        this.Options.RepairPreviewList.Add(new GenericListItem
                        {
                            PrimaryText = $"{shadow.PlaylistName} [{shortId}]",
                            SecondaryText = $"{shadow.Items.Count} shadow items — playlist missing entirely, Repair will recreate it | Shadow: {shadow.PlaylistName}-{shortId}.json",
                            Status = ItemStatus.Warning,
                            Icon = IconNames.warning,
                            IconMode = ItemListIconMode.SmallRegular
                        });
                        totalIssues++;
                        continue;
                    }

                    var livePlaylist = byGuid ?? byName.First();
                    var guidMismatch = byGuid == null;
                    var liveShortId = livePlaylist.Id.ToString("N").Substring(0, 8);
                    var playlistDir = livePlaylist.Path != null
                        ? Path.GetFileName(Path.GetDirectoryName(livePlaylist.Path))
                        : "unknown";

                    var liveItems = libraryManager.GetItemList(new InternalItemsQuery(user)
                    {
                        ListIds = new[] { livePlaylist.InternalId }
                    });

                    if (shadow.Items.Count == 0)
                    {
                        logger.Info("Scan: '{0}' [{1}] — shadow is empty.", shadow.PlaylistName, shortId);
                        this.Options.RepairPreviewList.Add(new GenericListItem
                        {
                            PrimaryText = $"{shadow.PlaylistName} [{shortId}]",
                            SecondaryText = $"Shadow is empty — no action will be taken | Playlist: {playlistDir}",
                            Status = ItemStatus.Warning,
                            Icon = IconNames.warning,
                            IconMode = ItemListIconMode.SmallRegular
                        });
                        totalIssues++;
                        continue;
                    }

                    var missingFromLive = shadow.Items.Where(si =>
                        !liveItems.Any(live =>
                            si.ProviderIds.Any(kvp =>
                                live.ProviderIds != null &&
                                live.ProviderIds.TryGetValue(kvp.Key, out var val) && val == kvp.Value)))
                        .ToList();
                    var extraInLive = liveItems.Length - (shadow.Items.Count - missingFromLive.Count);
                    var nameNote = multipleMatches ? $", {byName.Count} playlists share this name" : "";

                    if (!guidMismatch && missingFromLive.Count == 0)
                    {
                        logger.Info("Scan: '{0}' [{1}] — OK. {2} items match.", shadow.PlaylistName, shortId, shadow.Items.Count);
                        this.Options.RepairPreviewList.Add(new GenericListItem
                        {
                            PrimaryText = $"{shadow.PlaylistName} [{shortId}]",
                            SecondaryText = $"OK — {shadow.Items.Count} items present{nameNote} | Shadow: {shadow.PlaylistName}-{shortId}.json | Playlist: {playlistDir}",
                            Status = ItemStatus.Succeeded,
                            Icon = IconNames.check_circle,
                            IconMode = ItemListIconMode.SmallRegular
                        });
                        continue;
                    }

                    var details = new List<string>();
                    if (guidMismatch)
                        details.Add($"GUID mismatch (shadow: {shortId}, live: {liveShortId})");
                    if (missingFromLive.Count > 0)
                        details.Add($"{missingFromLive.Count} items missing");
                    if (fullRestore && extraInLive > 0)
                        details.Add($"{extraInLive} live items not in shadow (will be removed)");

                    var action = fullRestore
                        ? "Full Restore: replace live contents with shadow"
                        : $"Safe: add {missingFromLive.Count} missing items";

                    logger.Info("Scan: '{0}' [{1}] — {2}. Action: {3}", shadow.PlaylistName, shortId, string.Join(", ", details), action);
                    this.Options.RepairPreviewList.Add(new GenericListItem
                    {
                        PrimaryText = $"{shadow.PlaylistName} [{shortId}]",
                        SecondaryText = $"{string.Join(" | ", details)} → {action} | Shadow: {shadow.PlaylistName}-{shortId}.json | Playlist: {playlistDir}",
                        Status = ItemStatus.Warning,
                        Icon = IconNames.warning,
                        IconMode = ItemListIconMode.SmallRegular
                    });
                    totalIssues++;
                }

                // Show live playlists with no shadow
                foreach (var p in unshadowed)
                {
                    var liveShortId = p.Id.ToString("N").Substring(0, 8);
                    var playlistDir = p.Path != null
                        ? Path.GetFileName(Path.GetDirectoryName(p.Path))
                        : "unknown";
                    logger.Info("Scan: '{0}' [{1}] — no shadow yet.", p.Name, liveShortId);
                    this.Options.RepairPreviewList.Add(new GenericListItem
                    {
                        PrimaryText = $"{p.Name} [{liveShortId}]",
                        SecondaryText = $"No shadow — run Repair All to initialize, or will be tracked automatically after next playlist change | Playlist: {playlistDir}",
                        Status = ItemStatus.Unavailable,
                        Icon = IconNames.info,
                        IconMode = ItemListIconMode.SmallRegular
                    });
                }

                var total = shadows.Count + unshadowed.Count;
                var msg = totalIssues == 0
                    ? $"Scan complete: {total} playlists, no issues found."
                    : $"Scan complete: {total} playlists, {totalIssues} need attention. Click Repair All to fix.";
                SetShadowStatus(msg, totalIssues == 0 ? ItemStatus.Succeeded : ItemStatus.Warning);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Scan failed", ex);
                SetShadowStatus($"Scan failed: {ex.Message}", ItemStatus.Failed);
            }
        }

        private async Task HandleRepairAll()
        {
            SetShadowStatus("Repairing...", ItemStatus.InProgress);
            this.Options.MissingItemsList.Clear();
            RaiseUIViewInfoChanged();

            try
            {
                var user = GetUser();
                var shadows = shadowService.GetAllShadows();
                var fullRestore = this.Options.FullRestoreMode;

                if (shadows.Count == 0)
                {
                    SetShadowStatus("No shadow files found.", ItemStatus.Warning);
                    return;
                }

                int totalFixed = 0, totalMissing = 0;

                var allPlaylists = libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { "Playlist" }
                });

                foreach (var shadow in shadows)
                {
                    if (shadow.Items.Count == 0) continue;

                    var byGuid = shadow.PlaylistId != Guid.Empty
                        ? allPlaylists.FirstOrDefault(p => p.Id == shadow.PlaylistId)
                        : null;
                    var byName = allPlaylists
                        .Where(p => string.Equals(p.Name, shadow.PlaylistName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var livePlaylist = byGuid ?? byName.FirstOrDefault();

                    if (livePlaylist == null)
                    {
                        logger.Info("Repair: '{0}' missing — recreating.", shadow.PlaylistName);
                        var created = await playlistService.CreatePlaylistFromShadow(user, shadow);
                        if (created == null) { logger.Warn("Repair: Could not recreate '{0}'.", shadow.PlaylistName); continue; }
                        shadowService.DeleteShadow(shadow.PlaylistName, shadow.PlaylistId);
                        livePlaylist = created;
                        totalFixed++;
                    }

                    var liveItems = libraryManager.GetItemList(new InternalItemsQuery(user)
                    {
                        ListIds = new[] { livePlaylist.InternalId }
                    });

                    // Full restore: clear live playlist first
                    if (fullRestore && liveItems.Length > 0)
                    {
                        await playlistService.ClearPlaylist(livePlaylist.InternalId, liveItems);
                        liveItems = new BaseItem[0];
                        logger.Info("Repair (Full Restore): cleared '{0}'.", shadow.PlaylistName);
                    }

                    foreach (var shadowItem in shadow.Items)
                    {
                        var present = liveItems.Any(live =>
                            shadowItem.ProviderIds.Any(kvp =>
                                live.ProviderIds != null &&
                                live.ProviderIds.TryGetValue(kvp.Key, out var val) && val == kvp.Value));

                        if (present) continue;

                        BaseItem resolved = null;
                        string resolvedVia = null;

                        foreach (var kvp in shadowItem.ProviderIds.OrderBy(k =>
                            k.Key.Equals("Imdb", StringComparison.OrdinalIgnoreCase) ? 0 :
                            k.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase) ? 1 : 2))
                        {
                            resolved = libraryManager.GetItemList(new InternalItemsQuery(user)
                            {
                                AnyProviderIdEquals = new[] { new KeyValuePair<string, string>(kvp.Key, kvp.Value) }
                            }).FirstOrDefault();
                            if (resolved != null) { resolvedVia = $"{kvp.Key}={kvp.Value}"; break; }
                        }

                        if (resolved == null && (shadowItem.SeriesTmdbId.HasValue || shadowItem.SeriesTvdbId.HasValue)
                            && shadowItem.Season.HasValue && shadowItem.Episode.HasValue)
                        {
                            var seriesKvp = shadowItem.SeriesTmdbId.HasValue
                                ? new KeyValuePair<string, string>("Tmdb", shadowItem.SeriesTmdbId.Value.ToString())
                                : new KeyValuePair<string, string>("Tvdb", shadowItem.SeriesTvdbId.Value.ToString());
                            var series = libraryManager.GetItemList(new InternalItemsQuery(user)
                            {
                                IncludeItemTypes = new[] { "Series" },
                                AnyProviderIdEquals = new[] { seriesKvp }
                            }).FirstOrDefault();
                            if (series != null)
                            {
                                resolved = libraryManager.GetItemList(new InternalItemsQuery(user)
                                {
                                    AncestorIds = new[] { series.InternalId },
                                    IncludeItemTypes = new[] { "Episode" },
                                    ParentIndexNumber = shadowItem.Season,
                                    IndexNumber = shadowItem.Episode
                                }).FirstOrDefault();
                                if (resolved != null)
                                    resolvedVia = $"series {seriesKvp.Key}={seriesKvp.Value} S{shadowItem.Season}E{shadowItem.Episode}";
                            }
                        }

                        if (resolved != null)
                        {
                            playlistService.AddToPlaylist(livePlaylist.InternalId, resolved, user);
                            logger.Info("  Fixed: '{0}' via {1} → '{2}'", shadowItem.Name, resolvedVia, shadow.PlaylistName);
                            totalFixed++;
                        }
                        else
                        {
                            var providerStr = shadowItem.ProviderIds.Count > 0
                                ? string.Join(", ", shadowItem.ProviderIds.Select(k => $"{k.Key}={k.Value}"))
                                : "no provider IDs";
                            logger.Warn("  Missing: '{0}' [{1}]", shadowItem.Name, providerStr);
                            this.Options.MissingItemsList.Add(new GenericListItem
                            {
                                PrimaryText = shadow.PlaylistName,
                                SecondaryText = $"{shadowItem.Name} [{providerStr}]",
                                Status = ItemStatus.Warning,
                                Icon = IconNames.warning,
                                IconMode = ItemListIconMode.SmallRegular
                            });
                            totalMissing++;
                        }
                    }
                }

                logger.Info("Repair complete. Fixed: {0}, Still missing: {1}.", totalFixed, totalMissing);

                // Initialize shadows for any playlists that don't have one yet
                var shadowedIds = new HashSet<Guid>(shadows.Select(s => s.PlaylistId));
                var unshadowed = allPlaylists.Where(p => !shadowedIds.Contains(p.Id)).ToArray();
                foreach (var p in unshadowed)
                {
                    shadowService.UpdateShadow(user, p, "RepairAll-InitMissing", cleanDuplicates: false);
                    logger.Info("Repair: initialized shadow for unshadowed playlist '{0}'.", p.Name);
                }

                var msg = $"Repair complete. Fixed: {totalFixed}" +
                    (unshadowed.Length > 0 ? $", {unshadowed.Length} new shadows created" : "") +
                    (totalMissing > 0 ? $", {totalMissing} still missing (see Unresolvable Items below)." : ".");
                SetShadowStatus(msg, totalMissing == 0 ? ItemStatus.Succeeded : ItemStatus.Warning);
                RaiseUIViewInfoChanged();
            }
            catch (Exception ex)
            {
                logger.ErrorException("Repair failed", ex);
                SetShadowStatus($"Repair failed: {ex.Message}", ItemStatus.Failed);
            }
        }

        private static bool IsDirectoryWritable(string path)
        {
            try
            {
                var testFile = Path.Combine(path, Path.GetRandomFileName());
                using (File.Create(testFile, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
