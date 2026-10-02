using Notch.Core.Activities;
using Notch.Core.Shelf;

namespace Notch.Core.Tests;

public class ShelfTests
{
    [Fact]
    public void New_files_go_to_the_front_in_the_order_they_were_dropped()
    {
        var shelf = new FileShelf();

        shelf.Add([@"C:\a.txt"]);
        shelf.Add([@"C:\b.txt", @"C:\c.txt"]);

        Assert.Equal([@"C:\b.txt", @"C:\c.txt", @"C:\a.txt"], shelf.Snapshot());
    }

    [Fact]
    public void A_file_already_on_the_shelf_moves_to_the_front_instead_of_showing_twice()
    {
        var shelf = new FileShelf([@"C:\a.txt", @"C:\b.txt"]);

        shelf.Add([@"c:\B.TXT"]);

        Assert.Equal([@"c:\B.TXT", @"C:\a.txt"], shelf.Snapshot());
    }

    [Fact]
    public void A_full_shelf_drops_what_was_put_there_longest_ago()
    {
        var shelf = new FileShelf(Enumerable.Range(1, FileShelf.Capacity).Select(i => $@"C:\old{i}.txt"));

        shelf.Add([@"C:\new.txt"]);

        IReadOnlyList<string> paths = shelf.Snapshot();
        Assert.Equal(FileShelf.Capacity, paths.Count);
        Assert.Equal(@"C:\new.txt", paths[0]);
        Assert.DoesNotContain($@"C:\old{FileShelf.Capacity}.txt", paths);
    }

    [Fact]
    public void Blank_paths_and_repeats_in_one_drop_are_left_out()
    {
        var shelf = new FileShelf();

        shelf.Add(["", "  ", @"C:\a.txt", @"C:\A.txt"]);

        Assert.Equal([@"C:\a.txt"], shelf.Snapshot());
    }

    [Fact]
    public void Only_real_changes_are_reported()
    {
        var shelf = new FileShelf([@"C:\a.txt"]);
        int changes = 0;
        shelf.Changed += (_, _) => changes++;

        shelf.Add([]);
        shelf.Add([@"C:\a.txt"]);
        shelf.Prune(_ => true);
        Assert.False(shelf.Remove(@"C:\missing.txt"));
        Assert.Equal(0, changes);

        shelf.Add([@"C:\b.txt"]);
        Assert.True(shelf.Remove(@"c:\A.TXT"));
        shelf.Clear();
        shelf.Clear();
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Pruning_drops_files_that_are_gone()
    {
        var shelf = new FileShelf([@"C:\here.txt", @"C:\moved.txt", @"C:\also-here.txt"]);

        shelf.Prune(path => !path.Contains("moved"));

        Assert.Equal([@"C:\here.txt", @"C:\also-here.txt"], shelf.Snapshot());
    }

    [Fact]
    public void The_shelf_is_saved_and_loaded_in_order()
    {
        WithStore(store =>
        {
            Assert.Empty(store.Load());

            store.Save([@"C:\b.txt", @"C:\a.txt"]);

            Assert.Equal([@"C:\b.txt", @"C:\a.txt"], new FileShelf(store.Load()).Snapshot());
        });
    }

    [Fact]
    public void A_corrupt_shelf_file_yields_an_empty_shelf()
    {
        WithStore(store =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, "{ not a list");

            Assert.Empty(store.Load());
        });
    }

    [Theory]
    [InlineData(1, "1 item")]
    [InlineData(3, "3 items")]
    public void A_drop_on_the_closed_pill_is_confirmed_with_a_count(int count, string expected)
    {
        Activity notice = ShelfActivities.Added(count);

        Assert.Equal(ActivityTier.Transient, notice.Tier);
        Assert.Equal(expected, notice.Detail);
    }

    private static void WithStore(Action<ShelfStore> test)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"notch-test-{Guid.NewGuid():N}");
        try
        {
            test(new ShelfStore(Path.Combine(folder, "shelf.json")));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
