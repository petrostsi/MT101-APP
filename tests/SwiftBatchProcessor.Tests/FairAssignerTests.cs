using SwiftBatchApp.Core;

namespace SwiftBatchApp.Tests;

public class FairAssignerTests
{
    private static readonly string[] Users = { "a@x", "b@x", "c@x", "d@x" };

    [Fact]
    public void Lowest_load_wins()
    {
        var counts = new Dictionary<string, int> { ["a@x"] = 3, ["b@x"] = 1, ["c@x"] = 2, ["d@x"] = 5 };
        Assert.Equal("b@x", FairAssigner.PickFairest(Users, counts));
    }

    [Fact]
    public void Unknown_users_count_as_zero()
    {
        var counts = new Dictionary<string, int> { ["a@x"] = 1, ["b@x"] = 1, ["c@x"] = 1 };
        Assert.Equal("d@x", FairAssigner.PickFairest(Users, counts));
    }

    [Fact]
    public void Ties_are_broken_randomly_so_nobody_is_always_first()
    {
        var random = new Random(12345);
        var empty = new Dictionary<string, int>();
        var hits = Users.ToDictionary(u => u, _ => 0);
        for (int i = 0; i < 4000; i++) hits[FairAssigner.PickFairest(Users, empty, random)]++;
        Assert.All(hits.Values, n => Assert.InRange(n, 850, 1150));
    }

    [Fact]
    public void Distribution_stays_even_when_loads_are_updated()
    {
        var random = new Random(7);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int file = 0; file < 40; file++)
        {
            string u = FairAssigner.PickFairest(Users, counts, random);
            counts[u] = counts.GetValueOrDefault(u) + FairAssigner.LoadOf(1);
        }
        Assert.All(Users, u => Assert.Equal(10, counts[u]));
    }

    [Fact]
    public void Empty_file_still_counts_as_load() => Assert.Equal(1, FairAssigner.LoadOf(0));
}
