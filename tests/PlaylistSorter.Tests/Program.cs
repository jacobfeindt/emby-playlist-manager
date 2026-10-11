using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using EmbyPlaylistManager.Sorting;

internal static class Program
{
    private static int _passed, _failed;

    private static int Main(string[] args)
    {
        Run("natural compare: numbers by value, case-insensitive, null first", NaturalCompare);
        Run("TV order: null season/episode last, specials after seasons, movies interleave", TvOrder);
        Run("TV order descending keeps nulls last", TvOrderDescending);
        Run("release date: ascending/descending, unknown last, same-day parts stay in episode order", ReleaseDate);
        Run("title: natural order, ascending and descending", Title);
        Run("date added: ascending/descending, unknown last", DateAdded);
        Run("shuffle: permutation, repeatable per seed, different per seed", Shuffle);
        Run("re-sorting a sorted playlist plans zero moves (all modes)", Idempotent);
        Run("planner: sorted input gives 0 moves, reversed gives n-1", PlannerExtremes);
        Run("planner: single misplaced row takes exactly one move", PlannerSingleMove);
        Run("planner: 1,000 random permutations, result == target and moves == n - LIS", PlannerProperty);
        Run("planner: duplicate library items (distinct entry ids) are handled", PlannerDuplicateItems);
        Run("planner: invalid input is rejected", PlannerInvalid);
        Run("fixture: generated 2,769-row playlist shuffled and TV-sorted", Fixture);

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? $"ALL {_passed} TESTS PASSED" : $"{_failed} FAILED, {_passed} passed");
        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- tests

    private static void NaturalCompare()
    {
        var c = NaturalStringComparer.Instance;
        True(c.Compare("Ep 2", "Ep 10") < 0, "Ep 2 < Ep 10");
        True(c.Compare("ep 10", "EP 10") == 0, "case-insensitive");
        True(c.Compare("a01", "a1") == 0, "leading zeros equal");
        True(c.Compare("a2b", "a2c") < 0, "text after number");
        True(c.Compare("abc", "abcd") < 0, "prefix first");
        True(c.Compare(null, "a") < 0 && c.Compare("a", null) > 0 && c.Compare(null, null) == 0, "null first");
        True(c.Compare("Season 1 Episode 12", "Season 1 Episode 9") > 0, "12 > 9");
        var sorted = new[] { "x10", "x2", "X1", "x20", "x3" }.OrderBy(s => s, c).ToArray();
        Equal("X1,x2,x3,x10,x20", string.Join(",", sorted), "sequence");
    }

    private static void TvOrder()
    {
        var e = new List<SortEntry>
        {
            Ep(0, "The Simpsons", 2, 1, "b"),
            Movie(1, "A Christmas Story"),
            Ep(2, "The Simpsons", 1, 10, "c"),
            Ep(3, "The Simpsons", 1, 2, "d"),
            Ep(4, "The Simpsons", 0, 5, "special"),          // specials after regular seasons
            Ep(5, "The Simpsons", null, null, "unknown"),    // unknown season last
            Ep(6, "Bob's Burgers", 3, 9, "e"),
            Movie(7, "Zodiac"),
        };
        var t = Ids(PlaylistSorter.BuildTarget(e, PlaylistSortMode.TvOrder, SortDirection.Ascending, 0));
        // "A Christmas Story" < "Bob's Burgers" < "The Simpsons"(series key sort name, article kept here) < "Zodiac"
        Equal("1,6,3,2,0,4,5,7", t, "tv order");
    }

    private static void TvOrderDescending()
    {
        var e = new List<SortEntry>
        {
            Ep(0, "Show", 1, 1, "a"),
            Ep(1, "Show", 2, 1, "b"),
            Ep(2, "Show", null, null, "unknown"),
            Ep(3, "Show", 2, 2, "c"),
        };
        var t = Ids(PlaylistSorter.BuildTarget(e, PlaylistSortMode.TvOrder, SortDirection.Descending, 0));
        Equal("3,1,0,2", t, "descending, unknown season last");
    }

    private static void ReleaseDate()
    {
        var e = new List<SortEntry>
        {
            Dated(0, "c", D(2001, 5, 1)),
            Dated(1, "a", D(1999, 12, 24)),
            Dated(2, "b", null),
            Dated(3, "d", D(2001, 5, 1)),
        };
        Equal("1,0,3,2", Ids(PlaylistSorter.BuildTarget(e, PlaylistSortMode.ReleaseDate, SortDirection.Ascending, 0)), "ascending (ties by name then position)");
        Equal("0,3,1,2", Ids(PlaylistSorter.BuildTarget(e, PlaylistSortMode.ReleaseDate, SortDirection.Descending, 0)), "descending, unknown last");

        // same-day parts of one series stay in episode order even though the originals are reversed
        var parts = new List<SortEntry>
        {
            WithDate(Ep(0, "Show", 1, 2, "Part 2"), D(2010, 12, 10)),
            WithDate(Ep(1, "Show", 1, 1, "Part 1"), D(2010, 12, 10)),
        };
        Equal("1,0", Ids(PlaylistSorter.BuildTarget(parts, PlaylistSortMode.ReleaseDate, SortDirection.Ascending, 0)), "same day, episode order");
    }

    private static void Title()
    {
        var e = new List<SortEntry> { Named(0, "Episode 10"), Named(1, "episode 2"), Named(2, "Alpha"), Named(3, "Zeta") };
        Equal("2,1,0,3", Ids(PlaylistSorter.BuildTarget(e, PlaylistSortMode.Title, SortDirection.Ascending, 0)), "ascending");
        Equal("3,0,1,2", Ids(PlaylistSorter.BuildTarget(e, PlaylistSortMode.Title, SortDirection.Descending, 0)), "descending");
    }

    private static void DateAdded()
    {
        var e = new List<SortEntry>
        {
            Added(0, "a", D(2020, 1, 1)), Added(1, "b", null), Added(2, "c", D(2019, 1, 1)), Added(3, "d", D(2021, 1, 1)),
        };
        Equal("2,0,3,1", Ids(PlaylistSorter.BuildTarget(e, PlaylistSortMode.DateAdded, SortDirection.Ascending, 0)), "ascending");
        Equal("3,0,2,1", Ids(PlaylistSorter.BuildTarget(e, PlaylistSortMode.DateAdded, SortDirection.Descending, 0)), "descending, unknown last");
    }

    private static void Shuffle()
    {
        var e = Enumerable.Range(0, 50).Select(i => Named(i, "n" + i)).ToList();
        var a = Ids(PlaylistSorter.BuildTarget(e, PlaylistSortMode.Shuffle, SortDirection.Ascending, 42));
        var b = Ids(PlaylistSorter.BuildTarget(e, PlaylistSortMode.Shuffle, SortDirection.Descending, 42));
        var c = Ids(PlaylistSorter.BuildTarget(e, PlaylistSortMode.Shuffle, SortDirection.Ascending, 43));
        Equal(a, b, "same seed, direction ignored");
        True(a != c, "different seed gives a different order");
        Equal(string.Join(",", Enumerable.Range(0, 50)), string.Join(",", a.Split(',').Select(int.Parse).OrderBy(x => x)), "is a permutation");
        True(a != string.Join(",", Enumerable.Range(0, 50)), "actually shuffled");
    }

    private static void Idempotent()
    {
        var rng = new Random(7);
        foreach (PlaylistSortMode mode in Enum.GetValues(typeof(PlaylistSortMode)))
        {
            if (mode == PlaylistSortMode.Shuffle) continue;
            foreach (SortDirection dir in Enum.GetValues(typeof(SortDirection)))
            {
                var entries = RandomEntries(rng, 200);
                var target = PlaylistSorter.BuildTarget(entries, mode, dir, 0);
                var reindexed = target.Select((t, i) => Clone(t, i)).ToList();
                var again = PlaylistSorter.BuildTarget(reindexed, mode, dir, 0);
                var plan = MovePlanner.Plan(reindexed.Select(x => x.EntryId).ToList(), again.Select(x => x.EntryId).ToList());
                Equal(0, plan.Count, mode + "/" + dir + " second sort plans no moves");
            }
        }
    }

    private static void PlannerExtremes()
    {
        var ids = Enumerable.Range(1, 50).Select(i => (long)i).ToList();
        Equal(0, MovePlanner.Plan(ids, ids).Count, "sorted -> 0 moves");
        var rev = ids.AsEnumerable().Reverse().ToList();
        var ops = MovePlanner.Plan(rev, ids);
        Equal(49, ops.Count, "reversed -> n-1 moves");
        Equal(string.Join(",", ids), string.Join(",", MovePlanner.Simulate(rev, ops)), "reversed result");
        Equal(0, MovePlanner.Plan(new List<long>(), new List<long>()).Count, "empty");
        Equal(0, MovePlanner.Plan(new List<long> { 9 }, new List<long> { 9 }).Count, "single");
    }

    private static void PlannerSingleMove()
    {
        var target = new List<long> { 1, 2, 3, 4, 5 };
        var current = new List<long> { 5, 1, 2, 3, 4 };     // greedy left-to-right would need 4 moves
        var ops = MovePlanner.Plan(current, target);
        Equal(1, ops.Count, "one move");
        Equal(string.Join(",", target), string.Join(",", MovePlanner.Simulate(current, ops)), "result");
        Equal(4, ops[0].NewIndex, "moves 5 to the end");
    }

    private static void PlannerProperty()
    {
        var rng = new Random(12345);
        for (int iter = 0; iter < 1000; iter++)
        {
            int n = rng.Next(0, 40);
            var target = Enumerable.Range(1, n).Select(i => (long)i * 7).ToList();
            var current = target.OrderBy(_ => rng.Next()).ToList();
            var ops = MovePlanner.Plan(current, target);
            var result = MovePlanner.Simulate(current, ops);
            True(result.SequenceEqual(target), "iteration " + iter + ": result equals target");
            Equal(n - LisLength(current, target), ops.Count, "iteration " + iter + ": minimal move count");
            True(ops.All(o => o.NewIndex >= 0 && o.NewIndex < Math.Max(n, 1)), "indexes in range");
        }
    }

    private static void PlannerDuplicateItems()
    {
        // The same library item (ItemId 100) twice, with distinct entry ids; the planner works on entry ids only.
        var current = new List<SortEntry>
        {
            new SortEntry { EntryId = 11, ItemId = 100, OriginalIndex = 0, SortName = "b" },
            new SortEntry { EntryId = 12, ItemId = 200, OriginalIndex = 1, SortName = "a" },
            new SortEntry { EntryId = 13, ItemId = 100, OriginalIndex = 2, SortName = "b" },
        };
        var target = PlaylistSorter.BuildTarget(current, PlaylistSortMode.Title, SortDirection.Ascending, 0);
        Equal("12,11,13", string.Join(",", target.Select(t => t.EntryId)), "stable for equal names");
        var ops = MovePlanner.Plan(current.Select(c => c.EntryId).ToList(), target.Select(t => t.EntryId).ToList());
        Equal("12,11,13", string.Join(",", MovePlanner.Simulate(current.Select(c => c.EntryId).ToList(), ops)), "applied");
    }

    private static void PlannerInvalid()
    {
        Throws(() => MovePlanner.Plan(new List<long> { 1, 2 }, new List<long> { 1 }), "length mismatch");
        Throws(() => MovePlanner.Plan(new List<long> { 1, 2 }, new List<long> { 1, 3 }), "different ids");
        Throws(() => MovePlanner.Plan(new List<long> { 1, 1 }, new List<long> { 1, 2 }), "duplicate in current");
        Throws(() => MovePlanner.Plan(new List<long> { 1, 2 }, new List<long> { 2, 2 }), "duplicate in target");
    }

    private static void Fixture()
    {
        // A deterministic 2,769-row playlist grouped by series, like a real episode playlist:
        // 40 series, a couple of Season 0 specials each, then 12 episodes per season, with air dates.
        var entries = new List<SortEntry>();
        for (int s = 0; s < 40; s++)
        {
            int count = s < 9 ? 70 : 69;
            string series = "Show " + (s + 1).ToString("00", CultureInfo.InvariantCulture);
            for (int i = 0; i < count; i++)
            {
                int season = i < 2 ? 0 : 1 + (i - 2) / 12;
                int episode = i < 2 ? i + 1 : 1 + (i - 2) % 12;
                DateTime date = new DateTime(1990, 1, 1).AddDays(s * 37 + i * 7);
                string title = series + " S" + season + "E" + episode;
                entries.Add(new SortEntry
                {
                    EntryId = 1000 + entries.Count, ItemId = 5000 + entries.Count, OriginalIndex = entries.Count, IsEpisode = true,
                    SeriesKey = series, Season = season, Episode = episode, Date = date,
                    SortName = title, DateAdded = date, DisplayName = title
                });
            }
        }
        Equal(2769, entries.Count, "generated rows");

        var shuffled = PlaylistSorter.BuildTarget(entries, PlaylistSortMode.Shuffle, SortDirection.Ascending, 99).Select((e, i) => Clone(e, i)).ToList();
        var sw = Stopwatch.StartNew();
        var target = PlaylistSorter.BuildTarget(shuffled, PlaylistSortMode.TvOrder, SortDirection.Ascending, 0);
        var current = shuffled.Select(e => e.EntryId).ToList();
        var tIds = target.Select(e => e.EntryId).ToList();
        var ops = MovePlanner.Plan(current, tIds);
        sw.Stop();
        True(MovePlanner.Simulate(current, ops).SequenceEqual(tIds), "simulated moves reach the target");
        True(ops.Count < current.Count, "fewer moves than rows (" + ops.Count + " of " + current.Count + ")");
        Console.WriteLine("      planned " + ops.Count + " moves for " + current.Count + " rows in " + sw.ElapsedMilliseconds + " ms");
        True(sw.ElapsedMilliseconds < 5000, "planning is fast");

        // In TV order every series must form one contiguous block with episodes ascending.
        var seen = new HashSet<string>();
        string last = null; int lastSeason = -1, lastEp = -1;
        foreach (var e in target)
        {
            if (e.SeriesKey != last)
            {
                True(seen.Add(e.SeriesKey), "series block '" + e.SeriesKey + "' is contiguous");
                last = e.SeriesKey; lastSeason = -1; lastEp = -1;
            }
            int s = e.Season == 0 ? int.MaxValue : e.Season.Value;
            True(s > lastSeason || (s == lastSeason && e.Episode.Value >= lastEp), "episodes ascending within " + e.SeriesKey);
            lastSeason = s; lastEp = e.Episode.Value;
        }
    }

    // ---------------------------------------------------------------- builders and helpers

    private static SortEntry Ep(int id, string series, int? season, int? episode, string name)
    {
        return new SortEntry { EntryId = id, ItemId = id, OriginalIndex = id, IsEpisode = true, SeriesKey = series, Season = season, Episode = episode, SortName = name, DisplayName = name };
    }

    private static SortEntry Movie(int id, string name)
    {
        return new SortEntry { EntryId = id, ItemId = id, OriginalIndex = id, SortName = name, DisplayName = name };
    }

    private static SortEntry Named(int id, string name) { return Movie(id, name); }

    private static SortEntry Dated(int id, string name, DateTime? date)
    {
        var e = Movie(id, name); e.Date = date; return e;
    }

    private static SortEntry WithDate(SortEntry e, DateTime? date) { e.Date = date; return e; }

    private static SortEntry Added(int id, string name, DateTime? added)
    {
        var e = Movie(id, name); e.DateAdded = added; return e;
    }

    private static SortEntry Clone(SortEntry e, int newIndex)
    {
        return new SortEntry
        {
            EntryId = e.EntryId, ItemId = e.ItemId, OriginalIndex = newIndex, IsEpisode = e.IsEpisode, SeriesKey = e.SeriesKey, Season = e.Season,
            Episode = e.Episode, Date = e.Date, SortName = e.SortName, DateAdded = e.DateAdded, DisplayName = e.DisplayName
        };
    }

    private static List<SortEntry> RandomEntries(Random rng, int n)
    {
        var list = new List<SortEntry>();
        for (int i = 0; i < n; i++)
        {
            bool episode = rng.Next(3) != 0;
            var e = new SortEntry
            {
                EntryId = 100 + i, ItemId = rng.Next(50), OriginalIndex = i, IsEpisode = episode,
                SeriesKey = episode ? "Series " + rng.Next(8) : null,
                Season = episode && rng.Next(8) != 0 ? (int?)rng.Next(0, 5) : null,
                Episode = episode && rng.Next(8) != 0 ? (int?)rng.Next(1, 12) : null,
                Date = rng.Next(5) == 0 ? (DateTime?)null : D(1990 + rng.Next(30), 1 + rng.Next(12), 1 + rng.Next(28)),
                DateAdded = rng.Next(5) == 0 ? (DateTime?)null : D(2015 + rng.Next(10), 1 + rng.Next(12), 1 + rng.Next(28)),
                SortName = "name " + rng.Next(40),
            };
            list.Add(e);
        }
        return list;
    }

    private static DateTime D(int y, int m, int d) { return new DateTime(y, m, d); }

    private static string Ids(IEnumerable<SortEntry> e) { return string.Join(",", e.Select(x => x.EntryId)); }

    private static int LisLength(IList<long> current, IList<long> target)
    {
        var rank = new Dictionary<long, int>();
        for (int i = 0; i < target.Count; i++) rank[target[i]] = i;
        var s = current.Select(c => rank[c]).ToArray();
        var best = new int[s.Length];
        int max = 0;
        for (int i = 0; i < s.Length; i++)
        {
            best[i] = 1;
            for (int j = 0; j < i; j++) if (s[j] < s[i] && best[j] + 1 > best[i]) best[i] = best[j] + 1;
            max = Math.Max(max, best[i]);
        }
        return max;
    }

    private static void Run(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine("  PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("  FAIL  " + name + "\n        " + ex.Message); }
    }

    private static void True(bool condition, string what) { if (!condition) throw new Exception("expected true: " + what); }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(what + ": expected <" + expected + "> but was <" + actual + ">");
    }

    private static void Throws(Action a, string what)
    {
        try { a(); } catch (ArgumentException) { return; }
        throw new Exception("expected ArgumentException: " + what);
    }
}
