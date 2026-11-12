namespace SWGLCharacterCreator;

public enum DiffKind { Same, Removed, Added }

public readonly record struct DiffLine(DiffKind Kind, string Text, int OldLine, int NewLine);

/// <summary>Line diff (LCS on the part between the common prefix and suffix) used by the review-changes screen.</summary>
public static class TextDiff
{
    /// <summary>Above this many LCS cells the changed middle is shown as one removed block followed by one added block.</summary>
    public const long MaxCells = 4_000_000;

    public static string[] SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>Every line of both texts in order, tagged Same / Removed / Added. Line numbers are 1-based, 0 when absent.</summary>
    public static List<DiffLine> Compute(string oldText, string newText)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[^(suf + 1)] == b[^(suf + 1)]) suf++;

        var result = new List<DiffLine>(a.Length + b.Length);
        for (int i = 0; i < pre; i++) result.Add(new DiffLine(DiffKind.Same, a[i], i + 1, i + 1));

        int n = a.Length - pre - suf, m = b.Length - pre - suf;
        if ((long)n * m > MaxCells)
        {
            for (int i = 0; i < n; i++) result.Add(new DiffLine(DiffKind.Removed, a[pre + i], pre + i + 1, 0));
            for (int j = 0; j < m; j++) result.Add(new DiffLine(DiffKind.Added, b[pre + j], 0, pre + j + 1));
        }
        else
        {
            // lcs[i, j] = length of the LCS of a[pre+i..] and b[pre+j..] (middle part only).
            var lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[pre + i] == b[pre + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[pre + x] == b[pre + y])
                {
                    result.Add(new DiffLine(DiffKind.Same, a[pre + x], pre + x + 1, pre + y + 1));
                    x++; y++;
                }
                else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1]))
                {
                    result.Add(new DiffLine(DiffKind.Removed, a[pre + x], pre + x + 1, 0)); x++;
                }
                else
                {
                    result.Add(new DiffLine(DiffKind.Added, b[pre + y], 0, pre + y + 1)); y++;
                }
            }
        }

        for (int k = suf; k > 0; k--)
            result.Add(new DiffLine(DiffKind.Same, a[^k], a.Length - k + 1, b.Length - k + 1));
        return result;
    }

    /// <summary>
    /// Keeps the changed lines plus <paramref name="context"/> unchanged lines around them; each gap left out
    /// becomes a null entry (drawn as "...").
    /// </summary>
    public static List<DiffLine?> WithContext(List<DiffLine> lines, int context = 3)
    {
        var keep = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffKind.Same) continue;
            for (int k = Math.Max(0, i - context); k <= Math.Min(lines.Count - 1, i + context); k++) keep[k] = true;
        }
        var result = new List<DiffLine?>();
        bool gap = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (keep[i]) { result.Add(lines[i]); gap = false; }
            else if (!gap) { result.Add(null); gap = true; }
        }
        return result;
    }
}
