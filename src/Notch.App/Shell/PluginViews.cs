using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Notch.App.Shell;

/// <summary>Elements plugins draw themselves, for a widget on Home or a page of their own.</summary>
internal static class PluginViews
{
    /// <summary>The plugin's own element. Plugin code, so it may throw or hand back something that is not an element.</summary>
    public static UIElement Create(Func<object> view)
    {
        try
        {
            if (view() is UIElement element)
            {
                // An element can only be in one place; one the plugin reuses comes out of where it was.
                (VisualTreeHelper.GetParent(element) as Decorator)?.Child = null;
                return element;
            }

            return Problem("The plugin's view is not a WPF element.");
        }
        catch (Exception)
        {
            return Problem("The plugin's view could not be drawn.");
        }
    }

    /// <summary>The text box a click landed in, if any, so the notch can take the keyboard for it.</summary>
    public static TextBox? FindTextBox(DependencyObject? from)
    {
        for (DependencyObject? d = from; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is TextBox box)
            {
                return box;
            }
        }

        return null;
    }

    private static TextBlock Problem(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        return block;
    }
}
