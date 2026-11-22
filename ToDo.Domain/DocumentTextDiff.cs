namespace ToDo.Domain;

public sealed record DocumentTextChange(bool Added, int Line, string Text);
public sealed record DocumentTextComparison(IReadOnlyList<DocumentTextChange> Changes, bool IsRangeComparison, bool IsTruncated);

public static class DocumentTextDiff
{
    public static DocumentTextComparison Compare(string before, string after)
    {
        var left = before.Replace("\r\n", "\n").Split('\n');
        var right = after.Replace("\r\n", "\n").Split('\n');
        var start = 0;
        while (start < left.Length && start < right.Length && left[start] == right[start]) start++;
        var leftEnd = left.Length; var rightEnd = right.Length;
        while (leftEnd > start && rightEnd > start && left[leftEnd - 1] == right[rightEnd - 1]) { leftEnd--; rightEnd--; }
        var changes = new List<DocumentTextChange>();
        var rangeOnly = (long)(leftEnd - start) * (rightEnd - start) > 250000;
        var omitted = false;
        void Add(bool added, int line, string text)
        {
            if (changes.Count < 200) changes.Add(new(added, line, text)); else omitted = true;
        }
        if (rangeOnly)
        {
            for (var p = start; p < leftEnd; p++) Add(false, p + 1, left[p]);
            for (var p = start; p < rightEnd; p++) Add(true, p + 1, right[p]);
        }
        else
        {
            var m = leftEnd - start; var n = rightEnd - start;
            // Empty sides avoid allocating a huge (m+1) x 1 matrix for very long plain text.
            if (m == 0) { for (var p = start; p < rightEnd; p++) Add(true, p + 1, right[p]); }
            else if (n == 0) { for (var p = start; p < leftEnd; p++) Add(false, p + 1, left[p]); }
            else
            {
                var lengths = new int[m + 1, n + 1];
                for (var i = m - 1; i >= 0; i--)
                    for (var j = n - 1; j >= 0; j--)
                        lengths[i, j] = left[start + i] == right[start + j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
                var x = 0; var y = 0;
                while (x < m || y < n)
                {
                    if (x < m && y < n && left[start + x] == right[start + y]) { x++; y++; }
                    else if (x < m && (y == n || lengths[x + 1, y] >= lengths[x, y + 1])) { Add(false, start + x + 1, left[start + x]); x++; }
                    else { Add(true, start + y + 1, right[start + y]); y++; }
                }
            }
        }
        return new(changes, rangeOnly, omitted);
    }
}
