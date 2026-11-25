using System;
using System.Threading.Tasks;
using EmbyPlaylistManager.UIBaseClasses;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;

namespace EmbyPlaylistManager.UI
{
    /// <summary>One navigable plugin page. Each page supplies its own name/icon and a factory for its view.</summary>
    internal class PageController : ControllerBase
    {
        private readonly Func<IPluginUIView> createView;

        public PageController(
            PluginInfo pluginInfo,
            string pageName,
            string displayName,
            string menuIcon,
            Func<IPluginUIView> createView,
            bool isMainConfigPage = false,
            bool enableInMainMenu = true)
            : base(pluginInfo.Id)
        {
            this.createView = createView;

            this.PageInfo = new PluginPageInfo
            {
                Name = pageName,
                EnableInMainMenu = enableInMainMenu,
                DisplayName = displayName,
                MenuIcon = menuIcon,
                IsMainConfigPage = isMainConfigPage,
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
            => Task.FromResult(createView());
    }
}
