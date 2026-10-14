using Notch.Core.Agents;

namespace Notch.Core.Tests;

public class SkillLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notch-library-" + Guid.NewGuid().ToString("N"));

    public SkillLibraryTests() => Directory.CreateDirectory(_root);

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

    private string Claude(string scope)
    {
        string dir = Path.Combine(_root, scope, ".claude");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteSkill(string claudeDir, string folder, string body)
    {
        string dir = Path.Combine(claudeDir, "skills", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), body);
    }

    private static void WriteAgent(string claudeDir, string file, string body)
    {
        string dir = Path.Combine(claudeDir, "agents");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), body);
    }

    [Fact]
    public void Reads_skills_and_agents_with_their_front_matter()
    {
        string user = Claude("user");
        WriteSkill(user, "pdf", "---\nname: pdf\ndescription: Work with PDF files\n---\nbody");
        WriteAgent(user, "reviewer.md", "---\nname: reviewer\ndescription: Reviews code\n---\nbody");

        IReadOnlyList<LibraryItem> items = SkillLibrary.Discover([(user, "User")]);

        LibraryItem skill = Assert.Single(items, i => i.Kind == LibraryItemKind.Skill);
        Assert.Equal("pdf", skill.Name);
        Assert.Equal("Work with PDF files", skill.Description);
        Assert.Equal("User", skill.Scope);

        LibraryItem agent = Assert.Single(items, i => i.Kind == LibraryItemKind.Agent);
        Assert.Equal("reviewer", agent.Name);
        Assert.Equal("Reviews code", agent.Description);
    }

    [Fact]
    public void Falls_back_to_the_folder_or_file_name_when_front_matter_has_none()
    {
        string user = Claude("user");
        WriteSkill(user, "my-skill", "no front matter here");
        WriteAgent(user, "helper.md", "plain text");

        IReadOnlyList<LibraryItem> items = SkillLibrary.Discover([(user, "User")]);

        Assert.Contains(items, i => i is { Kind: LibraryItemKind.Skill, Name: "my-skill", Description: "" });
        Assert.Contains(items, i => i is { Kind: LibraryItemKind.Agent, Name: "helper", Description: "" });
    }

    [Fact]
    public void A_folder_without_a_SKILL_md_is_not_a_skill()
    {
        string user = Claude("user");
        Directory.CreateDirectory(Path.Combine(user, "skills", "empty"));

        Assert.Empty(SkillLibrary.Discover([(user, "User")]));
    }

    [Fact]
    public void A_later_root_shadows_an_earlier_one_with_the_same_name()
    {
        string user = Claude("user");
        string project = Claude("project");
        WriteSkill(user, "pdf", "---\nname: pdf\ndescription: user copy\n---");
        WriteSkill(project, "pdf", "---\nname: pdf\ndescription: project copy\n---");

        IReadOnlyList<LibraryItem> items = SkillLibrary.Discover([(user, "User"), (project, "proj")]);

        LibraryItem pdf = Assert.Single(items);
        Assert.Equal("project copy", pdf.Description);
        Assert.Equal("proj", pdf.Scope);
    }

    [Fact]
    public void A_missing_claude_folder_yields_nothing()
    {
        Assert.Empty(SkillLibrary.Discover([(Path.Combine(_root, "nope", ".claude"), "User")]));
    }

    [Fact]
    public void Results_are_sorted_by_kind_then_name()
    {
        string user = Claude("user");
        WriteSkill(user, "zebra", "---\nname: zebra\n---");
        WriteSkill(user, "alpha", "---\nname: alpha\n---");
        WriteAgent(user, "beta.md", "---\nname: beta\n---");

        string[] order = [.. SkillLibrary.Discover([(user, "User")]).Select(i => i.Name)];

        Assert.Equal(["alpha", "zebra", "beta"], order);
    }

    [Theory]
    [InlineData("---\ndescription: \"quoted value\"\n---", "quoted value")]
    [InlineData("---\ndescription: 'single quoted'\n---", "single quoted")]
    [InlineData("---\ndescription: >\n  folded over\n  two lines\n---", "folded over two lines")]
    [InlineData("---\ndescription: |\n  literal block\n---", "literal block")]
    public void Parses_description_values(string body, string expected)
    {
        (_, string? description) = SkillLibrary.ParseFrontMatter(body);
        Assert.Equal(expected, description);
    }

    [Fact]
    public void No_front_matter_block_parses_as_empty()
    {
        (string? name, string? description) = SkillLibrary.ParseFrontMatter("# Just a heading\nname: not in front matter");
        Assert.Null(name);
        Assert.Null(description);
    }
}
