using System;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Playlists;
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

            var options = _plugin.OptionsStore.GetOptions();
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
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error unsubscribing shadow events", ex);
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
                        _plugin.ShadowService.UpdateShadow(user, playlist, triggerEvent);
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
