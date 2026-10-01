using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Notch.Core.Activities;
using Notch.App.Terminal;
using Notch.Core.Media;
using Notch.Core.Plugins;
using Notch.Core.Settings;
using Notch.Core.Shell;
using Notch.Core.Widgets;
using Notch.Platform.Display;

namespace Notch.App.Shell;

public partial class NotchWindow : Window
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;

    private static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(350);
    private static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(160);

    // A game takes the foreground a moment before its window fills the screen.
    private static readonly TimeSpan FullscreenRecheckDelay = TimeSpan.FromMilliseconds(400);

    // How far outside the island, in DIPs, the pointer still counts as on it.
    private const double HoverMargin = 2;

    // In DIPs. Large fits the biggest expanded tab; small fits the pill plus room for its glow.
    // Both are the same width: a resize that moved the left edge would show the previous frame
    // in the wrong place until the next one is drawn, leaving nothing under the pointer.
    private static readonly Size LargeWindowSize = new(980, 600);
    private static readonly Size SmallWindowSize = new(980, 96);

    private readonly ActivityManager _activities;
    private readonly IMediaService _media;
    private readonly TerminalController _terminal;
    private readonly PluginCardBoard _pluginCards;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly NotchAnimator _animator;
    private readonly GlowController _glow;
    private readonly RectangleGeometry _islandClip = new();
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly DispatcherTimer _pointerWatch = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _housekeepingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _fullscreenRecheck = new() { Interval = FullscreenRecheckDelay };
    private ForegroundWatcher? _foregroundWatcher;

    private nint _hwnd;
    private DisplayInfo? _display;
    private bool _expanded;
    private bool _hoverWantsExpanded;
    private bool _pinnedOpen;
    private int? _displayOverride;
    private byte[]? _compactArtBytes;
    private GlowColor? _artAccent;
    private bool _largeWindow;
    private bool _hiddenForFullscreen;

    internal NotchWindow(
        ActivityManager activities,
        IMediaService media,
        TerminalController terminal,
        PluginCardBoard pluginCards,
        SettingsStore settingsStore,
        AppSettings settings)
    {
        InitializeComponent();

        _activities = activities;
        _activities.Changed += OnActivitiesChanged;
        _media = media;
        _terminal = terminal;
        _pluginCards = pluginCards;
        _settingsStore = settingsStore;
        _settings = settings;
        _glow = new GlowController(GlowLayer, GlowCore) { Gain = GlowOutput.Gain(settings.GlowIntensity) };
        InitializeMedia();
        InitializeTerminal();
        InitializeWidgets();
        InitializePlugins();

        Shape idle = ShapeFor(NotchMode.Idle);
        _animator = new NotchAnimator(idle.Width, idle.Height, idle.Radius);
        _animator.Frame += ApplyShape;
        _animator.Settled += ShrinkWindowWhenClear;
        Island.Clip = _islandClip;
        ApplyShape();

        Island.MouseEnter += (_, _) =>
        {
            HoverTrace.Write("enter");

            // Entering also cancels a pending close, which matters even when hover-to-open is off.
            if (_settings.ExpandOnHover || _expanded)
            {
                ScheduleExpanded(true, OpenDelay);
            }
        };
        Island.MouseLeave += (_, _) =>
        {
            // WPF also reports a leave when the window is resized under a pointer that has not
            // moved, so the pointer's real position decides.
            bool over = PointerOverIsland();
            HoverTrace.Write($"leave over={over}");
            if (!over)
            {
                ScheduleExpanded(false, CloseDelay);
            }
        };
        Island.MouseLeftButtonDown += OnIslandClicked;

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (!_hoverWantsExpanded && _expanded && PointerOverIsland())
            {
                return;
            }

            SetExpanded(_hoverWantsExpanded);
        };
        _pointerWatch.Tick += (_, _) => WatchPointer();
        _housekeepingTimer.Tick += (_, _) => Housekeeping();
        _fullscreenRecheck.Tick += (_, _) =>
        {
            _fullscreenRecheck.Stop();
            Housekeeping();
        };

        // Colours everything that is coloured in code, and does the first Refresh.
        ThemeManager.Changed += OnThemeChanged;
        OnThemeChanged();
        UpdateMediaCard();
    }

    private readonly record struct Shape(double Width, double Height, double Radius);

    private Shape ShapeFor(NotchMode mode) => mode switch
    {
        NotchMode.Compact => new Shape(320, 34, 17),
        NotchMode.Peek => new Shape(360, 44, 22),
        NotchMode.Expanded when _tab == NotchTab.Terminal => new Shape(920, 540, 30),
        NotchMode.Expanded when _tab == NotchTab.Stats => new Shape(640, 290, 30),
        NotchMode.Expanded when _tab == NotchTab.Plugins => new Shape(640, PluginsTabHeight, 30),

        // Home is taller while the media card is showing.
        NotchMode.Expanded => new Shape(640, _media.Current is null ? 236 : 364, 30),
        _ => new Shape(180, 32, 16),
    };

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        OverlayWindow.ApplyOverlayStyles(_hwnd);
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);

        Reposition();
        _housekeepingTimer.Start();

        // The periodic check alone would leave the notch over a game for up to a second after it starts.
        _foregroundWatcher = new ForegroundWatcher(() => Dispatcher.BeginInvoke(OnForegroundChanged));
    }

    private void OnForegroundChanged()
    {
        Housekeeping();
        _fullscreenRecheck.Stop();
        _fullscreenRecheck.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _activities.Changed -= OnActivitiesChanged;
        _media.Changed -= OnMediaChanged;
        _pluginCards.Changed -= OnPluginCardsChanged;
        ThemeManager.Changed -= OnThemeChanged;
        _mediaTimer.Stop();
        _glow.Dispose();
        _hoverTimer.Stop();
        _pointerWatch.Stop();
        _housekeepingTimer.Stop();
        _fullscreenRecheck.Stop();
        _foregroundWatcher?.Dispose();
        base.OnClosed(e);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED)
        {
            // Let WPF finish its own DPI handling before measuring against the new layout.
            Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);
        }
        else if (msg == WM_SETTINGCHANGE && _settings.Theme == NotchTheme.System)
        {
            // Windows sends this for many settings, the app theme among them.
            Dispatcher.BeginInvoke(() => ThemeManager.Apply(_settings), DispatcherPriority.Background);
        }

        return 0;
    }

    /// <summary>Recolours what the theme brushes do not reach.</summary>
    private void OnThemeChanged()
    {
        _terminal.Bridge.SetTheme(ThemeManager.IsLight);
        ApplyWidgetColors();
        UpdatePluginCards();
        Refresh();
    }

    /// <summary>True while restarting the app would interrupt something: the notch is open, a terminal session exists, or a timer is counting.</summary>
    public bool IsBusy =>
        _expanded
        || _terminal.Sessions.Count > 0
        || _countdown.State is CountdownState.Running or CountdownState.Paused;

    /// <summary>Re-reads the settings object after the user saved changes.</summary>
    public void ApplySettings()
    {
        ThemeManager.Apply(_settings);
        Reposition();
        Housekeeping();
        RefreshCalendar();
        ShowTimerPresets();
        _glow.Gain = GlowOutput.Gain(_settings.GlowIntensity);
        ApplyShape();
        Refresh();
    }

    /// <summary>
    /// Every repaint of a transparent window copies the whole window, so it stays only as big
    /// as the pill needs and grows just before the notch expands. This keeps glow animations cheap.
    /// </summary>
    private void SetLargeWindow(bool large)
    {
        if (_largeWindow != large)
        {
            _largeWindow = large;
            HoverTrace.Write($"window large={large}");
            if (_hwnd != 0)
            {
                Reposition();
            }
        }
    }

    /// <summary>Pins the window to the top-center of the chosen display, in physical pixels.</summary>
    private void Reposition()
    {
        // The saved display may have been unplugged since; fall back to the primary one.
        IReadOnlyList<DisplayInfo> displays = Displays.GetAll();
        DisplayInfo display = (_displayOverride ?? _settings.DisplayIndex) is { } index && index >= 0 && index < displays.Count
            ? displays[index]
            : Displays.GetPrimary();
        _display = display;

        Size size = _largeWindow ? LargeWindowSize : SmallWindowSize;
        int width = (int)Math.Round(size.Width * display.Scale);
        int height = (int)Math.Round(size.Height * display.Scale);
        int left = display.Bounds.Left + ((display.Bounds.Width - width) / 2);
        var target = new PixelRect(left, display.Bounds.Top, left + width, display.Bounds.Top + height);

        if (OverlayWindow.GetBounds(_hwnd) != target)
        {
            OverlayWindow.SetBounds(_hwnd, target);
        }
    }

    private void Housekeeping()
    {
        if (_display is null)
        {
            return;
        }

        NoteForegroundWindow();

        // Not while the user is typing into the notch: then it is the foreground window, by their choice.
        bool fullscreen = _settings.HideInFullscreen && !KeyboardInUse && FullscreenDetector.IsFullscreenAppOn(_display, _hwnd);
        SetHiddenForFullscreen(fullscreen);
        if (!fullscreen)
        {
            OverlayWindow.BringToTop(_hwnd);
        }

        // Catches a shrink that was put off because the pointer was resting on the pill.
        if (!_animator.IsRunning)
        {
            ShrinkWindowWhenClear();
        }
    }

    /// <summary>
    /// Takes the whole window off the screen while a fullscreen app shows on this display, so
    /// nothing of the notch, not even a HUD or its glow, is drawn over a game, and no
    /// always-on-top window sits above it.
    /// </summary>
    private void SetHiddenForFullscreen(bool hidden)
    {
        if (_hiddenForFullscreen == hidden)
        {
            return;
        }

        _hiddenForFullscreen = hidden;
        HoverTrace.Write($"hidden for fullscreen={hidden}");
        if (hidden)
        {
            _hoverTimer.Stop();
            SetExpanded(false);
            Hide();
        }
        else
        {
            // ShowActivated is off, so coming back does not take the keyboard.
            Show();
            Reposition();
        }
    }

    /// <summary>Returns the window to its small size once the notch is closed and the pointer is off it.</summary>
    private void ShrinkWindowWhenClear()
    {
        if (!_expanded && _largeWindow && !PointerOverIsland())
        {
            SetLargeWindow(false);
        }
    }

    /// <summary>
    /// Whether the pointer is on the island, judged by where both are on screen. Mouse events
    /// alone are not reliable here: resizing the window, or the terminal's own child window,
    /// makes WPF report a leave while the pointer is still on the notch.
    /// </summary>
    private bool PointerOverIsland()
    {
        if (_display is not { } display || OverlayWindow.GetCursorPosition() is not { } cursor)
        {
            return Island.IsMouseOver;
        }

        return IslandHitTest.Contains(
            display.Bounds.Left,
            display.Bounds.Top,
            display.Bounds.Width,
            display.Scale,
            _animator.Width,
            _animator.Height,
            cursor.X,
            cursor.Y,
            HoverMargin);
    }

    /// <summary>
    /// Runs while the notch is open. Closes it once the pointer has really left, including when
    /// WPF never reported that, and calls off a close when the pointer is back.
    /// </summary>
    private void WatchPointer()
    {
        bool closePending = _hoverTimer.IsEnabled && !_hoverWantsExpanded;
        if (PointerOverIsland())
        {
            if (closePending)
            {
                HoverTrace.Write("watch: close cancelled");
                _hoverTimer.Stop();
            }
        }
        else if (!closePending && !_pinnedOpen)
        {
            HoverTrace.Write("watch: pointer left");
            ScheduleExpanded(false, CloseDelay);
        }
    }

    private void OnActivitiesChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(Refresh);

    private void OnIslandClicked(object sender, MouseButtonEventArgs e)
    {
        _hoverTimer.Stop();
        SetExpanded(true);
    }

    private void ScheduleExpanded(bool expanded, TimeSpan delay)
    {
        if (!expanded && KeyboardInUse)
        {
            return;
        }

        _hoverWantsExpanded = expanded;
        _hoverTimer.Stop();
        _hoverTimer.Interval = delay;
        _hoverTimer.Start();
    }

    /// <summary>
    /// Puts the notch on a display of this run's choosing without changing the saved setting
    /// (<c>--display=</c>, for development: keeps a debug build off an installed copy's screen).
    /// </summary>
    /// <param name="index">An index into the system's display list, like <see cref="AppSettings.DisplayIndex"/>.</param>
    public void UseDisplay(int index)
    {
        _displayOverride = index;
        if (_hwnd != 0)
        {
            Reposition();
        }
    }

    /// <summary>Keeps the notch expanded regardless of the pointer (<c>--pin-open</c>, for development).</summary>
    public void PinOpen()
    {
        _pinnedOpen = true;
        SetExpanded(true);
    }

    private void SetExpanded(bool expanded)
    {
        expanded |= _pinnedOpen;
        if (_expanded != expanded)
        {
            _expanded = expanded;
            HoverTrace.Write($"expanded={expanded}");
            if (expanded)
            {
                UpdateCalendar();
                _pointerWatch.Start();
            }
            else
            {
                _pointerWatch.Stop();
                SetTimerEntryOpen(false);
            }

            Refresh();
            UpdateKeyboardInteraction();
            UpdateStatsTimer();
        }
    }

    private void Refresh()
    {
        IReadOnlyList<Activity> activities = _activities.Snapshot();
        NotchMode mode = NotchModeResolver.Resolve(_expanded, activities);

        if (mode == NotchMode.Expanded)
        {
            SetLargeWindow(true);
        }

        Shape shape = ShapeFor(mode);
        _animator.AnimateTo(shape.Width, shape.Height, shape.Radius);

        if (activities.Count > 0)
        {
            Activity top = activities[0];
            CompactGlyph.Text = top.Glyph;

            // The glyph takes the colour of the activity's glow; without one it is plain text.
            if (top.Glow is { } glow)
            {
                CompactGlyph.Foreground = ThemeManager.Brush(glow.Color);
            }
            else
            {
                CompactGlyph.ClearValue(ForegroundProperty);
            }

            if (!ReferenceEquals(top.Image, _compactArtBytes))
            {
                _compactArtBytes = top.Image;
                ImageSource? art = ImageLoader.Decode(top.Image, 64);
                CompactArt.Background = ImageLoader.ToCoverBrush(art);
                _artAccent = ImageLoader.AccentOf(art);
            }

            // Album art and the like replace the glyph when the image decodes.
            bool showArt = CompactArt.Background is not null;
            CompactArt.Visibility = showArt ? Visibility.Visible : Visibility.Collapsed;
            CompactGlyph.Visibility = showArt ? Visibility.Collapsed : Visibility.Visible;
            CompactTitle.Text = top.Title;
            CompactDetail.Text = top.Detail;

            // A transient HUD with a level (volume, brightness) shows a bar instead of detail text.
            bool showBar = top is { Tier: ActivityTier.Transient, Progress: not null };
            CompactProgress.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
            CompactDetail.Visibility = showBar ? Visibility.Collapsed : Visibility.Visible;
            CompactProgress.Value = top.Progress ?? 0;
        }

        // The content keeps a fixed size per tab; the island grows or shrinks around it.
        Shape expanded = ShapeFor(NotchMode.Expanded);
        ExpandedLayer.Width = expanded.Width - 40;
        ExpandedLayer.Height = expanded.Height - 36;
        UpdateMediaTimer();

        // No glow while expanded: the halo around the large panel would swallow clicks meant
        // for the windows next to it, including the click that closes the notch.
        Activity? lit = mode is NotchMode.Compact or NotchMode.Peek && _settings.GlowEffects ? activities[0] : null;
        _glow.Show(lit?.Glow, lit?.Id == MediaActivityPublisher.ActivityId && lit.Image is not null ? _artAccent : null);

        Fade(CompactLayer, mode is NotchMode.Compact or NotchMode.Peek);
        Fade(ExpandedLayer, mode is NotchMode.Expanded);
        ExpandedLayer.IsHitTestVisible = mode is NotchMode.Expanded;
    }

    private static void Fade(UIElement element, bool visible) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, FadeDuration));

    private void ApplyShape()
    {
        double width = Math.Max(0, _animator.Width);
        double height = Math.Max(0, _animator.Height);
        double radius = Math.Max(0, _animator.Radius);

        Island.Width = width;
        Island.Height = height;

        // The glow layers follow the island's outline, a little outside it.
        double spread = _glow.Spread;
        GlowLayer.Width = GlowCore.Width = width + (2 * spread);
        GlowLayer.Height = GlowCore.Height = height + spread;
        GlowLayer.CornerRadius = GlowCore.CornerRadius = new CornerRadius(0, 0, radius + spread, radius + spread);

        // The rounded rectangle starts one radius above the island, so only the bottom corners
        // are rounded and the top edge sits flush against the screen edge.
        _islandClip.Rect = new Rect(0, -radius, width, height + radius);
        _islandClip.RadiusX = radius;
        _islandClip.RadiusY = radius;
    }
}
