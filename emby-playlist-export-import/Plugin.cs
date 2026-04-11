using System;
using System.IO;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;

namespace EmbyPlaylistMigration
{
    public class Plugin : BasePluginSimpleUI<PluginOptions>, IHasThumbImage
    {
        public const string PluginName = "Playlist Export/Import";

        private readonly Guid _id = new Guid("7b3d3243-e854-4aaf-9636-1505fdd78d6c");
        private readonly ILogger _logger;

        public Plugin(IApplicationHost applicationHost, ILogManager logManager)
            : base(applicationHost)
        {
            _logger = logManager.GetLogger(PluginName);
            _logger.Info("Playlist Export/Import plugin loaded.");
        }

        public override string Name => PluginName;
        public override string Description => "Export and import playlists using provider IDs (IMDb/TMDb) for portability across Emby servers.";
        public override Guid Id => _id;

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".ThumbImage.png");
        }

        protected override void OnOptionsSaved(PluginOptions options)
        {
            _logger.Info("Playlist Export/Import plugin options saved.");
        }
    }
}
