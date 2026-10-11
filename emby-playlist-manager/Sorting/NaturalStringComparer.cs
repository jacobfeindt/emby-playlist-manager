using System;
using System.Collections.Generic;

namespace EmbyPlaylistManager.Sorting
{
    /// <summary>
    /// Case-insensitive "natural" string comparison: runs of ASCII digits compare by numeric value, everything else
    /// compares character by character. Purely ordinal (no culture), so the result is identical on Windows and Linux
    /// even when the server runs with invariant globalization. Null sorts before any non-null string.
    /// </summary>
    public sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new NaturalStringComparer();

        public int Compare(string x, string y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                char cx = x[i], cy = y[j];
                if (IsDigit(cx) && IsDigit(cy))
                {
                    int si = i, sj = j;
                    while (i < x.Length && IsDigit(x[i])) i++;
                    while (j < y.Length && IsDigit(y[j])) j++;
                    int c = CompareDigitRuns(x, si, i, y, sj, j);
                    if (c != 0) return c;
                }
                else
                {
                    char ux = char.ToUpperInvariant(cx), uy = char.ToUpperInvariant(cy);
                    if (ux != uy) return ux < uy ? -1 : 1;
                    i++;
                    j++;
                }
            }

            // One string is a prefix of the other (ignoring numerically equal runs): the shorter one first.
            if (i < x.Length) return 1;
            if (j < y.Length) return -1;
            return 0;
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        private static int CompareDigitRuns(string x, int xs, int xe, string y, int ys, int ye)
        {
            while (xs < xe && x[xs] == '0') xs++;
            while (ys < ye && y[ys] == '0') ys++;
            int lx = xe - xs, ly = ye - ys;
            if (lx != ly) return lx < ly ? -1 : 1;
            for (int k = 0; k < lx; k++)
            {
                char a = x[xs + k], b = y[ys + k];
                if (a != b) return a < b ? -1 : 1;
            }
            return 0;
        }
    }
}
