using Notch.Core.Plugins;

namespace Notch.Core.Tests;

public class PluginRegistryTests
{
    private const string Sample = """
        {
          "version": 1,
          "plugins": [
            { "id": "acme.build-status", "name": "Build Status", "repository": "acme/build-status",
              "author": "Acme", "description": "Shows CI.", "tags": ["dev", "ci"],
              "permissions": ["Network", "network", "  shell "], "apiVersion": 6, "verified": true },
            { "id": "Bad Id", "name": "Nope", "repository": "acme/nope" },
            { "id": "acme.no-repo", "name": "No repo" },
            { "id": "acme.build-status", "name": "Duplicate", "repository": "evil/dup" },
            { "id": "acme.link", "name": "By link", "repository": "https://github.com/acme/link-plugin" },
            { "name": "No id", "repository": "acme/x" }
          ]
        }
        """;

    [Fact]
    public void Usable_entries_are_kept_and_the_rest_skipped()
    {
        PluginRegistry registry = PluginRegistry.Parse(Sample);

        Assert.Equal(["acme.build-status", "acme.link"], registry.Entries.Select(e => e.Id));
        RegistryEntry first = registry.Find("acme.build-status")!;
        Assert.Equal("acme/build-status", first.Repository);
        Assert.Equal(["network", "shell"], first.Permissions);
        Assert.Equal(["dev", "ci"], first.Tags);
        Assert.True(first.Verified);
        Assert.Equal(6, first.ApiVersion);
        Assert.Equal("acme/link-plugin", registry.Find("acme.link")!.Repository);
    }

    [Fact]
    public void The_first_entry_wins_when_an_id_is_repeated()
    {
        Assert.Equal("acme/build-status", PluginRegistry.Parse(Sample).Find("acme.build-status")!.Repository);
    }

    [Fact]
    public void Unknown_ids_are_not_found()
    {
        PluginRegistry registry = PluginRegistry.Parse(Sample);
        Assert.Null(registry.Find("evil.thing"));
        Assert.Null(registry.Find("ACME.BUILD-STATUS"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Something_that_is_not_a_registry_is_an_error(string text)
    {
        Assert.Throws<PluginLoadException>(() => PluginRegistry.Parse(text));
    }

    [Fact]
    public void A_manifest_lists_what_the_plugin_says_it_uses()
    {
        PluginManifest manifest = PluginManifest.Parse("""
            { "id": "a.b", "name": "B", "assembly": "B.dll", "apiVersion": 1, "permissions": ["Network", "clipboard", "network", ""] }
            """);
        Assert.Equal(["network", "clipboard"], manifest.Permissions);
        Assert.Equal("Connects to the internet", PluginManifest.DescribePermission("network"));
        Assert.Equal("quantum", PluginManifest.DescribePermission("quantum"));
    }

    [Fact]
    public void A_manifest_without_permissions_declares_none()
    {
        Assert.Empty(PluginManifest.Parse("""{ "id": "a.b", "name": "B", "assembly": "B.dll", "apiVersion": 1 }""").Permissions);
    }
}
