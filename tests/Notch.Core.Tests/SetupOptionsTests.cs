using Notch.Setup;

namespace Notch.Core.Tests;

/// <summary>
/// What a setup file with no app attached does. The bug this guards against: an update download that was cut
/// short, run by the updater with /S /UPDATE, uninstalled Notch instead of installing it.
/// </summary>
public sealed class SetupOptionsTests
{
    [Theory]
    [InlineData("/uninstall")]
    [InlineData("/uninstall", "/S")]
    [InlineData("--uninstall", "/D=C:\\Notch")]
    public void The_uninstall_entry_uninstalls(params string[] args) =>
        Assert.True(Options.Parse(args).UninstallsWithoutPayload);

    [Fact]
    public void No_switches_uninstalls_as_an_older_uninstall_entry_asked() =>
        Assert.True(Options.Parse([]).UninstallsWithoutPayload);

    [Theory]
    [InlineData("/S", "/UPDATE")]
    [InlineData("/S")]
    [InlineData("--silent")]
    [InlineData("/PATH")]
    [InlineData("/D=C:\\Notch")]
    public void Install_switches_never_uninstall(params string[] args) =>
        Assert.False(Options.Parse(args).UninstallsWithoutPayload);
}
