using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Notch.Core.Activities;
using Notch.Core.Shell;
using Notch.Platform.Display;

namespace Notch.App.Shell;

public partial class NotchWindow : Window
{
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;

    private static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(350);
    private static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(160);

    private readonly ActivityManager _activities;
    private readonly NotchAnimator _animator;
    private readonly RectangleGeometry _islandClip = new();
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly DispatcherTimer _housekeepingTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private nint _hwnd;
    private DisplayInfo? _display;
    private bool _expanded;
    private bool _hoverWantsExpanded;

    public NotchWindow(ActivityManager activities)
    {
        InitializeComponent();

        _activities = activities;
        _activities.Changed += OnActivitiesChanged;

        Shape idle = ShapeFor(NotchMode.Idle);
        _animator = new NotchAnimator(idle.Width, idle.Height, idle.Radius);
        _animator.Frame += ApplyShape;
        Island.Clip = _islandClip;
        ApplyShape();

        Island.MouseEnter += (_, _) => ScheduleExpanded(true, OpenDelay);
        Island.MouseLeave += (_, _) => ScheduleExpanded(false, CloseDelay);
        Island.MouseLeftButtonDown += OnIslandClicked;

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            SetExpanded(_hoverWantsExpanded);
        };
        _housekeepingTimer.Tick += (_, _) => Housekeeping();

        Refresh();
    }

    private readonly record struct Shape(double Width, double Height, double Radius);

    private static Shape ShapeFor(NotchMode mode) => mode switch
    {
        NotchMode.Compact => new Shape(320, 34, 17),
        NotchMode.Peek => new Shape(360, 44, 22),
        NotchMode.Expanded => new Shape(640, 360, 30),
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
    }

    protected override void OnClosed(EventArgs e)
    {
        _activities.Changed -= OnActivitiesChanged;
        _hoverTimer.Stop();
        _housekeepingTimer.Stop();
        base.OnClosed(e);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED)
        {
            // Let WPF finish its own DPI handling before measuring against the new layout.
            Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);
        }

        return 0;
    }

    /// <summary>Pins the window to the top-center of the primary display, in physical pixels.</summary>
    private void Reposition()
    {
        DisplayInfo display = Displays.GetPrimary();
        _display = display;

        int width = (int)Math.Round(Width * display.Scale);
        int height = (int)Math.Round(Height * display.Scale);
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

        bool fullscreen = FullscreenDetector.IsFullscreenAppOn(_display, _hwnd);
        Root.Visibility = fullscreen ? Visibility.Hidden : Visibility.Visible;
        if (!fullscreen)
        {
            OverlayWindow.BringToTop(_hwnd);
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
        _hoverWantsExpanded = expanded;
        _hoverTimer.Stop();
        _hoverTimer.Interval = delay;
        _hoverTimer.Start();
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded != expanded)
        {
            _expanded = expanded;
            Refresh();
        }
    }

    private void Refresh()
    {
        IReadOnlyList<Activity> activities = _activities.Snapshot();
        NotchMode mode = NotchModeResolver.Resolve(_expanded, activities);

        Shape shape = ShapeFor(mode);
        _animator.AnimateTo(shape.Width, shape.Height, shape.Radius);

        if (activities.Count > 0)
        {
            Activity top = activities[0];
            CompactGlyph.Text = top.Glyph;
            CompactTitle.Text = top.Title;
            CompactDetail.Text = top.Detail;

            // A transient HUD with a level (volume, brightness) shows a bar instead of detail text.
            bool showBar = top is { Tier: ActivityTier.Transient, Progress: not null };
            CompactProgress.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
            CompactDetail.Visibility = showBar ? Visibility.Collapsed : Visibility.Visible;
            CompactProgress.Value = top.Progress ?? 0;
        }

        ActivityList.ItemsSource = activities;
        EmptyText.Visibility = activities.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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

        // The rounded rectangle starts one radius above the island, so only the bottom corners
        // are rounded and the top edge sits flush against the screen edge.
        _islandClip.Rect = new Rect(0, -radius, width, height + radius);
        _islandClip.RadiusX = radius;
        _islandClip.RadiusY = radius;
    }
}
