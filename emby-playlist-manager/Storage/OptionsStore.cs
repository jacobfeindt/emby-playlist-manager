using EmbyPlaylistManager.UIBaseClasses.Store;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;

namespace EmbyPlaylistManager.Storage
{
    public class OptionsStore : SimpleFileStore<PluginOptions>
    {
        public OptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
        }
    }
}
