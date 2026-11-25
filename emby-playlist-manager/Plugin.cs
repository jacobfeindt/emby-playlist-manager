using System;
using System.Collections.Generic;
using System.IO;
using EmbyPlaylistManager.Storage;
using EmbyPlaylistManager.UI;
using MediaBrowser.Common.Configuration;
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
        private readonly OptionsStore<ExportImportOptions> _exportStore;
        private readonly OptionsStore<ShadowOptions> _shadowStore;
        private readonly OptionsStore<SortOptions> _sortStore;
        private readonly OptionsStore<ConvertOptions> _convertStore;
        private readonly OptionsStore<ThemesOptions> _themesStore;
        private readonly PlaylistService _playlistService;
        private readonly ShadowPlaylistService _shadowService;
        private List<IPluginUIPageController> _pages;

        private readonly PlaylistSortService _sortService;
        private readonly PlaylistConvertService _convertService;
        private readonly HomeScreenService _homeScreenService;

        public OptionsStore<ExportImportOptions> ExportStore => _exportStore;
        public OptionsStore<ShadowOptions> ShadowStore => _shadowStore;
        public OptionsStore<SortOptions> SortStore => _sortStore;
        public OptionsStore<ConvertOptions> ConvertStore => _convertStore;
        public OptionsStore<ThemesOptions> ThemesStore => _themesStore;
        public PlaylistService PlaylistService => _playlistService;
        public ShadowPlaylistService ShadowService => _shadowService;
        public PlaylistSortService SortService => _sortService;
        public PlaylistConvertService ConvertService => _convertService;
        public HomeScreenService HomeScreenService => _homeScreenService;

        // Emby's IApplicationHost.Resolve<Plugin>() builds a NEW Plugin on every call, so anything that must be shared between
        // the page, the scheduled task, the HTTP API and the event handlers has to live in a static, not in an instance field.
        private static readonly OperationGate SharedGate = new OperationGate();

        /// <summary>Only one long-running playlist operation (import, repair, scan, sort, scheduled repair) runs at a time, across every Plugin instance.</summary>
        public OperationGate Gate => SharedGate;

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
            _exportStore = new OptionsStore<ExportImportOptions>(appHost, _logger, "PlaylistManager.ExportImport");
            _shadowStore = new OptionsStore<ShadowOptions>(appHost, _logger, "PlaylistManager.Shadow");
            _sortStore = new OptionsStore<SortOptions>(appHost, _logger, "PlaylistManager.Sort");
            _convertStore = new OptionsStore<ConvertOptions>(appHost, _logger, "PlaylistManager.Convert");
            _themesStore = new OptionsStore<ThemesOptions>(appHost, _logger, "PlaylistManager.Themes");
            _playlistService = new PlaylistService(libraryManager, playlistManager, _logger);
            var appPaths = appHost.Resolve<IApplicationPaths>();
            _shadowService = new ShadowPlaylistService(libraryManager, appPaths, _logger);
            _sortService = new PlaylistSortService(libraryManager, playlistManager, _shadowService, appPaths, _logger);
            _convertService = new PlaylistConvertService(appHost, libraryManager, _logger);
            _homeScreenService = new HomeScreenService(appHost, libraryManager, _logger);
            _logger.Info("Playlist Manager plugin loaded ({0}).", DescribeBuild());
        }

        /// <summary>
        /// Version and, when known, build time of this DLL for the log. It must never throw: Emby can load a plugin from memory,
        /// which leaves Assembly.Location empty, and an exception here would stop the whole plugin from loading.
        /// </summary>
        private static string DescribeBuild()
        {
            try
            {
                var assembly = typeof(Plugin).Assembly;
                var text = "version " + assembly.GetName().Version;
                var location = assembly.Location;
                if (!string.IsNullOrEmpty(location) && File.Exists(location))
                    text += ", DLL built " + File.GetLastWriteTimeUtc(location).ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
                return text;
            }
            catch (Exception)
            {
                return "build unknown";
            }
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
                    var userManager = _appHost.Resolve<IUserManager>();
                    var info = GetPluginInfo();
                    var tabs = new List<IPluginUIPageController>
                    {
                        new PageController(info, "PlaylistManagerExportImport", "Export & Import", "import_export",
                            () => new ExportImportPageView(info, _appHost, userManager, _libraryManager, _logger, _exportStore), enableInMainMenu: false),
                        new PageController(info, "PlaylistManagerShadow", "Shadow Playlists", "sync",
                            () => new ShadowPageView(info, _appHost, userManager, _libraryManager, _logger, _shadowStore), enableInMainMenu: false),
                        new PageController(info, "PlaylistManagerSort", "Sort a Playlist", "sort",
                            () => new SortPageView(info, _appHost, userManager, _libraryManager, _logger, _sortStore), enableInMainMenu: false),
                        new PageController(info, "PlaylistManagerConvert", "Convert to Collection", "playlist_add",
                            () => new ConvertPageView(info, _appHost, userManager, _libraryManager, _logger, _convertStore), enableInMainMenu: false),
                        new PageController(info, "PlaylistManagerThemes", "Themes", "tag",
                            () => new ThemesPageView(info, _appHost, userManager, _libraryManager, _logger, _themesStore), enableInMainMenu: false),
                    };
                    _pages = new List<IPluginUIPageController>
                    {
                        new MainPageController(info, tabs)
                    };
                }
                return _pages.AsReadOnly();
            }
        }

        public Type ConfigurationType => typeof(ShadowOptions);
        public BasePluginConfiguration Configuration { get; } = new BasePluginConfiguration();
        public void UpdateConfiguration(BasePluginConfiguration configuration) { }
        public void SetStartupInfo(Action<string> directoryCreateFn) { }
    }
}
