using System;
using System.Collections.Generic;

namespace EmbyPlaylistManager.Sorting
{
    /// <summary>One MoveItem call: move the row to <see cref="NewIndex"/>, meaning its final zero-based position in the list afterwards.</summary>
    public struct MoveOp
    {
        public MoveOp(long entryId, int newIndex)
        {
            EntryId = entryId;
            NewIndex = newIndex;
        }

        public long EntryId { get; }

        public int NewIndex { get; }

        public override string ToString()
        {
            return EntryId + " -> " + NewIndex;
        }
    }

    /// <summary>
    /// Turns "current order" and "target order" into the fewest MoveItem calls. Rows that already form the longest
    /// run in the right relative order are left alone; every other row is moved once, in target order, to sit directly
    /// after its predecessor in the target. The number of moves is the playlist length minus that longest run.
    /// </summary>
    public static class MovePlanner
    {
        public static List<MoveOp> Plan(IList<long> current, IList<long> target)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (target == null) throw new ArgumentNullException(nameof(target));
            int n = current.Count;
            if (target.Count != n) throw new ArgumentException("current and target must have the same length");

            var rank = new Dictionary<long, int>(n);
            for (int i = 0; i < n; i++)
            {
                if (rank.ContainsKey(target[i])) throw new ArgumentException("target contains duplicate entry id " + target[i]);
                rank[target[i]] = i;
            }

            var seq = new int[n];
            var seen = new HashSet<long>();
            for (int i = 0; i < n; i++)
            {
                if (!seen.Add(current[i])) throw new ArgumentException("current contains duplicate entry id " + current[i]);
                if (!rank.TryGetValue(current[i], out int r)) throw new ArgumentException("entry id " + current[i] + " is not in target");
                seq[i] = r;
            }

            var settled = new bool[n]; // indexed by target rank
            foreach (int r in LongestIncreasingSubsequence(seq)) settled[r] = true;

            var sim = new List<long>(current);
            var ops = new List<MoveOp>();
            for (int r = 0; r < n; r++)
            {
                if (settled[r]) continue;
                long id = target[r];
                sim.RemoveAt(sim.IndexOf(id));
                int newIndex = r == 0 ? 0 : sim.IndexOf(target[r - 1]) + 1;
                sim.Insert(newIndex, id);
                ops.Add(new MoveOp(id, newIndex));
            }
            return ops;
        }

        /// <summary>Applies planned moves to a copy of <paramref name="start"/>, mirroring what MoveItem does to the stored order.</summary>
        public static List<long> Simulate(IList<long> start, IEnumerable<MoveOp> ops)
        {
            var list = new List<long>(start);
            foreach (var op in ops)
            {
                int old = list.IndexOf(op.EntryId);
                if (old < 0) throw new InvalidOperationException("entry id " + op.EntryId + " not in list");
                list.RemoveAt(old);
                list.Insert(op.NewIndex, op.EntryId);
            }
            return list;
        }

        /// <summary>Number of positions whose row differs between the two orders (what a preview reports as "items change position").</summary>
        public static int CountChangedPositions(IList<long> current, IList<long> target)
        {
            int changed = 0;
            int n = Math.Min(current.Count, target.Count);
            for (int i = 0; i < n; i++)
            {
                if (current[i] != target[i]) changed++;
            }
            return changed + Math.Abs(current.Count - target.Count);
        }

        /// <summary>Returns the values (not indexes) of one longest strictly increasing subsequence of <paramref name="seq"/>.</summary>
        private static List<int> LongestIncreasingSubsequence(int[] seq)
        {
            int n = seq.Length;
            var tails = new List<int>();        // tails[k] = index into seq of the smallest tail of an increasing run of length k+1
            var prev = new int[n];
            for (int i = 0; i < n; i++)
            {
                int lo = 0, hi = tails.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (seq[tails[mid]] < seq[i]) lo = mid + 1; else hi = mid;
                }
                prev[i] = lo > 0 ? tails[lo - 1] : -1;
                if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
            }

            var result = new List<int>(tails.Count);
            if (tails.Count == 0) return result;
            for (int k = tails[tails.Count - 1]; k >= 0; k = prev[k]) result.Add(seq[k]);
            result.Reverse();
            return result;
        }
    }
}
