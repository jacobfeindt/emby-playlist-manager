using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyPlaylistManager.Conversion
{
    /// <summary>
    /// Pure helpers for turning a playlist's rows into a collection's item list.
    /// No Emby types, so the test project can compile this file directly.
    /// </summary>
    public static class ConversionPlanner
    {
        /// <summary>Distinct ids in first-occurrence order. A playlist can hold one library item several times; a collection is a set.</summary>
        public static List<long> UniqueInOrder(IEnumerable<long> ids)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            var seen = new HashSet<long>();
            var result = new List<long>();
            foreach (var id in ids)
            {
                if (seen.Add(id)) result.Add(id);
            }
            return result;
        }

        /// <summary>The wanted ids that the collection does not hold yet, distinct and in wanted order.</summary>
        public static List<long> Missing(IEnumerable<long> wanted, IEnumerable<long> alreadyInCollection)
        {
            if (wanted == null) throw new ArgumentNullException(nameof(wanted));
            var have = new HashSet<long>(alreadyInCollection ?? Enumerable.Empty<long>());
            return UniqueInOrder(wanted).Where(id => !have.Contains(id)).ToList();
        }

        /// <summary>Splits ids into batches of at most <paramref name="size"/>, keeping order.</summary>
        public static List<long[]> Chunk(IList<long> ids, int size)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));
            var chunks = new List<long[]>();
            for (int i = 0; i < ids.Count; i += size)
            {
                int n = Math.Min(size, ids.Count - i);
                var chunk = new long[n];
                for (int j = 0; j < n; j++) chunk[j] = ids[i + j];
                chunks.Add(chunk);
            }
            return chunks;
        }

        /// <summary>The collection name to use: the typed name when there is one, otherwise the playlist's own name. Never null.</summary>
        public static string ResolveName(string requested, string playlistName)
        {
            var name = (requested ?? string.Empty).Trim();
            if (name.Length == 0) name = (playlistName ?? string.Empty).Trim();
            return name;
        }
    }
}
