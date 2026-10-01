using Notch.Core.Plugins;

namespace Notch.Core.Tests;

public class PluginPageBoardTests
{
    private static PluginPage Page(string id = "p", string title = "Page") => new() { Id = id, Title = title };

    [Fact]
    public void Console_lines_are_kept_in_order_and_survive_replacing_the_page()
    {
        var board = new PluginPageBoard();
        board.Set("a", Page());
        board.Append("a", "p", "one");
        board.Append("a", "p", "two");
        board.Set("a", Page(title: "Renamed"));

        Assert.Equal(["one", "two"], board.ConsoleOf("a", "p"));
        Assert.Equal("Renamed", Assert.Single(board.Snapshot()).Page.Title);
    }

    [Fact]
    public void Only_the_newest_lines_are_kept()
    {
        var board = new PluginPageBoard();
        board.Set("a", Page());
        for (int i = 0; i < PluginPageBoard.MaxConsoleLines + 10; i++)
        {
            board.Append("a", "p", i.ToString());
        }

        IReadOnlyList<string> lines = board.ConsoleOf("a", "p");
        Assert.Equal(PluginPageBoard.MaxConsoleLines, lines.Count);
        Assert.Equal("10", lines[0]);
    }

    [Fact]
    public void Appending_to_a_missing_page_is_ignored()
    {
        var board = new PluginPageBoard();
        var changes = new List<PluginPageChange>();
        board.Changed += (_, e) => changes.Add(e.Change);

        board.Append("a", "nope", "line");
        board.RequestOpen("a", "nope");

        Assert.Empty(changes);
    }

    [Fact]
    public void Pages_stay_in_the_order_they_were_added_and_belong_to_their_plugin()
    {
        var board = new PluginPageBoard();
        board.Set("a", Page("one"));
        board.Set("b", Page("one"));
        board.Set("a", Page("two"));
        board.Set("a", Page("one", "Again"));

        Assert.Equal([("a", "one"), ("b", "one"), ("a", "two")], board.Snapshot().Select(e => (e.PluginId, e.Page.Id)));

        board.RemoveAll("a");
        Assert.Equal([("b", "one")], board.Snapshot().Select(e => (e.PluginId, e.Page.Id)));
    }

    [Fact]
    public void At_most_eight_stats_are_kept()
    {
        var board = new PluginPageBoard();
        board.Set("a", Page() with { Stats = [.. Enumerable.Range(0, 12).Select(i => new PluginStat { Label = i.ToString() })] });

        Assert.Equal(8, board.Snapshot()[0].Page.Stats.Count);
    }

    [Fact]
    public void Changes_are_reported_with_the_page_they_concern()
    {
        var board = new PluginPageBoard();
        var seen = new List<(PluginPageChange, string, string?)>();
        board.Changed += (_, e) => seen.Add((e.Change, e.PageId, e.Line));

        board.Set("a", Page());
        board.Append("a", "p", "hi");
        board.ClearConsole("a", "p");
        board.RequestOpen("a", "p");
        board.Remove("a", "p");

        Assert.Equal(
            [
                (PluginPageChange.Pages, "p", null),
                (PluginPageChange.Console, "p", "hi"),
                (PluginPageChange.ConsoleCleared, "p", null),
                (PluginPageChange.OpenRequested, "p", null),
                (PluginPageChange.Pages, "p", null),
            ],
            seen);
    }
}
