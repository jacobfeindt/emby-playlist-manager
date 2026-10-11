using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyPlaylistManager.Sorting
{
    /// <summary>
    /// Computes the target order of a playlist. The order is total and deterministic: unknown values (null) always sort
    /// last in both directions, the direction applies to the sort key only, and the final tie-break is the entry's
    /// original position. Sorting an already sorted playlist therefore changes nothing.
    /// </summary>
    public static class PlaylistSorter
    {
        /// <summary>Season 0 (specials) sorts after the regular seasons of the same series.</summary>
        private const int SpecialsSeasonKey = int.MaxValue;

        public static List<SortEntry> BuildTarget(IEnumerable<SortEntry> entries, PlaylistSortMode mode, SortDirection direction, int seed)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            var list = entries.OrderBy(e => e.OriginalIndex).ToList();

            if (mode == PlaylistSortMode.Shuffle)
            {
                return Shuffle(list, seed);
            }

            bool desc = direction == SortDirection.Descending;
            Comparison<SortEntry> cmp;
            switch (mode)
            {
                case PlaylistSortMode.TvOrder:
                    cmp = (a, b) => CompareTv(a, b, desc);
                    break;
                case PlaylistSortMode.ReleaseDate:
                    cmp = (a, b) =>
                    {
                        int c = CompareNullable(a.Date?.Date, b.Date?.Date, desc);
                        if (c != 0) return c;
                        // Same day (or both unknown): keep parts of the same series together and in episode order.
                        c = CompareTv(a, b, false);
                        return c != 0 ? c : a.OriginalIndex.CompareTo(b.OriginalIndex);
                    };
                    break;
                case PlaylistSortMode.Title:
                    cmp = (a, b) =>
                    {
                        int c = Apply(NaturalStringComparer.Instance.Compare(a.SortName, b.SortName), desc);
                        return c != 0 ? c : a.OriginalIndex.CompareTo(b.OriginalIndex);
                    };
                    break;
                case PlaylistSortMode.DateAdded:
                    cmp = (a, b) =>
                    {
                        int c = CompareNullable(a.DateAdded, b.DateAdded, desc);
                        if (c != 0) return c;
                        c = CompareTv(a, b, false);
                        return c != 0 ? c : a.OriginalIndex.CompareTo(b.OriginalIndex);
                    };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }

            list.Sort(cmp);
            return list;
        }

        private static int CompareTv(SortEntry a, SortEntry b, bool desc)
        {
            // Non-episodes use their own sort name as the "series", so movies interleave with series alphabetically.
            string sa = a.SeriesKey ?? a.SortName;
            string sb = b.SeriesKey ?? b.SortName;
            int c = Apply(NaturalStringComparer.Instance.Compare(sa, sb), desc);
            if (c != 0) return c;

            c = CompareNullable(SeasonKey(a.Season), SeasonKey(b.Season), desc);
            if (c != 0) return c;

            c = CompareNullable(a.Episode, b.Episode, desc);
            if (c != 0) return c;

            c = Apply(NaturalStringComparer.Instance.Compare(a.SortName, b.SortName), desc);
            return c != 0 ? c : a.OriginalIndex.CompareTo(b.OriginalIndex);
        }

        private static int? SeasonKey(int? season)
        {
            if (!season.HasValue) return null;
            return season.Value == 0 ? SpecialsSeasonKey : season;
        }

        private static int Apply(int comparison, bool desc)
        {
            return desc ? -comparison : comparison;
        }

        /// <summary>Compares nullable values; null is always last regardless of direction.</summary>
        private static int CompareNullable<T>(T? a, T? b, bool desc) where T : struct, IComparable<T>
        {
            if (!a.HasValue && !b.HasValue) return 0;
            if (!a.HasValue) return 1;
            if (!b.HasValue) return -1;
            return Apply(a.Value.CompareTo(b.Value), desc);
        }

        private static List<SortEntry> Shuffle(List<SortEntry> list, int seed)
        {
            var rng = new Random(seed);
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
            return list;
        }
    }
}
