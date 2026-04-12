using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;

namespace EmbyPlaylistManager
{
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Playlist Manager";
        public override string EditorDescription => "Export, import, and repair Emby playlists using IMDb/TMDb IDs for portability across servers.";

        // --- Export ---
        public CaptionItem ExportCaption { get; set; } = new CaptionItem("Export");

        [DisplayName("Export Output Folder")]
        [Description("Folder where the exported playlists JSON file will be saved.")]
        [EditFolderPicker]
        public string ExportFolder { get; set; }

        public ButtonItem ExportButton { get; set; } = new ButtonItem("Export All Playlists") { Icon = IconNames.upload, Data1 = "Export" };

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        // --- Import ---
        public CaptionItem ImportCaption { get; set; } = new CaptionItem("Import");

        [DisplayName("Import File")]
        [Description("Select a previously exported playlists JSON file. Preview loads automatically.")]
        [EditFilePicker]
        [AutoPostBack("Preview", nameof(ImportFilePath))]
        public string ImportFilePath { get; set; }

        public CaptionItem PreviewCaption { get; set; } = new CaptionItem("Playlists in File");

        [DisplayName("")]
        [Description("Playlists found in the selected file. Existing playlists with the same name will be skipped on import.")]
        public GenericItemList PreviewList { get; set; } = new GenericItemList();

        public ButtonItem ImportButton { get; set; } = new ButtonItem("Import Playlists") { Icon = IconNames.download, Data1 = "Import" };

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();

        // --- Status ---
        public StatusItem Status { get; set; } = new StatusItem("Status", "No operation started yet.", ItemStatus.Unavailable);
    }
}
