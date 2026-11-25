using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace EmbyPlaylistManager
{
    public class PluginEntryPoint : IServerEntryPoint
    {
        private readonly IApplicationHost _appHost;
        private readonly IPlaylistManager _playlistManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly ILogger _logger;

        private Plugin _plugin;
        private bool _shadowFolderReady;
        private ISessionManager _sessionManager;

        // Coalesces rapid reconnect events so one sign-in doesn't shuffle several times.
        private static readonly ConcurrentDictionary<long, DateTime> LastAutoShuffle = new ConcurrentDictionary<long, DateTime>();
        private static readonly TimeSpan AutoShuffleMinInterval = TimeSpan.FromMinutes(1);

        public PluginEntryPoint(
            IApplicationHost appHost,
            IPlaylistManager playlistManager,
            ILibraryManager libraryManager,
            IUserManager userManager,
            ILogManager logManager)
        {
            _appHost = appHost;
            _playlistManager = playlistManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _logger = logManager.GetLogger(Plugin.PluginName);
        }

        public void Run()
        {
            _plugin = _appHost.Resolve<Plugin>();
            if (_plugin == null)
            {
                _logger.Warn("PluginEntryPoint: Could not resolve Plugin instance. Shadow system will not start.");
                return;
            }

            _sessionManager = _appHost.Resolve<ISessionManager>();
            if (_sessionManager != null)
            {
                _sessionManager.SessionStarted += OnSessionStarted;
                _logger.Info("Themes on home: listening for session start events.");
            }

            var options = _plugin.ShadowStore.GetOptions();
            _logger.Info("Shadow system: EnableShadow={0}", options.EnableShadow);

            if (!options.EnableShadow)
            {
                _logger.Info("Shadow system is disabled. Skipping event subscriptions.");
                return;
            }

            _shadowFolderReady = _plugin.ShadowService.EnsureShadowFolder();
            if (!_shadowFolderReady)
                return;

            _playlistManager.PlaylistItemsAdded   += OnPlaylistItemsAdded;
            _playlistManager.PlaylistItemsRemoved += OnPlaylistItemsRemoved;
            _playlistManager.PlaylistItemsMoved   += OnPlaylistItemsMoved;
            _libraryManager.ItemAdded             += OnItemAdded;

            _logger.Info("Shadow system started. Subscribed to playlist and library events. Shadow folder: {0}",
                _plugin.ShadowService.ShadowFolder);

            if (_plugin.ShadowService.ShadowFolderIsEmpty())
            {
                Task.Run(() =>
                {
                    try
                    {
                        var user = GetAdminUser();
                        if (user != null) _plugin.ShadowService.InitializeAllShadows(user);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("Shadow initialization failed", ex);
                    }
                });
            }
            else
            {
                Task.Run(() =>
                {
                    try
                    {
                        var user = GetAdminUser();
                        if (user != null) _plugin.ShadowService.InitializeMissingShadows(user);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("Shadow missing initialization failed", ex);
                    }
                });
            }
        }

        public void Dispose()
        {
            try
            {
                _playlistManager.PlaylistItemsAdded   -= OnPlaylistItemsAdded;
                _playlistManager.PlaylistItemsRemoved -= OnPlaylistItemsRemoved;
                _playlistManager.PlaylistItemsMoved   -= OnPlaylistItemsMoved;
                _libraryManager.ItemAdded             -= OnItemAdded;
                if (_sessionManager != null)
                    _sessionManager.SessionStarted -= OnSessionStarted;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error unsubscribing shadow events", ex);
            }
        }

        private void OnSessionStarted(object sender, SessionEventArgs e)
        {
            try
            {
                var session = e.SessionInfo;
                if (session == null || !session.HasUser || string.IsNullOrWhiteSpace(session.UserId))
                    return;

                if (_plugin == null) _plugin = _appHost.Resolve<Plugin>();
                if (_plugin == null) return;

                var themesOptions = _plugin.ThemesStore.GetOptions();
                if (!themesOptions.AutoShuffleOnConnect)
                    return;

                if (!Guid.TryParse(session.UserId, out var userGuid))
                    return;

                var user = _userManager.GetUserById(userGuid);
                if (user == null) return;

                // Coalesce rapid reconnects.
                var now = DateTime.UtcNow;
                if (LastAutoShuffle.TryGetValue(user.InternalId, out var last) && (now - last) < AutoShuffleMinInterval)
                    return;
                LastAutoShuffle[user.InternalId] = now;

                int count = Math.Max(1, themesOptions.HomeThemeCount);
                _logger.Info("Themes on home: session started for user '{0}'; auto-shuffling {1} themes.", user.Name, count);
                Task.Run(async () =>
                {
                    try
                    {
                        await _plugin.HomeScreenService.ShuffleThemesOnHome(user, count, themesOptions.ThemeNames, themesOptions.IncludeBoxSets, themesOptions.RandomizeItemOrder);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Themes on home: auto-shuffle failed for '{0}': {1}", user.Name, ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Warn("Themes on home: could not handle session start: {0}", ex.Message);
            }
        }

        private void OnPlaylistItemsAdded(object sender, PlaylistItemsAddedEventArgs e)
        {
            _logger.Debug("PlaylistItemsAdded: Playlist='{0}' InternalId={1}", e.Playlist?.Name, e.Playlist?.InternalId);
            ScheduleShadowUpdate(e.Playlist, "PlaylistItemsAdded");
        }

        private void OnPlaylistItemsRemoved(object sender, PlaylistItemsRemovedEventArgs e)
        {
            _logger.Debug("PlaylistItemsRemoved: Playlist='{0}' InternalId={1}", e.Playlist?.Name, e.Playlist?.InternalId);
            ScheduleShadowUpdate(e.Playlist, "PlaylistItemsRemoved");
        }

        private void OnPlaylistItemsMoved(object sender, PlaylistItemsMovedEventArgs e)
        {
            _logger.Debug("PlaylistItemsMoved: Playlist='{0}' InternalId={1}", e.Playlist?.Name, e.Playlist?.InternalId);
            ScheduleShadowUpdate(e.Playlist, "PlaylistItemsMoved");
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (_plugin == null) return;
            Task.Run(() =>
            {
                try
                {
                    if (e?.Item == null || e.Item.ProviderIds == null || e.Item.ProviderIds.Count == 0)
                        return;

                    var shadows = _plugin.ShadowService.GetAllShadows();
                    foreach (var shadow in shadows)
                    {
                        foreach (var shadowItem in shadow.Items)
                        {
                            if (shadowItem.ProviderIds.Any(kvp =>
                                e.Item.ProviderIds.TryGetValue(kvp.Key, out var val) && val == kvp.Value))
                            {
                                _logger.Info("ItemAdded may resolve broken shadow item '{0}' in playlist '{1}'. Consider running Repair.",
                                    e.Item.Name, shadow.PlaylistName);
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error in OnItemAdded shadow check", ex);
                }
            });
        }

        private void ScheduleShadowUpdate(MediaBrowser.Controller.Entities.BaseItem playlist, string triggerEvent)
        {
            if (!_shadowFolderReady || playlist == null || _plugin == null) return;

            Task.Run(() =>
            {
                try
                {
                    var user = GetAdminUser();
                    if (user != null)
                        _plugin.ShadowService.RequestUpdate(user, playlist, triggerEvent);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Shadow update failed for '{0}'", ex, playlist?.Name);
                }
            });
        }

        private MediaBrowser.Controller.Entities.User GetAdminUser()
        {
            try { return _userManager.GetUserList(new MediaBrowser.Model.Querying.UserQuery()).FirstOrDefault(u => u.Policy?.IsAdministrator == true); }
            catch (Exception ex)
            {
                _logger.ErrorException("Could not resolve admin user for shadow operation", ex);
                return null;
            }
        }
    }
}
