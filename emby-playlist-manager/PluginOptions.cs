using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using EmbyPlaylistManager.Sorting;
using MediaBrowser.Model.Attributes;

namespace EmbyPlaylistManager
{
    public enum CollisionMode
    {
        AutoRename,
        Overwrite
    }

    // Each page of the plugin has its own options class, so Emby renders one section per page
    // (navigated from the plugin's sidebar entries) instead of one long scrolling form.

    public class ExportImportOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Playlist Manager — Export & Import";
        public override string EditorDescription => "Export all playlists to one portable JSON file, or import playlists from one.";

        public CaptionItem ExportCaption { get; set; } = new CaptionItem("Export");

        [DisplayName("Export Output Folder")]
        [Description("Folder where the exported playlists JSON file will be saved.")]
        [EditFolderPicker]
        public string ExportFolder { get; set; }

        public ButtonItem ExportButton { get; set; } = new ButtonItem("Export All Playlists") { Icon = IconNames.upload, Data1 = "Export" };

        public CaptionItem ImportCaption { get; set; } = new CaptionItem("Import");

        [DisplayName("Import File")]
        [Description("Select a previously exported playlists JSON file. Preview loads automatically.")]
        [EditFilePicker]
        [AutoPostBack("Preview", nameof(ImportFilePath))]
        public string ImportFilePath { get; set; }

        public LabelItem SelectedFileLabel { get; set; } = new LabelItem("No file selected.");

        [DisplayName("")]
        [Description("Playlists found in the selected file.")]
        public GenericItemList PreviewList { get; set; } = new GenericItemList();

        [DisplayName("Overwrite Existing Playlists")]
        [Description("When on, existing playlists are cleared and rebuilt from the import file. When off, a unique name is generated (e.g. My Playlist (1)).")]
        public bool OverwriteExisting { get; set; } = false;

        public ButtonItem ImportButton { get; set; } = new ButtonItem("Import Playlists") { Icon = IconNames.download, Data1 = "Import" };

        public StatusItem Status { get; set; } = new StatusItem("Status", "No operation started yet.", ItemStatus.Unavailable);
    }

    public class ShadowOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Playlist Manager — Shadow Playlists";
        public override string EditorDescription => "Keep a live JSON copy of every playlist for one-click repair after renames, library moves, or re-scans.";

        [DisplayName("Enable Shadow Playlists")]
        [Description("When on, a JSON copy of every playlist is kept up to date automatically. Required for repair. Turning this on for the first time will export all existing playlists to shadow files.")]
        public bool EnableShadow { get; set; } = false;

        public CaptionItem RepairCaption { get; set; } = new CaptionItem("Repair");

        [DisplayName("")]
        [Description("Broken items found across all playlists. An item is broken when it appears in the shadow but is missing from the live playlist.")]
        public GenericItemList RepairPreviewList { get; set; } = new GenericItemList();

        public ButtonItem ScanButton { get; set; } = new ButtonItem("Scan for Issues") { Icon = IconNames.search, Data1 = "Scan" };

        public ButtonItem RepairAllButton { get; set; } = new ButtonItem("Repair All") { Icon = IconNames.build, Data1 = "RepairAll" };

        [DisplayName("Full Restore Mode")]
        [Description("When off (Safe), repair only adds missing items and never removes or replaces. When on (Full Restore), the shadow is treated as authoritative — playlist contents are replaced to match the shadow exactly.")]
        public bool FullRestoreMode { get; set; } = false;

        public CaptionItem MissingCaption { get; set; } = new CaptionItem("Unresolvable Items");

        [DisplayName("")]
        [Description("Items that could not be resolved against the library after repair. Use provider IDs to search Radarr/Sonarr.")]
        public GenericItemList MissingItemsList { get; set; } = new GenericItemList();

        public ButtonItem ExportMissingButton { get; set; } = new ButtonItem("Export Missing Items") { Icon = IconNames.download, Data1 = "ExportMissing" };

        public StatusItem ShadowStatus { get; set; } = new StatusItem("Shadow Status", "Shadow system is disabled.", ItemStatus.Unavailable);
    }

    public class SortOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Playlist Manager — Sort a Playlist";
        public override string EditorDescription => "Reorder a playlist by TV order, air date, title, date added, or shuffle.";

        // Runtime-only dropdown source, never persisted.
        [Browsable(false)]
        public List<EditorSelectOption> SortPlaylistChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("Playlist")]
        [Description("The playlist to sort. Playlists that share a name are told apart by the short id in brackets.")]
        [SelectItemsSource(nameof(SortPlaylistChoices))]
        [AutoPostBack("SortChanged", nameof(SortPlaylistId))]
        public string SortPlaylistId { get; set; }

        [DisplayName("Sort by")]
        [Description("TV order sorts by series, then season, then episode (specials last). Items with no value for the chosen sort, such as no air date, always go last.")]
        [AutoPostBack("SortChanged", nameof(SortMode))]
        public PlaylistSortMode SortMode { get; set; } = PlaylistSortMode.TvOrder;

        [DisplayName("Direction")]
        [Description("Not used for shuffle.")]
        [AutoPostBack("SortChanged", nameof(SortDirectionChoice))]
        public SortDirection SortDirectionChoice { get; set; } = SortDirection.Ascending;

        public ButtonItem SortRefreshButton { get; set; } = new ButtonItem("Refresh Playlists") { Icon = IconNames.refresh, Data1 = "SortRefresh" };

        public ButtonItem SortPreviewButton { get; set; } = new ButtonItem("Preview Sort") { Icon = IconNames.search, Data1 = "SortPreview" };

        [DisplayName("")]
        [Description("The first 200 items in the new order, with where each one is now. Nothing changes until you click Apply Sort.")]
        public GenericItemList SortPreviewList { get; set; } = new GenericItemList();

        public ButtonItem SortApplyButton { get; set; } = new ButtonItem("Apply Sort") { Icon = IconNames.build, Data1 = "SortApply", IsEnabled = false };

        public StatusItem SortStatus { get; set; } = new StatusItem("Sort Status", "Choose a playlist and a sort, then click Preview Sort.", ItemStatus.Unavailable);
    }

    public class ConvertOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Playlist Manager — Convert to Collection";
        public override string EditorDescription => "Turn a playlist into a collection, item by item. The playlist is never changed.";

        // Runtime-only dropdown source, never persisted.
        [Browsable(false)]
        public List<EditorSelectOption> PlaylistChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("Playlist")]
        [Description("The playlist whose items become a collection. The playlist itself is not changed.")]
        [SelectItemsSource(nameof(PlaylistChoices))]
        [AutoPostBack("ConvertPlaylistChanged", nameof(ConvertPlaylistId))]
        public string ConvertPlaylistId { get; set; }

        [DisplayName("Collection name")]
        [Description("Leave empty to use the playlist's name. If a collection with this name already exists, the missing items are added to it and nothing is removed.")]
        [AutoPostBack("ConvertChanged", nameof(ConvertCollectionName))]
        public string ConvertCollectionName { get; set; }

        public ButtonItem ConvertPreviewButton { get; set; } = new ButtonItem("Preview Conversion") { Icon = IconNames.search, Data1 = "ConvertPreview" };

        [DisplayName("")]
        [Description("What will be added to the collection. Nothing changes until you click Create Collection.")]
        public GenericItemList ConvertPreviewList { get; set; } = new GenericItemList();

        public ButtonItem ConvertApplyButton { get; set; } = new ButtonItem("Create Collection") { Icon = IconNames.build, Data1 = "ConvertApply", IsEnabled = false };

        public StatusItem ConvertStatus { get; set; } = new StatusItem("Conversion Status", "Choose a playlist, then click Preview Conversion.", ItemStatus.Unavailable);
    }

    public class ThemesOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Playlist Manager — Themes";
        public override string EditorDescription => "Organize collections into named themes. A theme is a tag applied to a collection.";

        // Persisted: the theme names the user has defined.
        [Browsable(false)]
        public List<string> ThemeNames { get; set; } = new List<string>();

        // Runtime-only dropdown sources and displays, never persisted.
        [Browsable(false)]
        public List<EditorSelectOption> ThemeChoices { get; set; } = new List<EditorSelectOption>();
        [Browsable(false)]
        public List<EditorSelectOption> CollectionChoices { get; set; } = new List<EditorSelectOption>();

        public CaptionItem NamesCaption { get; set; } = new CaptionItem("Theme names");

        [DisplayName("New theme name")]
        [Description("Type a name and click Add Theme. A theme is a tag applied to collections.")]
        public string NewThemeName { get; set; }

        public ButtonItem AddThemeButton { get; set; } = new ButtonItem("Add Theme") { Icon = IconNames.add, Data1 = "AddTheme" };

        [DisplayName("")]
        [Description("Themes you have defined.")]
        public GenericItemList ThemeList { get; set; } = new GenericItemList();

        [DisplayName("Theme")]
        [Description("The theme to remove, or to assign to / remove from a collection below.")]
        [SelectItemsSource(nameof(ThemeChoices))]
        public string SelectedTheme { get; set; }

        public ButtonItem RemoveThemeButton { get; set; } = new ButtonItem("Remove Selected Theme") { Icon = IconNames.remove_circle_outline, Data1 = "RemoveTheme" };

        public CaptionItem AssignCaption { get; set; } = new CaptionItem("Assign collections");

        [DisplayName("Include box sets")]
        [Description("Also list auto-created movie-series box sets. When off, only user-created collections are shown.")]
        [AutoPostBack("Refresh", nameof(IncludeBoxSetsInAssign))]
        public bool IncludeBoxSetsInAssign { get; set; } = false;

        [DisplayName("Collection")]
        [Description("A collection to assign to a theme.")]
        [SelectItemsSource(nameof(CollectionChoices))]
        public string SelectedCollectionId { get; set; }

        public ButtonItem AssignThemeButton { get; set; } = new ButtonItem("Assign Theme") { Icon = IconNames.tag, Data1 = "AssignTheme" };
        public ButtonItem UnassignThemeButton { get; set; } = new ButtonItem("Remove Theme from Collection") { Icon = IconNames.playlist_remove, Data1 = "UnassignTheme" };

        [DisplayName("")]
        [Description("Collections and the themes they belong to.")]
        public GenericItemList CollectionList { get; set; } = new GenericItemList();

        public ButtonItem RefreshButton { get; set; } = new ButtonItem("Refresh") { Icon = IconNames.refresh, Data1 = "Refresh" };

        public CaptionItem HomeCaption { get; set; } = new CaptionItem("Themes on home screen");

        [DisplayName("Number of theme rows")]
        [Description("How many random theme collections to show on the home screen. Shuffle replaces them with a fresh random set.")]
        public int HomeThemeCount { get; set; } = 8;

        [DisplayName("Shuffle when a user connects")]
        [Description("Automatically re-shuffle the theme rows whenever a user or device signs in. Each user gets their own fresh random set.")]
        public bool AutoShuffleOnConnect { get; set; } = false;

        [DisplayName("Include box sets")]
        [Description("Also include movie-series box sets (collections with a TMDb/IMDb/TVDb provider id) in the shuffle pool, not just your themed collections.")]
        public bool IncludeBoxSets { get; set; } = false;

        [DisplayName("Randomize item order")]
        [Description("Show each collection's items in a random order so the first visible items change each time.")]
        public bool RandomizeItemOrder { get; set; } = true;

        public ButtonItem ShuffleHomeButton { get; set; } = new ButtonItem("Shuffle Themes on Home") { Icon = IconNames.refresh, Data1 = "ShuffleHome" };

        public StatusItem Status { get; set; } = new StatusItem("Status", "Ready.", ItemStatus.Unavailable);
    }
}
