namespace MySync.Domain.Diffing;

public enum EditKind
{
    Equal,
    Delete,
    Insert,
}

/// <summary>
/// One line of an edit script. <see cref="OldIndex"/> and <see cref="NewIndex"/> are 0-based positions
/// in each sequence: for an insert, <see cref="OldIndex"/> is how many old lines come before it; for a
/// delete, <see cref="NewIndex"/> is how many new lines come before it.
/// </summary>
public readonly record struct Edit(EditKind Kind, int OldIndex, int NewIndex);

/// <summary>
/// A minimal line edit script between two sequences: Myers' O(ND) algorithm in its linear-space form
/// (the "middle snake", recursing on both halves), after trimming the common prefix and suffix.
/// Lines compare ordinally. Within a changed region deletions come before insertions, as in diff/git.
/// </summary>
/// <remarks>
/// If the search exceeds <see cref="DiffLimits.MaxSearchSteps"/> the remaining region is replaced as a
/// block: the script is still correct (it turns A into B), just not minimal.
/// </remarks>
public static class LineDiff
{
    public static IReadOnlyList<Edit> Compute(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines) =>
        Compute(oldLines, newLines, DiffLimits.MaxSearchSteps);

    internal static IReadOnlyList<Edit> Compute(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, long maxSearchSteps)
    {
        ArgumentNullException.ThrowIfNull(oldLines);
        ArgumentNullException.ThrowIfNull(newLines);

        Dictionary<string, int> ids = new(StringComparer.Ordinal);
        int[] a = ToIds(oldLines, ids);
        int[] b = ToIds(newLines, ids);

        Search search = new(a, b, maxSearchSteps);
        search.Compare(0, a.Length, 0, b.Length);
        return search.Script();
    }

    private static int[] ToIds(IReadOnlyList<string> lines, Dictionary<string, int> ids)
    {
        int[] result = new int[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (!ids.TryGetValue(lines[i], out int id))
            {
                id = ids.Count;
                ids.Add(lines[i], id);
            }

            result[i] = id;
        }

        return result;
    }

    private sealed class Search(int[] a, int[] b, long budget)
    {
        private readonly bool[] _deleted = new bool[a.Length];
        private readonly bool[] _inserted = new bool[b.Length];
        private long _budget = budget;

        public void Compare(int aLo, int aHi, int bLo, int bHi)
        {
            while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo])
            {
                aLo++;
                bLo++;
            }

            while (aLo < aHi && bLo < bHi && a[aHi - 1] == b[bHi - 1])
            {
                aHi--;
                bHi--;
            }

            if (aLo == aHi || bLo == bHi || _budget <= 0)
            {
                MarkReplaced(aLo, aHi, bLo, bHi);
                return;
            }

            (int x, int y)? split = Bisect(aLo, aHi - aLo, bLo, bHi - bLo);
            if (split is not { } point)
            {
                MarkReplaced(aLo, aHi, bLo, bHi);
                return;
            }

            Compare(aLo, aLo + point.x, bLo, bLo + point.y);
            Compare(aLo + point.x, aHi, bLo + point.y, bHi);
        }

        public List<Edit> Script()
        {
            List<Edit> script = new(Math.Max(a.Length, b.Length));
            int i = 0, j = 0;
            while (i < a.Length || j < b.Length)
            {
                if (i < a.Length && _deleted[i])
                {
                    script.Add(new Edit(EditKind.Delete, i++, j));
                }
                else if (j < b.Length && _inserted[j])
                {
                    script.Add(new Edit(EditKind.Insert, i, j++));
                }
                else
                {
                    script.Add(new Edit(EditKind.Equal, i++, j++));
                }
            }

            return script;
        }

        private void MarkReplaced(int aLo, int aHi, int bLo, int bHi)
        {
            Array.Fill(_deleted, true, aLo, aHi - aLo);
            Array.Fill(_inserted, true, bLo, bHi - bLo);
        }

        /// <summary>
        /// Finds where a shortest path's forward and reverse searches meet (Myers' middle snake),
        /// for the non-empty ranges a[aLo..aLo+n) and b[bLo..bLo+m). <c>null</c> only when the budget runs out.
        /// </summary>
        private (int x, int y)? Bisect(int aLo, int n, int bLo, int m)
        {
            int maxD = (n + m + 1) / 2;
            int offset = maxD + 1;
            int length = 2 * maxD + 2;
            int[] forward = new int[length];
            int[] reverse = new int[length];
            Array.Fill(forward, -1);
            Array.Fill(reverse, -1);
            forward[offset + 1] = 0;
            reverse[offset + 1] = 0;

            int delta = n - m;
            bool front = (delta & 1) != 0; // the forward search meets the reverse one when delta is odd
            int k1Start = 0, k1End = 0, k2Start = 0, k2End = 0;

            for (int d = 0; d < maxD; d++)
            {
                for (int k1 = -d + k1Start; k1 <= d - k1End; k1 += 2)
                {
                    int k1Offset = offset + k1;
                    int x1 = k1 == -d || k1 != d && forward[k1Offset - 1] < forward[k1Offset + 1]
                        ? forward[k1Offset + 1]
                        : forward[k1Offset - 1] + 1;
                    int y1 = x1 - k1;
                    while (x1 < n && y1 < m && a[aLo + x1] == b[bLo + y1])
                    {
                        x1++;
                        y1++;
                    }

                    forward[k1Offset] = x1;
                    if (--_budget <= 0)
                    {
                        return null;
                    }

                    if (x1 > n)
                    {
                        k1End += 2;
                    }
                    else if (y1 > m)
                    {
                        k1Start += 2;
                    }
                    else if (front)
                    {
                        int k2Offset = offset + delta - k1;
                        if (k2Offset >= 0 && k2Offset < length && reverse[k2Offset] != -1 && x1 >= n - reverse[k2Offset])
                        {
                            return (x1, y1);
                        }
                    }
                }

                for (int k2 = -d + k2Start; k2 <= d - k2End; k2 += 2)
                {
                    int k2Offset = offset + k2;
                    int x2 = k2 == -d || k2 != d && reverse[k2Offset - 1] < reverse[k2Offset + 1]
                        ? reverse[k2Offset + 1]
                        : reverse[k2Offset - 1] + 1;
                    int y2 = x2 - k2;
                    while (x2 < n && y2 < m && a[aLo + n - x2 - 1] == b[bLo + m - y2 - 1])
                    {
                        x2++;
                        y2++;
                    }

                    reverse[k2Offset] = x2;
                    if (--_budget <= 0)
                    {
                        return null;
                    }

                    if (x2 > n)
                    {
                        k2End += 2;
                    }
                    else if (y2 > m)
                    {
                        k2Start += 2;
                    }
                    else if (!front)
                    {
                        int k1Offset = offset + delta - k2;
                        if (k1Offset >= 0 && k1Offset < length && forward[k1Offset] != -1)
                        {
                            int x1 = forward[k1Offset];
                            int y1 = offset + x1 - k1Offset;
                            if (x1 >= n - x2)
                            {
                                return (x1, y1);
                            }
                        }
                    }
                }
            }

            return null;
        }
    }
}
