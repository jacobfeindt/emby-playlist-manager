using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace EmbyPlaylistManager
{
    public class PlaylistRepairTask : IScheduledTask
    {
        private readonly IApplicationHost _appHost;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly ILogger _logger;

        public string Name        => "Playlist Manager: Repair Broken Links";
        public string Description => "Scans all playlists for broken item links and repairs them using the shadow playlist store.";
        public string Category    => "Playlist Manager";
        public string Key         => "PlaylistManagerRepair";

        public PlaylistRepairTask(
            IApplicationHost appHost,
            ILibraryManager libraryManager,
            IUserManager userManager,
            ILogManager logManager)
        {
            _appHost = appHost;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _logger = logManager.GetLogger(Plugin.PluginName);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo
            {
                Type           = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek      = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        };

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = _appHost.Resolve<Plugin>();
            if (plugin == null)
            {
                _logger.Warn("PlaylistRepairTask: Could not resolve Plugin instance. Aborting.");
                return;
            }

            var options = plugin.OptionsStore.GetOptions();
            if (!options.EnableShadow)
            {
                _logger.Info("PlaylistRepairTask: Shadow system is disabled. Skipping.");
                return;
            }

            _logger.Info("PlaylistRepairTask: Starting scheduled repair.");
            progress.Report(0);

            try
            {
                var user = GetAdminUser();
                if (user == null)
                {
                    _logger.Warn("PlaylistRepairTask: No admin user found. Aborting.");
                    return;
                }

                var shadows = plugin.ShadowService.GetAllShadows();
                if (shadows.Count == 0)
                {
                    _logger.Info("PlaylistRepairTask: No shadow files found. Nothing to repair.");
                    progress.Report(100);
                    return;
                }

                int totalFixed = 0, totalMissing = 0, totalOk = 0;
                double step = 100.0 / shadows.Count;

                for (int i = 0; i < shadows.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var shadow = shadows[i];

                    try
                    {
                        var livePlaylist = _libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            IncludeItemTypes = new[] { "Playlist" }
                        }).FirstOrDefault(p => string.Equals(p.Name, shadow.PlaylistName, StringComparison.OrdinalIgnoreCase));

                        if (livePlaylist == null)
                        {
                            _logger.Info("PlaylistRepairTask: Playlist '{0}' not found on server — skipped.", shadow.PlaylistName);
                            progress.Report((i + 1) * step);
                            continue;
                        }

                        var liveItems = _libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            ListIds = new[] { livePlaylist.InternalId }
                        });

                        _logger.Info("PlaylistRepairTask: Playlist '{0}': {1} live items, {2} shadow items.",
                            shadow.PlaylistName, liveItems.Length, shadow.Items.Count);

                        foreach (var shadowItem in shadow.Items)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var present = liveItems.Any(live =>
                                shadowItem.ProviderIds.Any(kvp =>
                                    live.ProviderIds != null &&
                                    live.ProviderIds.TryGetValue(kvp.Key, out var val) && val == kvp.Value));

                            if (present) { totalOk++; continue; }

                            BaseItem resolved = null;
                            string resolvedVia = null;

                            foreach (var kvp in shadowItem.ProviderIds.OrderBy(k =>
                                      k.Key.Equals("Imdb", StringComparison.OrdinalIgnoreCase) ? 0 :
                                      k.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase) ? 1 : 2))
                            {
                                resolved = _libraryManager.GetItemList(new InternalItemsQuery(user)
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

                                var series = _libraryManager.GetItemList(new InternalItemsQuery(user)
                                {
                                    IncludeItemTypes = new[] { "Series" },
                                    AnyProviderIdEquals = new[] { seriesKvp }
                                }).FirstOrDefault();

                                if (series != null)
                                {
                                    resolved = _libraryManager.GetItemList(new InternalItemsQuery(user)
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
                                plugin.PlaylistService.AddToPlaylist(livePlaylist.InternalId, resolved, user);
                                _logger.Info("  Fixed: '{0}' — re-resolved via {1}, added to '{2}'",
                                    shadowItem.Name, resolvedVia, shadow.PlaylistName);
                                totalFixed++;
                            }
                            else
                            {
                                var providerStr = shadowItem.ProviderIds.Count > 0
                                    ? string.Join(", ", shadowItem.ProviderIds.Select(k => $"{k.Key}={k.Value}"))
                                    : "no provider IDs";
                                _logger.Warn("  Missing: '{0}' — not found in library [{1}]", shadowItem.Name, providerStr);
                                totalMissing++;
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("PlaylistRepairTask: Error processing playlist '{0}'", ex, shadow.PlaylistName);
                    }

                    progress.Report((i + 1) * step);
                }

                _logger.Info("PlaylistRepairTask: Complete. OK: {0}, Fixed: {1}, Still missing: {2}.",
                    totalOk, totalFixed, totalMissing);
                progress.Report(100);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("PlaylistRepairTask: Cancelled.");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("PlaylistRepairTask: Unexpected error", ex);
            }
        }

        private User GetAdminUser()
        {
            try { return _userManager.GetUserList(new MediaBrowser.Model.Querying.UserQuery()).FirstOrDefault(u => u.Policy?.IsAdministrator == true); }
            catch (Exception ex)
            {
                _logger.ErrorException("PlaylistRepairTask: Could not resolve admin user", ex);
                return null;
            }
        }
    }
}
