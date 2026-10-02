using Notch.Setup;

namespace Notch.Core.Tests;

/// <summary>
/// The installer's folder swap. An update that cannot replace a file must leave the installed
/// version exactly as it was: the bug this guards against deleted the old version and installed nothing.
/// </summary>
public sealed class FolderSwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notch-swap-" + Guid.NewGuid().ToString("N"));

    public FolderSwapTests() => Directory.CreateDirectory(_root);

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

    private string Make(string name, params (string Path, string Content)[] files)
    {
        string folder = Path.Combine(_root, name);
        foreach ((string path, string content) in files)
        {
            string full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Read(string folder, string path) => File.ReadAllText(Path.Combine(folder, path));

    private static string[] Files(string folder) =>
        [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)).Order()];

    [Fact]
    public void The_new_version_replaces_the_old_one_and_files_it_no_longer_ships_are_gone()
    {
        string installed = Make("Notch", ("Notch.exe", "old"), ("old-only.dll", "x"), ("Assets/a.html", "old page"));
        string staging = Make("Notch.new-1", ("Notch.exe", "new"), ("new-only.dll", "y"), ("Assets/a.html", "new page"), ("Assets/b.js", "z"));

        FolderSwap.Replace(installed, staging, FolderSwap.BackupName(installed, "1"));

        string[] expected = [Path.Combine("Assets", "a.html"), Path.Combine("Assets", "b.js"), "new-only.dll", "Notch.exe"];
        Assert.Equal(expected.Order(), Files(installed));
        Assert.Equal("new", Read(installed, "Notch.exe"));
        Assert.Equal("new page", Read(installed, Path.Combine("Assets", "a.html")));
    }

    [Fact]
    public void Nothing_is_left_beside_the_install_folder_afterwards()
    {
        string installed = Make("Notch", ("Notch.exe", "old"));
        string staging = Make("Notch.new-1", ("Notch.exe", "new"));
        string backup = FolderSwap.BackupName(installed, "1");

        FolderSwap.Replace(installed, staging, backup);

        Assert.False(Directory.Exists(staging));
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void A_first_install_just_moves_the_files_in()
    {
        string installed = Path.Combine(_root, "Notch");
        string staging = Make("Notch.new-1", ("Notch.exe", "new"), ("Assets/x", "1"));

        FolderSwap.Replace(installed, staging, FolderSwap.BackupName(installed, "1"));

        Assert.Equal("new", Read(installed, "Notch.exe"));
        Assert.Equal(2, Files(installed).Length);
    }

    [Fact]
    public void A_file_that_is_in_use_leaves_the_old_version_exactly_as_it_was()
    {
        string installed = Make("Notch", ("Notch.exe", "old exe"), ("a.dll", "old a"), ("Assets/index.html", "old page"), ("z.dll", "old z"));
        string staging = Make("Notch.new-1", ("Notch.exe", "new exe"), ("a.dll", "new a"), ("Assets/index.html", "new page"));
        string[] before = Files(installed);

        // A program that holds a file open and does not let go: the case that wiped the install.
        using FileStream lockedFile = File.Open(Path.Combine(installed, "Assets", "index.html"), FileMode.Open, FileAccess.Read, FileShare.None);

        IOException error = Assert.Throws<IOException>(() =>
            FolderSwap.Replace(installed, staging, FolderSwap.BackupName(installed, "1"), attempts: 2, pauseMilliseconds: 5));

        Assert.Contains("left as it was", error.Message);
        Assert.False(Directory.Exists(FolderSwap.BackupName(installed, "1")), "an empty backup skeleton should not be left behind");
        Assert.Contains("index.html", error.Message);
        Assert.Equal(before, Files(installed));
        Assert.Equal("old exe", Read(installed, "Notch.exe"));
        Assert.Equal("old a", Read(installed, "a.dll"));
        Assert.Equal("old z", Read(installed, "z.dll"));
    }

    [Fact]
    public void A_file_that_is_freed_while_the_swap_retries_does_not_fail_it()
    {
        string installed = Make("Notch", ("Notch.exe", "old"), ("hook.exe", "old hook"));
        string staging = Make("Notch.new-1", ("Notch.exe", "new"), ("hook.exe", "new hook"));
        FileStream? held = File.Open(Path.Combine(installed, "hook.exe"), FileMode.Open, FileAccess.Read, FileShare.None);
        var closed = new List<string>();

        // The retry hook is where setup closes the programs that hold files; here it lets go.
        FolderSwap.Replace(
            installed,
            staging,
            FolderSwap.BackupName(installed, "1"),
            retry: file =>
            {
                closed.Add(Path.GetFileName(file));
                held?.Dispose();
                held = null;
            },
            attempts: 5,
            pauseMilliseconds: 5);

        Assert.Equal(["hook.exe"], closed);
        Assert.Equal("new hook", Read(installed, "hook.exe"));
        Assert.Equal("new", Read(installed, "Notch.exe"));
    }

    [Fact]
    public void A_running_program_can_still_be_swapped_when_it_allows_renaming()
    {
        // Windows lets a running executable be renamed (it is opened with delete sharing).
        string installed = Make("Notch", ("Notch.exe", "old"));
        string staging = Make("Notch.new-1", ("Notch.exe", "new"));
        using FileStream running = File.Open(Path.Combine(installed, "Notch.exe"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        FolderSwap.Replace(installed, staging, FolderSwap.BackupName(installed, "1"));

        Assert.Equal("new", Read(installed, "Notch.exe"));
    }

    [Fact]
    public void Leftovers_of_an_interrupted_swap_are_cleared_but_nothing_else_is()
    {
        string installed = Make("Notch", ("Notch.exe", "keep"));
        string newer = Make("Notch.new-abc", ("x", "1"));
        string older = Make("Notch.old-def", ("x", "1"));
        string other = Make("Notch.backup", ("x", "1"));
        string sibling = Make("Notch2", ("x", "1"));

        FolderSwap.CleanLeftovers(installed);

        Assert.False(Directory.Exists(newer));
        Assert.False(Directory.Exists(older));
        Assert.True(Directory.Exists(other));
        Assert.True(Directory.Exists(sibling));
        Assert.Equal("keep", Read(installed, "Notch.exe"));
    }

    [Fact]
    public void Cleaning_up_a_folder_with_no_neighbours_does_not_fail()
    {
        FolderSwap.CleanLeftovers(Path.Combine(_root, "never-existed", "Notch"));
    }
}
