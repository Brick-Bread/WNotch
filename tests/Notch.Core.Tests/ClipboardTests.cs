using Notch.Core.Clipboard;

namespace Notch.Core.Tests;

public class ClipboardTests
{
    private static readonly DateTime T = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_newest_copy_is_first()
    {
        var history = new ClipboardHistory();
        history.AddText("one", T);
        history.AddText("two", T.AddSeconds(1));
        history.AddText("three", T.AddSeconds(2));

        Assert.Equal(["three", "two", "one"], history.Snapshot().Select(i => i.Text));
    }

    [Fact]
    public void Copying_the_same_thing_again_moves_it_up()
    {
        var history = new ClipboardHistory();
        history.AddText("a", T);
        history.AddText("b", T.AddSeconds(1));
        history.AddText("a", T.AddSeconds(2));

        Assert.Equal(["a", "b"], history.Snapshot().Select(i => i.Text));
    }

    [Fact]
    public void The_oldest_go_when_the_history_is_full_but_pins_stay()
    {
        var history = new ClipboardHistory(capacity: 3);
        ClipboardItem first = history.AddText("first", T)!;
        history.SetPinned(first.Id, true);
        for (int i = 0; i < 5; i++)
        {
            history.AddText("item " + i, T.AddSeconds(i + 1));
        }

        string[] texts = [.. history.Snapshot().Select(i => i.Text!)];
        Assert.Equal(["first", "item 4", "item 3", "item 2"], texts);
    }

    [Fact]
    public void Pinned_items_are_listed_first()
    {
        var history = new ClipboardHistory();
        ClipboardItem old = history.AddText("old", T)!;
        history.AddText("new", T.AddSeconds(5));
        history.SetPinned(old.Id, true);

        Assert.Equal(["old", "new"], history.Snapshot().Select(i => i.Text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n")]
    public void Empty_text_is_not_kept(string? text)
    {
        Assert.Null(new ClipboardHistory().AddText(text, T));
    }

    [Fact]
    public void Huge_copies_are_not_kept()
    {
        var history = new ClipboardHistory();
        Assert.Null(history.AddText(new string('x', ClipboardHistory.MaxTextLength + 1), T));
        Assert.Null(history.AddImage(new byte[ClipboardHistory.MaxImageBytes + 1], T));
        Assert.Empty(history.Snapshot());
    }

    [Fact]
    public void Pictures_and_files_are_kept_and_matched_by_content()
    {
        var history = new ClipboardHistory();
        history.AddImage([1, 2, 3], T);
        history.AddImage([1, 2, 3], T.AddSeconds(1));
        history.AddFiles([@"C:\a\one.txt", @"C:\a\two.txt"], T.AddSeconds(2));
        history.AddFiles([@"c:\A\ONE.txt", @"C:\a\two.txt"], T.AddSeconds(3));

        Assert.Equal(2, history.Snapshot().Count);
        Assert.Equal("one.txt, two.txt", history.Snapshot().First(i => i.Kind == ClipboardKind.Files).Preview);
        Assert.Null(history.AddFiles([], T));
    }

    [Fact]
    public void The_list_can_be_searched()
    {
        var history = new ClipboardHistory();
        history.AddText("hello world", T);
        history.AddText("goodbye", T.AddSeconds(1));
        history.AddFiles([@"C:\docs\World Cup.xlsx"], T.AddSeconds(2));

        Assert.Equal(2, history.Snapshot("WORLD").Count);
        Assert.Single(history.Snapshot("goodbye"));
        Assert.Empty(history.Snapshot("nothing here"));
    }

    [Fact]
    public void Previews_show_the_first_line()
    {
        var item = new ClipboardItem(1, ClipboardKind.Text, T, Text: "\n  first line\r\nsecond line");
        Assert.Equal("first line", item.Preview);
        Assert.EndsWith("…", new ClipboardItem(1, ClipboardKind.Text, T, Text: new string('x', 500)).Preview);
    }

    [Fact]
    public void Clearing_keeps_pins_unless_told_otherwise()
    {
        var history = new ClipboardHistory();
        ClipboardItem keep = history.AddText("keep", T)!;
        history.AddText("lose", T.AddSeconds(1));
        history.SetPinned(keep.Id, true);

        history.Clear();
        Assert.Equal(["keep"], history.Snapshot().Select(i => i.Text));

        history.Clear(includePinned: true);
        Assert.Empty(history.Snapshot());
    }

    [Fact]
    public void Changes_are_announced_once_each()
    {
        var history = new ClipboardHistory();
        int changes = 0;
        history.Changed += () => changes++;

        ClipboardItem item = history.AddText("a", T)!;
        history.SetPinned(item.Id, true);
        history.SetPinned(item.Id, true);
        history.Remove(item.Id);
        history.Remove(item.Id);

        Assert.Equal(3, changes);
    }

    [Fact]
    public void Marked_copies_are_skipped()
    {
        Assert.True(ClipboardPrivacy.ShouldSkip(hasExcludeFormat: true, null, null));
        Assert.True(ClipboardPrivacy.ShouldSkip(false, canIncludeInHistory: 0, null));
        Assert.False(ClipboardPrivacy.ShouldSkip(false, canIncludeInHistory: 1, "notepad"));
        Assert.False(ClipboardPrivacy.ShouldSkip(false, null, null));
    }

    [Theory]
    [InlineData("KeePassXC")]
    [InlineData("1Password")]
    [InlineData("Bitwarden")]
    public void Password_managers_are_skipped_even_when_they_do_not_mark_their_copies(string program)
    {
        Assert.True(ClipboardPrivacy.ShouldSkip(false, null, program));
    }

    [Fact]
    public void Only_pins_are_saved()
    {
        string file = Path.Combine(Path.GetTempPath(), "notch-pins-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var history = new ClipboardHistory();
            ClipboardItem pinned = history.AddText("pinned text", T)!;
            history.AddText("not pinned", T.AddSeconds(1));
            history.AddFiles([@"C:\x\y.txt"], T.AddSeconds(2));
            history.SetPinned(pinned.Id, true);

            ClipboardPins.Save(file, history.Snapshot());
            IReadOnlyList<ClipboardItem> loaded = ClipboardPins.Load(file);

            Assert.Equal("pinned text", Assert.Single(loaded).Text);

            var next = new ClipboardHistory();
            next.Restore(loaded);
            Assert.True(Assert.Single(next.Snapshot()).Pinned);

            ClipboardPins.Save(file, []);
            Assert.False(File.Exists(file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
