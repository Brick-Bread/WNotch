using Notch.Core.Activities;

namespace Notch.Core.Plugins;

// The building blocks of a page body. A page shows its Blocks in a scrolling column instead of
// the console (see PluginPage.Blocks). Blocks are plain data; Notch draws them in its own style
// and keeps what the user is typing when the plugin sends an updated page. API version 5.

/// <summary>Something a page can show or ask for. See the types below.</summary>
public abstract record PluginBlock;

public enum PluginTextStyle
{
    Body,
    Heading,

    /// <summary>Smaller and dimmer.</summary>
    Muted,

    /// <summary>Monospace, e.g. for log output or a path.</summary>
    Code,
}

/// <summary>A paragraph. Wraps; line breaks are kept.</summary>
public sealed record PluginText : PluginBlock
{
    public required string Text { get; init; }

    public PluginTextStyle Style { get; init; }

    public GlowColor? Color { get; init; }
}

/// <summary>A caption on the left and a value on the right, e.g. "Version" and "1.2.0".</summary>
public sealed record PluginValueRow : PluginBlock
{
    public required string Label { get; init; }

    public string? Value { get; init; }

    public GlowColor? Color { get; init; }
}

/// <summary>A caption, a value and a bar.</summary>
public sealed record PluginProgress : PluginBlock
{
    public required string Label { get; init; }

    public string? Value { get; init; }

    /// <summary>0..1.</summary>
    public double Progress { get; init; }

    public GlowColor? Color { get; init; }
}

/// <summary>A grid of text with a header row. Up to eight columns and 200 rows are drawn.</summary>
public sealed record PluginTable : PluginBlock
{
    public required IReadOnlyList<string> Columns { get; init; }

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = [];
}

/// <summary>A line chart, oldest value on the left. Up to 240 values are drawn.</summary>
public sealed record PluginChart : PluginBlock
{
    public string? Label { get; init; }

    public required IReadOnlyList<double> Values { get; init; }

    /// <summary>The value that reaches the top. Defaults to the largest value shown, or 1 when that is smaller.</summary>
    public double? Max { get; init; }

    public GlowColor? Color { get; init; }

    /// <summary>In device-independent pixels, 40 to 240.</summary>
    public double Height { get; init; } = 72;
}

/// <summary>A row of buttons inside the body.</summary>
public sealed record PluginButtons : PluginBlock
{
    public required IReadOnlyList<PluginAction> Actions { get; init; }
}

/// <summary>A labelled switch.</summary>
public sealed record PluginToggle : PluginBlock
{
    public required string Label { get; init; }

    public string? Detail { get; init; }

    public bool Value { get; init; }

    /// <summary>Called on a background thread with the new value when the user flips it.</summary>
    public Action<bool>? Changed { get; init; }
}

/// <summary>A labelled slider with its current value shown beside it.</summary>
public sealed record PluginSlider : PluginBlock
{
    public required string Label { get; init; }

    public double Min { get; init; }

    public double Max { get; init; } = 100;

    public double Value { get; init; }

    /// <summary>How far one step moves the value; 0 for a continuous slider.</summary>
    public double Step { get; init; } = 1;

    /// <summary>Text after the number, e.g. "%" or " s".</summary>
    public string? Unit { get; init; }

    /// <summary>Called on a background thread with the new value, once the user lets go of the handle.</summary>
    public Action<double>? Changed { get; init; }
}

/// <summary>A labelled drop-down list.</summary>
public sealed record PluginSelect : PluginBlock
{
    public required string Label { get; init; }

    public required IReadOnlyList<string> Options { get; init; }

    public string? Selected { get; init; }

    /// <summary>Called on a background thread with the option the user picked.</summary>
    public Action<string>? Changed { get; init; }
}

/// <summary>A labelled text box with a button that submits it.</summary>
public sealed record PluginTextField : PluginBlock
{
    public required string Label { get; init; }

    /// <summary>
    /// The text to show. While the user is typing in the box, Notch leaves their text alone when
    /// an updated page arrives.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>Greyed text in the empty box.</summary>
    public string? Hint { get; init; }

    /// <summary>Shows dots instead of the text.</summary>
    public bool Secret { get; init; }

    /// <summary>The button's text. Defaults to "Save".</summary>
    public string? SubmitLabel { get; init; }

    /// <summary>Called on a background thread with the text when the user presses the button or Enter.</summary>
    public Action<string>? Submitted { get; init; }
}

/// <summary>A picture. PNG, JPEG or any other format WPF reads.</summary>
public sealed record PluginImage : PluginBlock
{
    public required byte[] Data { get; init; }

    /// <summary>In device-independent pixels, 20 to 400. The width follows the picture.</summary>
    public double Height { get; init; } = 120;
}

/// <summary>A thin line between groups of blocks.</summary>
public sealed record PluginSeparator : PluginBlock;
