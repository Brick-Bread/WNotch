using System.IO;
using System.Windows;
using Notch.App.Agents;
using Notch.Core.Agents;

namespace Notch.App.Shell;

// The skills-and-agents list down the right of the Terminal tab: what the coding agents would load.
public partial class NotchWindow
{
    private string _libraryFolder = "";

    /// <summary>Fills the lists once, then again whenever the Terminal tab opens or its folder changes.</summary>
    private void RefreshLibrary()
    {
        // The user's own .claude folder (honouring CLAUDE_CONFIG_DIR), then the session's folder,
        // which comes last so a project's own skills and agents shadow the user's by name.
        string userDir = Path.GetDirectoryName(AgentHookFiles.ClaudeSettingsPath) ?? "";
        var roots = new List<(string ClaudeDir, string Scope)> { (userDir, "User") };

        string folder = _terminalFolder;
        string folderDir = Path.Combine(folder, ".claude");
        if (!string.Equals(Path.GetFullPath(folderDir), Path.GetFullPath(userDir), StringComparison.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            roots.Add((folderDir, name.Length > 0 ? name : folder));
        }

        // Reading the files off disk is kept off the UI thread; the folder it ran for is checked
        // on the way back, so a quick folder change does not show a stale list.
        _libraryFolder = folder;
        Task.Run(() => SkillLibrary.Discover(roots)).ContinueWith(
            task => ShowLibrary(folder, task.Result),
            System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowLibrary(string folder, IReadOnlyList<LibraryItem> items)
    {
        if (folder != _libraryFolder)
        {
            return;
        }

        LibraryRow[] skills = [.. items.Where(i => i.Kind == LibraryItemKind.Skill).Select(i => new LibraryRow(i))];
        LibraryRow[] agents = [.. items.Where(i => i.Kind == LibraryItemKind.Agent).Select(i => new LibraryRow(i))];

        LibrarySkillList.ItemsSource = skills;
        LibraryAgentList.ItemsSource = agents;
        LibrarySkillsHeader.Visibility = skills.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        LibraryAgentsHeader.Visibility = agents.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        LibraryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One row of the skills or agents list.</summary>
    private sealed class LibraryRow(LibraryItem item)
    {
        public string Name { get; } = item.Name;

        public string Scope { get; } = item.Scope;

        /// <summary>The description, shown on hover; null leaves the row without a tooltip.</summary>
        public string? Tip { get; } = item.Description.Length > 0 ? item.Description : null;
    }
}
