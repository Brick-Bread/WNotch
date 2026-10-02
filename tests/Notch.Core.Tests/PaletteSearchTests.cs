using Notch.Core.Shell;

namespace Notch.Core.Tests;

public class PaletteSearchTests
{
    private static readonly string[] Items =
    [
        "Open Home",
        "Open Terminal",
        "Start a 25 minute timer",
        "Pause the timer",
        "Open settings",
        "New Claude session",
        "Switch on Break reminder",
    ];

    private static IReadOnlyList<string> Rank(string? query, int max = 8) =>
        PaletteSearch.Rank(Items, query, item => item, max: max);

    [Fact]
    public void With_nothing_typed_the_first_entries_are_listed_in_order()
    {
        Assert.Equal(Items, Rank(""));
        Assert.Equal(Items.Take(2), Rank(null, max: 2));
        Assert.Equal(Items.Take(3), Rank("   ", max: 3));
    }

    [Fact]
    public void A_word_at_the_start_beats_one_inside()
    {
        Assert.Equal("Open Home", Rank("open")[0]);
        Assert.Equal(["Pause the timer"], Rank("pau"));

        // "timer" starts a word in both, but "Pause the timer" has it later; both match.
        Assert.Equal(["Start a 25 minute timer", "Pause the timer"], Rank("timer"));
    }

    [Fact]
    public void Every_word_must_match()
    {
        Assert.Equal(["Open Terminal"], Rank("open term"));
        Assert.Empty(Rank("open banana"));
    }

    [Fact]
    public void A_title_that_starts_with_the_word_beats_one_that_only_contains_it()
    {
        Assert.Equal(["Pause", "Unpause"], PaletteSearch.Rank(["Unpause", "Pause"], "pause", item => item));
    }

    [Fact]
    public void Letters_in_order_still_find_it()
    {
        Assert.Contains("Open settings", Rank("opst"));
        Assert.DoesNotContain("Open Home", Rank("opst"));
    }

    [Fact]
    public void Case_does_not_matter()
    {
        Assert.Equal(Rank("claude"), Rank("CLAUDE"));
    }

    [Fact]
    public void Keywords_find_an_entry_the_title_does_not()
    {
        string[] items = ["Open Settings", "Open Home"];
        IReadOnlyList<string> ranked = PaletteSearch.Rank(items, "preferences", i => i, i => i == "Open Settings" ? "preferences options" : null);
        Assert.Equal(["Open Settings"], ranked);
    }

    [Fact]
    public void The_list_is_cut_to_the_maximum()
    {
        Assert.Equal(3, Rank("o", max: 3).Count);
    }

    [Fact]
    public void Equal_matches_keep_their_given_order()
    {
        Assert.Equal(["Open B", "Open A", "Open C"], PaletteSearch.Rank(["Open B", "Open A", "Open C"], "open", i => i));
    }
}
