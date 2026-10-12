using System;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace EmbyPlaylistManager
{
    /// <summary>
    /// The one place that reads the contents of a playlist.
    /// A bare <c>ListIds</c> query returns the rows in no guaranteed order and the rows' playlist-entry ids
    /// (<see cref="BaseItem.ListItemEntryId"/>) are what <c>RemoveFromPlaylist</c> and <c>MoveItem</c> need,
    /// so every read asks for <see cref="ItemSortBy.ListItemOrder"/> explicitly.
    /// </summary>
    public static class PlaylistQueries
    {
        /// <summary>
        /// Returns the playlist's items in playlist order. Each returned item carries its own <see cref="BaseItem.ListItemEntryId"/>;
        /// the same library item can appear more than once, each time with a different entry id.
        /// </summary>
        /// <param name="strict">
        /// When true, a failure of the ordered query is thrown (used by sorting, which must not act on a guessed order).
        /// When false the query falls back to the unordered form and logs a warning (used by read-only features such as shadows).
        /// </param>
        public static BaseItem[] GetOrderedEntries(ILibraryManager libraryManager, User user, long playlistInternalId, ILogger logger = null, bool strict = false)
        {
            try
            {
                return libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    ListIds = new[] { playlistInternalId },
                    OrderBy = new[] { (ItemSortBy.ListItemOrder, SortOrder.Ascending) }
                });
            }
            catch (Exception ex)
            {
                if (strict) throw;
                logger?.ErrorException("Ordered playlist query failed for playlist {0}; falling back to an unordered read", ex, playlistInternalId);
                return libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    ListIds = new[] { playlistInternalId }
                });
            }
        }

        /// <summary>Entry ids for removal or moving. Throws if any row has no entry id, because passing item ids instead could delete other playlists' rows.</summary>
        public static long[] GetEntryIds(BaseItem[] entries)
        {
            var ids = entries.Select(e => e.ListItemEntryId).ToArray();
            if (ids.Any(id => id == 0))
                throw new InvalidOperationException("Playlist rows were returned without entry ids; refusing to modify the playlist.");
            return ids;
        }
    }
}
