namespace Notch.Core.Automation;

/// <summary>
/// What to do when a second copy of Notch starts and hands its arguments to the one already running.
/// Starting Notch by hand should bring the notch up, but a copy the installer or an update starts
/// must not: it would pop the notch open and hold it there for no reason the user can see.
/// </summary>
public static class ForwardedLaunch
{
    /// <summary>Switches that mean "started by the installer or a restart", not "the user asked to see Notch".</summary>
    private static readonly string[] Quiet = ["--updated", "--wait-for="];

    /// <summary>Whether the running notch should open for a second launch with these arguments.</summary>
    public static bool ShouldReveal(IReadOnlyList<string> arguments) =>
        !arguments.Any(argument => Quiet.Any(flag => argument.StartsWith(flag, StringComparison.OrdinalIgnoreCase)));
}
