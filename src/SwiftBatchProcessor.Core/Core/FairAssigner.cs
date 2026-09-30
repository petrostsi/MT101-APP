namespace SwiftBatchApp.Core;

/// <summary>
/// Fair, random assignment: the candidate with the LOWEST payment count today wins; ties are broken
/// uniformly at random so no processor is systematically first ("user 1 must not be mistreated").
/// </summary>
public static class FairAssigner
{
    public static string PickFairest(IReadOnlyList<string> candidates, IReadOnlyDictionary<string, int> counts, Random? random = null)
    {
        if (candidates.Count == 0) throw new ArgumentException("No candidates.", nameof(candidates));
        int Load(string u) => counts.TryGetValue(u, out int n) ? n : 0;
        int min = candidates.Min(Load);
        var lowest = candidates.Where(u => Load(u) == min).ToList();
        return lowest[(random ?? Random.Shared).Next(lowest.Count)];
    }

    /// <summary>Load a file adds to its assignee: its payment count, at least 1.</summary>
    public static int LoadOf(int paymentCount) => Math.Max(1, paymentCount);
}
