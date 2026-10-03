namespace Codale.Search.Tests;

public sealed class RipgrepSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-rg-").FullName;

    [Fact(Timeout = 30_000)]
    public async Task Stopping_at_max_results_does_not_wait_for_ripgrep_to_finish()
    {
        // Far more output than a pipe buffer holds: if the search stops reading and then
        // waits for ripgrep to exit, ripgrep blocks on a full stdout and this never returns.
        for (var file = 0; file < 200; file++)
        {
            File.WriteAllLines(
                Path.Combine(_root, $"f{file}.txt"),
                Enumerable.Range(0, 50).Select(i => $"snake head line {i} {new string('x', 200)}"));
        }

        var hits = new List<SearchHit>();
        await foreach (var hit in new RipgrepSearch(_root).SearchAsync(
                           new SearchQuery { Text = "snake|head", IsRegex = true, MaxResults = 30 }))
        {
            hits.Add(hit);
        }

        Assert.Equal(30, hits.Count);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
