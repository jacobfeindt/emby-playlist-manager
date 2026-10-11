using System;

namespace EmbyPlaylistManager.Sorting
{
    /// <summary>
    /// Plain-data snapshot of one playlist row, holding only what the sorter needs. Deliberately free of Emby types
    /// so the sorting logic can be unit-tested outside the server.
    /// </summary>
    public sealed class SortEntry
    {
        /// <summary>Playlist row id (BaseItem.ListItemEntryId). This is what MoveItem and RemoveFromPlaylist take.</summary>
        public long EntryId { get; set; }

        /// <summary>Library item id (BaseItem.InternalId). The same item can appear in a playlist more than once.</summary>
        public long ItemId { get; set; }

        /// <summary>Zero-based position in the playlist when the snapshot was taken. Final tie-break, so sorts are stable.</summary>
        public int OriginalIndex { get; set; }

        public bool IsEpisode { get; set; }

        /// <summary>Series sort name for episodes. For non-episodes the factory leaves this null and the sorter uses SortName.</summary>
        public string SeriesKey { get; set; }

        public int? Season { get; set; }

        public int? Episode { get; set; }

        /// <summary>Premiere date, or 1 January of the production year when only the year is known. Null when unknown.</summary>
        public DateTime? Date { get; set; }

        /// <summary>Emby sort name (falls back to the display name). Never null.</summary>
        public string SortName { get; set; } = string.Empty;

        public DateTime? DateAdded { get; set; }

        /// <summary>Display name, used only for preview text.</summary>
        public string DisplayName { get; set; }
    }
}
