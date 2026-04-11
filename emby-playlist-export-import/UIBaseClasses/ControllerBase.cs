using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace EmbyPlaylistMigration.UIBaseClasses
{
    public abstract class ControllerBase : IPluginUIPageController
    {
        protected ControllerBase(string pluginId)
        {
            this.PluginId = pluginId;
        }

        public abstract PluginPageInfo PageInfo { get; }
        public string PluginId { get; }

        public virtual Task Initialize(CancellationToken token) => Task.CompletedTask;

        public abstract Task<IPluginUIView> CreateDefaultPageView();
    }
}
