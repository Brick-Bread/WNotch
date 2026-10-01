namespace Notch.Core.Settings;

/// <summary>The shape the pill takes against the edge of the screen.</summary>
public enum NotchStyle
{
    /// <summary>Grows out of the screen edge like a camera notch: flat against it, rounded away from it.</summary>
    Notch,

    /// <summary>A pill rounded all the way around that floats a little clear of the edge.</summary>
    Island,
}

/// <summary>Where on the display the pill sits.</summary>
public enum NotchPosition
{
    /// <summary>Middle of the top edge; the panel opens downwards.</summary>
    TopCenter,

    /// <summary>In the taskbar's far left corner; the panel opens upwards.</summary>
    TaskbarLeft,
}
