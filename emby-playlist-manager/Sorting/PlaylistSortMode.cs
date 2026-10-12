using System.ComponentModel;

namespace EmbyPlaylistManager.Sorting
{
    /// <summary>How a playlist is ordered. The Description text is what the settings page shows in the dropdown.</summary>
    public enum PlaylistSortMode
    {
        [Description("TV order (series, season, episode)")]
        TvOrder,

        [Description("Air / release date")]
        ReleaseDate,

        [Description("Title")]
        Title,

        [Description("Date added to library")]
        DateAdded,

        [Description("Shuffle (random)")]
        Shuffle
    }

    public enum SortDirection
    {
        [Description("Ascending")]
        Ascending,

        [Description("Descending")]
        Descending
    }
}
