using System;
using System.Collections.Generic;
using System.IO;
using EmbyPlaylistManager.Storage;
using EmbyPlaylistManager.UI;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;

namespace EmbyPlaylistManager
{
    public class Plugin : BasePlugin, IHasThumbImage, IHasUIPages, IHasPluginConfiguration
    {
        public const string PluginName = "Playlist Manager";
        private readonly Guid _id = new Guid("7b3d3243-e854-4aaf-9636-1505fdd78d6c");

        private readonly IServerApplicationHost _appHost;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly OptionsStore _optionsStore;
        private readonly PlaylistService _playlistService;
        private List<IPluginUIPageController> _pages;

        public OptionsStore OptionsStore => _optionsStore;
        public PlaylistService PlaylistService => _playlistService;

        public Plugin(
            IServerApplicationHost appHost,
            ILibraryManager libraryManager,
            IPlaylistManager playlistManager,
            IUserManager userManager,
            ILogManager logManager)
        {
            _appHost = appHost;
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger(PluginName);
            _optionsStore = new OptionsStore(appHost, _logger, PluginName);
            _playlistService = new PlaylistService(libraryManager, playlistManager, _logger);
            _logger.Info("Playlist Manager plugin loaded.");
        }

        public override string Name => PluginName;
        public override string Description => "Manage Emby playlists — export, import, and repair using provider IDs (IMDb/TMDb) for portability across servers.";
        public override Guid Id => _id;

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".ThumbImage.png");
        }

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (_pages == null)
                {
                    _pages = new List<IPluginUIPageController>
                    {
                        new PageController(
                            GetPluginInfo(),
                            _appHost,
                            _optionsStore,
                            _appHost.Resolve<IUserManager>(),
                            _libraryManager,
                            _playlistService,
                            _logger)
                    };
                }
                return _pages.AsReadOnly();
            }
        }

        public Type ConfigurationType => typeof(PluginOptions);
        public BasePluginConfiguration Configuration { get; } = new BasePluginConfiguration();
        public void UpdateConfiguration(BasePluginConfiguration configuration) { }
        public void SetStartupInfo(Action<string> directoryCreateFn) { }
    }
}
