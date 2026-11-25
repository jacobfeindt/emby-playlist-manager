using System.Collections.Generic;
using System.Threading.Tasks;
using EmbyPlaylistManager.UIBaseClasses;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace EmbyPlaylistManager.UI
{
    /// <summary>One top-level "Playlist Manager" page whose tabs are the individual feature pages.</summary>
    internal class MainPageController : ControllerBase, IHasTabbedUIPages
    {
        private readonly List<IPluginUIPageController> _tabs;

        public MainPageController(PluginInfo pluginInfo, List<IPluginUIPageController> tabs)
            : base(pluginInfo.Id)
        {
            _tabs = tabs ?? new List<IPluginUIPageController>();

            this.PageInfo = new PluginPageInfo
            {
                Name = "PlaylistManager",
                EnableInMainMenu = true,
                DisplayName = "Playlist Manager",
                MenuIcon = "import_export",
                IsMainConfigPage = true,
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
            => _tabs.Count > 0 ? _tabs[0].CreateDefaultPageView() : Task.FromResult<IPluginUIView>(null);

        public IReadOnlyList<IPluginUIPageController> TabPageControllers => _tabs.AsReadOnly();
    }
}
