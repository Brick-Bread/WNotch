using System.Windows.Automation;
using Notch.Core.Agents;
using Notch.Platform.Agents;

namespace Notch.App.Agents;

/// <summary>
/// Brings up the very Windows Terminal tab an agent runs in, not just the window: it reads the tab names
/// through UI Automation, as a screen reader would, and selects the one <see cref="TerminalTabs"/> picks.
/// Best effort: false when no tab could be told apart, and the caller raises the window as before.
/// </summary>
internal static class TerminalTabFocus
{
    /// <param name="terminalPid">The Windows Terminal process, which owns every window of it.</param>
    /// <param name="agentPid">The agent, whose console names its tab.</param>
    /// <param name="folderName">The name of the folder the agent works in, when known.</param>
    public static bool Focus(int terminalPid, int agentPid, string? folderName)
    {
        try
        {
            string? title = ConsoleTitle.Of(agentPid);
            foreach (nint window in WindowFocus.WindowsOf(terminalPid))
            {
                AutomationElement root = AutomationElement.FromHandle(window);
                AutomationElement[] tabs = [.. root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)).Cast<AutomationElement>()];
                int index = TerminalTabs.Pick([.. tabs.Select(t => t.Current.Name ?? "")], title, folderName);
                if (index < 0)
                {
                    continue;
                }

                if (tabs[index].TryGetCurrentPattern(SelectionItemPattern.Pattern, out object pattern))
                {
                    ((SelectionItemPattern)pattern).Select();
                }

                WindowFocus.Bring(window);
                return true;
            }
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // The window went away, or would not say; the caller raises it the usual way.
        }

        return false;
    }
}
