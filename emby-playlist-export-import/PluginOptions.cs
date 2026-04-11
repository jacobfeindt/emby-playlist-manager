using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;

namespace EmbyPlaylistMigration
{
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Playlist Export/Import";

        public override string EditorDescription => "Export playlists to portable JSON and import them on any Emby server using IMDb/TMDb IDs.";

        [DisplayName("Export Output Folder")]
        [Description("Folder where exported playlist JSON files will be saved.")]
        [EditFolderPicker]
        public string ExportFolder { get; set; }

        [DisplayName("Import File Path")]
        [Description("Full path to a playlist JSON file to import.")]
        public string ImportFilePath { get; set; }

        [DisplayName("Import Playlist Name")]
        [Description("Name for the newly created playlist on import.")]
        public string ImportPlaylistName { get; set; }
    }
}
