using Notch.Core.Plugins;

namespace Notch.Core.Tests;

public class PluginPaletteBoardTests
{
    private static PluginPaletteEntry Entry(string id, string title = "Title") => new() { Id = id, Title = title, Invoke = () => { } };

    [Fact]
    public void Fixed_entries_keep_their_place_when_replaced_and_belong_to_their_plugin()
    {
        var board = new PluginPaletteBoard();
        board.Set("a", "A", Entry("one", "First"));
        board.Set("b", "B", Entry("one", "Other"));
        board.Set("a", "A", Entry("two"));
        board.Set("a", "A", Entry("one", "Renamed"));

        Assert.Equal(["Renamed", "Other", "Title"], board.Entries().Select(e => e.Entry.Title));

        board.RemoveAll("a");
        Assert.Equal("b", Assert.Single(board.Entries()).PluginId);
    }

    [Fact]
    public async Task Live_results_are_gathered_in_order_capped_and_a_failing_or_slow_provider_adds_nothing()
    {
        var board = new PluginPaletteBoard();
        var failures = new List<string>();
        board.SetProvider("a", "A", (query, _) => Task.FromResult<IReadOnlyList<PluginPaletteEntry>>([.. Enumerable.Range(0, 9).Select(i => Entry($"{i}", $"{query} {i}"))]));
        board.SetProvider("b", "B", (_, _) => throw new InvalidOperationException("boom"));
        board.SetProvider("c", "C", async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return [Entry("late")];
        });

        IReadOnlyList<PluginPaletteItem> results = await board.QueryAsync("2+2", CancellationToken.None, (plugin, _) => failures.Add(plugin));

        Assert.Equal(PluginPaletteBoard.MaxResults, results.Count);
        Assert.All(results, r => Assert.Equal("a", r.PluginId));
        Assert.Equal("2+2 0", results[0].Entry.Title);
        Assert.Equal(["b"], failures);
        Assert.Empty(await board.QueryAsync("  ", CancellationToken.None));
    }

    [Fact]
    public async Task A_query_whose_text_changed_is_cancelled()
    {
        var board = new PluginPaletteBoard();
        board.SetProvider("a", "A", async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return [Entry("x")];
        });
        using var cancel = new CancellationTokenSource();

        Task<IReadOnlyList<PluginPaletteItem>> asked = board.QueryAsync("x", cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked);
    }
}
