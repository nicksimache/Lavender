namespace Lavender.Application.Agent;

/// <summary>Non-overlapping retained ranges, sorted by start. Lookup is O(log n + overlaps).</summary>
internal sealed class SeenFileRanges(string hash)
{
    internal readonly record struct Range(int Start, int End, string CallId);
    public string Hash { get; } = hash;
    public List<Range> Ranges { get; } = [];

    public List<Range> Overlaps(int start, int end)
    {
        int low = 0, high = Ranges.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (Ranges[mid].End < start) low = mid + 1; else high = mid;
        }
        List<Range> result = [];
        for (int i = low; i < Ranges.Count && Ranges[i].Start <= end; i++) result.Add(Ranges[i]);
        return result;
    }

    public void Add(IEnumerable<Range> ranges)
    {
        Ranges.AddRange(ranges);
        Ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = Ranges.Count - 1; i > 0; i--)
        {
            var a = Ranges[i - 1]; var b = Ranges[i];
            if (a.CallId == b.CallId && a.End + 1 == b.Start)
            { Ranges[i - 1] = a with { End = b.End }; Ranges.RemoveAt(i); }
        }
    }
}
