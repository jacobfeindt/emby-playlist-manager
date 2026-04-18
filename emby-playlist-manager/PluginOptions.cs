using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;

namespace EmbyPlaylistManager
{
    public enum CollisionMode
    {
        AutoRename,
        Overwrite
    }

    public class PluginOptions : EditableOptionsBase
    {
        private const string DividerLine = "────────────────────────────────────────────────────";
        public override string EditorTitle => "Playlist Manager";
        public override string EditorDescription => "Export, import, and repair Emby playlists using provider IDs (IMDb/TMDb/TVDb) for portability across servers.";

        // --- Shadow Playlists ---
        public CaptionItem ShadowCaption { get; set; } = new CaptionItem("Shadow Playlists");

        [DisplayName("Enable Shadow Playlists")]
        [Description("When on, a JSON copy of every playlist is kept up to date automatically. Required for repair. Turning this on for the first time will export all existing playlists to shadow files.")]
        public bool EnableShadow { get; set; } = false;

        public CaptionItem RepairCaption { get; set; } = new CaptionItem("Repair");

        [DisplayName("")]
        [Description("Broken items found across all playlists. An item is broken when it appears in the shadow but is missing from the live playlist.")]
        public GenericItemList RepairPreviewList { get; set; } = new GenericItemList();

        public ButtonItem ScanButton { get; set; } = new ButtonItem("Scan for Issues") { Icon = IconNames.search, Data1 = "Scan" };

        public ButtonItem RepairAllButton { get; set; } = new ButtonItem("Repair All") { Icon = IconNames.build, Data1 = "RepairAll" };

        public StatusItem ShadowStatus { get; set; } = new StatusItem("Shadow Status", "Shadow system is disabled.", ItemStatus.Unavailable);

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public LabelItem Divider1 { get; set; } = new LabelItem(DividerLine);
        public SpacerItem Spacer1b { get; set; } = new SpacerItem();

        // --- Export ---
        public CaptionItem ExportCaption { get; set; } = new CaptionItem("Export");

        [DisplayName("Export Output Folder")]
        [Description("Folder where the exported playlists JSON file will be saved.")]
        [EditFolderPicker]
        public string ExportFolder { get; set; }

        public ButtonItem ExportButton { get; set; } = new ButtonItem("Export All Playlists") { Icon = IconNames.upload, Data1 = "Export" };

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public LabelItem Divider2 { get; set; } = new LabelItem(DividerLine);
        public SpacerItem Spacer2b { get; set; } = new SpacerItem();

        // --- Import ---
        public CaptionItem ImportCaption { get; set; } = new CaptionItem("Import");

        [DisplayName("Import File")]
        [Description("Select a previously exported playlists JSON file. Preview loads automatically.")]
        [EditFilePicker]
        [AutoPostBack("Preview", nameof(ImportFilePath))]
        public string ImportFilePath { get; set; }

        public LabelItem SelectedFileLabel { get; set; } = new LabelItem("No file selected.");

        public CaptionItem PreviewCaption { get; set; } = new CaptionItem("Playlists in File");

        [DisplayName("")]
        [Description("Playlists found in the selected file.")]
        public GenericItemList PreviewList { get; set; } = new GenericItemList();

        [DisplayName("Overwrite Existing Playlists")]
        [Description("When on, existing playlists are cleared and rebuilt from the import file. When off, a unique name is generated (e.g. My Playlist (1)).")]
        public bool OverwriteExisting { get; set; } = false;

        public ButtonItem ImportButton { get; set; } = new ButtonItem("Import Playlists") { Icon = IconNames.download, Data1 = "Import" };

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public LabelItem Divider3 { get; set; } = new LabelItem(DividerLine);
        public SpacerItem Spacer3b { get; set; } = new SpacerItem();

        // --- Status ---
        public StatusItem Status { get; set; } = new StatusItem("Status", "No operation started yet.", ItemStatus.Unavailable);
    }
}
