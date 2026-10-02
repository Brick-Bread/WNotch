using Notch.Core.Activities;

namespace Notch.Core.Automation;

/// <summary>
/// Something an outside caller may ask the notch to do: a <c>notch://</c> link, the command line
/// tool and the webhook all end up as one of these. Only the commands listed here exist, so what
/// a link can do is limited to what is safe to do on a stranger's say-so.
/// </summary>
public abstract record NotchCommand;

/// <summary>Asks to install a plugin from the registry. The user still has to confirm.</summary>
public sealed record InstallPluginCommand(string PluginId) : NotchCommand;

/// <summary>Switches an installed plugin on or off.</summary>
public sealed record SetPluginEnabledCommand(string PluginId, bool Enabled) : NotchCommand;

public sealed record StartTimerCommand(TimeSpan Duration) : NotchCommand;

public enum TimerAction
{
    Stop,
    Pause,
    Resume,
}

public sealed record TimerActionCommand(TimerAction Action) : NotchCommand;

/// <summary>Opens the notch on a tab: <c>home</c>, <c>terminal</c>, <c>stats</c>, <c>shelf</c>, <c>plugins</c> or <c>clipboard</c>.</summary>
public sealed record OpenTabCommand(string Tab) : NotchCommand;

/// <summary>A short notice in the pill.</summary>
public sealed record ToastCommand(string Title, string? Detail, string? Glyph, GlowColor? Color, TimeSpan? Lifetime) : NotchCommand;

public sealed record OpenPaletteCommand : NotchCommand;

public sealed record OpenSettingsCommand : NotchCommand;
