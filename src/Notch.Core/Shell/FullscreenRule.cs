namespace Notch.Core.Shell;

/// <summary>An application window on the notch's display, as far as hiding for fullscreen apps is concerned.</summary>
/// <param name="CoversDisplay">It fills the whole display and has no title bar: a game, a fullscreen video, an F11 browser.</param>
/// <param name="IsTopmost">It is an always-on-top window, which floats above whatever the user is really looking at.</param>
public readonly record struct StackedWindow(bool CoversDisplay, bool IsTopmost);

public static class FullscreenRule
{
    /// <summary>
    /// Whether a fullscreen app is what shows on the display, given its windows from the top of
    /// the stack down. Small always-on-top windows are looked past, so an overlay or a pinned
    /// tool above a game does not bring the notch back; an ordinary window on top means the
    /// user has put something over the fullscreen app, and the notch may show.
    /// </summary>
    public static bool IsCovered(IEnumerable<StackedWindow> topToBottom)
    {
        foreach (StackedWindow window in topToBottom)
        {
            if (window.CoversDisplay)
            {
                return true;
            }

            if (!window.IsTopmost)
            {
                return false;
            }
        }

        return false;
    }
}
