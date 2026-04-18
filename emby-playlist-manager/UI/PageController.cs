using System.Threading.Tasks;
using EmbyPlaylistManager.Storage;
using EmbyPlaylistManager.UIBaseClasses;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;

namespace EmbyPlaylistManager.UI
{
    internal class PageController : ControllerBase
    {
        private readonly PluginInfo pluginInfo;
        private readonly OptionsStore optionsStore;
        private readonly IServerApplicationHost appHost;
        private readonly IUserManager userManager;
        private readonly ILibraryManager libraryManager;
        private readonly PlaylistService playlistService;
        private readonly ShadowPlaylistService shadowService;
        private readonly ILogger logger;

        public PageController(
            PluginInfo pluginInfo,
            IServerApplicationHost appHost,
            OptionsStore optionsStore,
            IUserManager userManager,
            ILibraryManager libraryManager,
            PlaylistService playlistService,
            ShadowPlaylistService shadowService,
            ILogger logger)
            : base(pluginInfo.Id)
        {
            this.pluginInfo = pluginInfo;
            this.appHost = appHost;
            this.optionsStore = optionsStore;
            this.userManager = userManager;
            this.libraryManager = libraryManager;
            this.playlistService = playlistService;
            this.shadowService = shadowService;
            this.logger = logger;

            this.PageInfo = new PluginPageInfo
            {
                Name = "PlaylistManagerMainPage",
                EnableInMainMenu = true,
                DisplayName = "Playlist Manager",
                MenuIcon = "import_export",
                IsMainConfigPage = true,
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new PluginPageView(
                this.pluginInfo,
                this.optionsStore,
                this.appHost,
                this.userManager,
                this.libraryManager,
                this.playlistService,
                this.shadowService,
                this.logger);
            return Task.FromResult(view);
        }
    }
}
