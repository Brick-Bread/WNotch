using System.Windows;
using Notch.Core.Plugins;
using Notch.Core.Plugins.Checks;

namespace Notch.App.Plugins;

/// <summary>
/// The question asked before a plugin's code runs for the first time, or after its files changed.
/// Approving is the user's own decision, so nothing but this question (or the install window that
/// says the same things) ever records it.
/// </summary>
public static class PluginReviewPrompt
{
    /// <param name="lead">How the question starts, e.g. "Switch on".</param>
    /// <returns>True when the user approved the plugin as it is now. Plugins that must not run are refused without asking.</returns>
    public static bool Ask(Window? owner, PluginReview review, string lead)
    {
        string name = review.Info.Name;
        if (review.Revoked is { } revoked)
        {
            Show(owner, $"\"{name}\" cannot be switched on: the plugin list withdrew it" + (revoked.Reason.Length > 0 ? $" ({revoked.Reason})." : "."), MessageBoxImage.Warning);
            return false;
        }

        if (review.Scan.Blocked)
        {
            Show(owner, $"\"{name}\" cannot be switched on. {review.Scan.BlockReason}", MessageBoxImage.Warning);
            return false;
        }

        string question = $"{lead} this plugin?{Environment.NewLine}{Environment.NewLine}{review.Describe()}{Environment.NewLine}{Environment.NewLine}"
            + "Plugins run as you, with access to everything you can reach. Only switch on plugins you trust.";
        return (owner is null
            ? MessageBox.Show(question, "Notch", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(owner, question, "Notch", MessageBoxButton.YesNo, MessageBoxImage.Warning)) == MessageBoxResult.Yes;
    }

    private static void Show(Window? owner, string text, MessageBoxImage image)
    {
        if (owner is null)
        {
            MessageBox.Show(text, "Notch", MessageBoxButton.OK, image);
        }
        else
        {
            MessageBox.Show(owner, text, "Notch", MessageBoxButton.OK, image);
        }
    }
}
