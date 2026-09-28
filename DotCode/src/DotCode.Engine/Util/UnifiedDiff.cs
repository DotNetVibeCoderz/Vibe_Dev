using System.Text;

namespace DotCode.Engine.Util;

/// <summary>Line-based Myers diff producing unified-diff hunks. Common prefix/suffix are trimmed first so the
/// O(ND) core only runs on the changed region (edits are usually small and local).</summary>
public static class UnifiedDiff
{
    public static string Create(string oldText, string newText, string path, int context = 3)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        var ops = Diff(a, b);
        if (ops.All(o => o.Kind == ' ')) return "";

        var sb = new StringBuilder();
        sb.Append("--- a/").Append(path).Append('\n');
        sb.Append("+++ b/").Append(path).Append('\n');

        // Group ops into hunks with `context` lines around changes.
        var i = 0;
        while (i < ops.Count)
        {
            while (i < ops.Count && ops[i].Kind == ' ') i++;
            if (i >= ops.Count) break;
            var start = Math.Max(0, i - context);
            var end = i;
            var lastChange = i;
            while (end < ops.Count)
            {
                if (ops[end].Kind != ' ') lastChange = end;
                else if (end - lastChange > context * 2) break;
                end++;
            }
            end = Math.Min(ops.Count, lastChange + context + 1);

            int oldStart = ops[start].OldLine, newStart = ops[start].NewLine;
            int oldCount = 0, newCount = 0;
            for (var k = start; k < end; k++)
            {
                if (ops[k].Kind != '+') oldCount++;
                if (ops[k].Kind != '-') newCount++;
            }
            sb.Append("@@ -").Append(oldCount == 0 ? oldStart : oldStart + 1).Append(',').Append(oldCount)
              .Append(" +").Append(newCount == 0 ? newStart : newStart + 1).Append(',').Append(newCount).Append(" @@\n");
            for (var k = start; k < end; k++) sb.Append(ops[k].Kind).Append(ops[k].Text).Append('\n');
            i = end;
        }
        return sb.ToString();
    }

    public static (int Added, int Removed) Count(string diff)
    {
        int add = 0, rem = 0;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) continue;
            if (line.StartsWith('+')) add++;
            else if (line.StartsWith('-')) rem++;
        }
        return (add, rem);
    }

    public static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    public readonly record struct Op(char Kind, string Text, int OldLine, int NewLine);

    public static List<Op> Diff(string[] a, string[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

        var ops = new List<Op>(a.Length + b.Length);
        for (var k = 0; k < prefix; k++) ops.Add(new Op(' ', a[k], k, k));

        var aMid = a.AsSpan(prefix, a.Length - prefix - suffix).ToArray();
        var bMid = b.AsSpan(prefix, b.Length - prefix - suffix).ToArray();
        foreach (var op in Myers(aMid, bMid))
            ops.Add(op with { OldLine = op.OldLine + prefix, NewLine = op.NewLine + prefix });

        for (var k = 0; k < suffix; k++)
        {
            var ai = a.Length - suffix + k;
            ops.Add(new Op(' ', a[ai], ai, b.Length - suffix + k));
        }
        return ops;
    }

    private const int MaxEditDistance = 2000;

    private static List<Op> Myers(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var result = new List<Op>();
        if (n == 0) { for (var j = 0; j < m; j++) result.Add(new Op('+', b[j], 0, j)); return result; }
        if (m == 0) { for (var i = 0; i < n; i++) result.Add(new Op('-', a[i], i, 0)); return result; }

        var max = n + m;
        var v = new int[2 * max + 2];
        // trace[d] stores only the diagonals reachable at step d (k in [-d, d]) to keep memory O(D^2).
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            if (d > MaxEditDistance) break;
            var snapshot = new int[2 * d + 3];
            for (var si = 0; si < snapshot.Length; si++)
            {
                var idx = max - d - 1 + si;
                snapshot[si] = idx >= 0 && idx < v.Length ? v[idx] : 0;
            }
            trace.Add(snapshot);
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || k != d && v[max + k - 1] < v[max + k + 1]) x = v[max + k + 1];
                else x = v[max + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[max + k] = x;
                if (x >= n && y >= m)
                {
                    Backtrack(trace, a, b, result);
                    return result;
                }
            }
        }
        // Too different: replace the whole region.
        for (var i = 0; i < n; i++) result.Add(new Op('-', a[i], i, 0));
        for (var j = 0; j < m; j++) result.Add(new Op('+', b[j], n, j));
        return result;
    }

    private static void Backtrack(List<int[]> trace, string[] a, string[] b, List<Op> result)
    {
        int x = a.Length, y = b.Length;
        var rev = new List<Op>();
        for (var d = trace.Count - 1; d >= 0 && (x > 0 || y > 0); d--)
        {
            var snap = trace[d];
            // snapshot index for diagonal k at step d: k + d + 1
            int V(int k) => snap[k + d + 1];
            var k = x - y;
            int prevK;
            if (k == -d || k != d && V(k - 1) < V(k + 1)) prevK = k + 1;
            else prevK = k - 1;
            var prevX = d == 0 ? 0 : V(prevK);
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY)
            {
                x--; y--;
                rev.Add(new Op(' ', a[x], x, y));
            }
            if (d > 0)
            {
                if (x == prevX) { y--; rev.Add(new Op('+', b[y], x, y)); }
                else { x--; rev.Add(new Op('-', a[x], x, y)); }
            }
        }
        rev.Reverse();
        result.AddRange(rev);
    }
}
