using Emby.Web.GenericEdit;
using EmbyPlaylistManager.UIBaseClasses.Store;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;

namespace EmbyPlaylistManager.Storage
{
    /// <summary>A typed JSON file store for one page of plugin options.</summary>
    public class OptionsStore<TOptions> : SimpleFileStore<TOptions>
        where TOptions : EditableOptionsBase, new()
    {
        public OptionsStore(IApplicationHost applicationHost, ILogger logger, string storeName)
            : base(applicationHost, logger, storeName)
        {
        }
    }
}
