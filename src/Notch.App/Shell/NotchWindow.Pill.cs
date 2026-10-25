using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Notch.Core.Activities;
using Notch.Core.Agents;
using Notch.Core.Media;

namespace Notch.App.Shell;

// The pill: what the activity on top is (left), a line about it (middle) and its state (right).
public partial class NotchWindow
{
    private const string DoneGlyph = "";

    private bool _dotsRunning;
    private bool _waveRunning;

    /// <summary>The activity the pill shows, which also decides how wide it is (<see cref="BaseShapeFor"/>).</summary>
    private Activity? _pillActivity;

    private static bool IsMedia(Activity activity) => activity.Id == MediaActivityPublisher.ActivityId;

    /// <summary>The pill's width for an activity: wide enough for what it shows, and no wider.</summary>
    private static double PillWidth(Activity? activity) => activity switch
    {
        null => 360,
        _ when IsMedia(activity) => 380,
        { AgentState: AgentState.NeedsInput } => 372,
        { AgentState: not null } => 292,
        _ => 360,
    };

    private const int MaxGlanceItems = 6;
    private const double MaxGlanceWidth = 520;

    /// <summary>The pill's width while it shows the glance row, measured when the row is filled.</summary>
    private double _glanceWidth = 180;

    /// <summary>The glance items' texts that are clocks (<see cref="Activity.CountingFrom"/>), moved on every second.</summary>
    private readonly List<(TextBlock Text, DateTimeOffset From)> _glanceClocks = [];

    private void FillPill(Activity top, IReadOnlyList<Activity> activities)
    {
        bool glance = top.Tier == ActivityTier.Glance;
        GlanceRow.Visibility = glance ? Visibility.Visible : Visibility.Collapsed;
        CompactIcon.Visibility = CompactText.Visibility = CompactRight.Visibility = glance ? Visibility.Collapsed : Visibility.Visible;
        if (glance)
        {
            FillGlance(activities);
            StopPillAnimations();
            return;
        }

        FillPill(top);
    }

    /// <summary>Every glance activity side by side: its glyph or picture, then its title or clock.</summary>
    private void FillGlance(IReadOnlyList<Activity> activities)
    {
        GlanceRow.Children.Clear();
        _glanceClocks.Clear();
        foreach (Activity item in activities
            .Where(a => a.Tier == ActivityTier.Glance)
            .OrderBy(a => a.Order)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .Take(MaxGlanceItems))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = item, Background = Brushes.Transparent };
            if (GlanceRow.Children.Count > 0)
            {
                row.Margin = new Thickness(14, 0, 0, 0);
            }

            if (ImageLoader.Decode(item.Image, 32) is { } picture)
            {
                row.Children.Add(new Image { Source = picture, Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center });
            }
            else if (!string.IsNullOrEmpty(item.Glyph))
            {
                var glyph = new TextBlock { Text = item.Glyph, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
                if (item.Glow is { } glow)
                {
                    glyph.Foreground = ThemeManager.Brush(glow.Color);
                }

                row.Children.Add(glyph);
            }

            var text = new TextBlock
            {
                Text = item.CountingFrom is { } from ? ElapsedClock.Since(from) : item.Title,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                MaxWidth = 150,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(row.Children.Count > 0 ? 6 : 0, 0, 0, 0),
                ToolTip = item.CountingFrom is null ? null : item.Title,
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            row.Children.Add(text);
            if (item.CountingFrom is { } clock)
            {
                _glanceClocks.Add((text, clock));
            }

            GlanceRow.Children.Add(row);
        }

        // The pill's sides (CompactLayer's margin) plus the row, within reason.
        GlanceRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _glanceWidth = Math.Clamp(Math.Ceiling(GlanceRow.DesiredSize.Width + 40), 120, MaxGlanceWidth);
    }

    /// <summary>The glance item under the pointer, or the nearest one; null when the row is empty.</summary>
    private Activity? GlanceItemAt(MouseEventArgs e)
    {
        FrameworkElement? nearest = null;
        double best = double.MaxValue;
        foreach (FrameworkElement item in GlanceRow.Children.OfType<FrameworkElement>())
        {
            double x = e.GetPosition(item).X;
            double distance = x < 0 ? -x : x > item.ActualWidth ? x - item.ActualWidth : 0;
            if (distance < best)
            {
                best = distance;
                nearest = item;
            }
        }

        return nearest?.Tag as Activity;
    }

    /// <summary>Moves on the glance items' clocks.</summary>
    private void TickGlanceClocks()
    {
        foreach ((TextBlock text, DateTimeOffset from) in _glanceClocks)
        {
            text.Text = ElapsedClock.Since(from);
        }
    }

    private void FillPill(Activity top)
    {
        if (!ReferenceEquals(top.Image, _compactArtBytes))
        {
            _compactArtBytes = top.Image;
            ImageSource? art = ImageLoader.Decode(top.Image, 64);
            CompactArt.Background = ImageLoader.ToCoverBrush(art);
            _artAccent = ImageLoader.AccentOf(art);
        }

        // Left: album art when there is some, else the agent's logo, else the glyph.
        Geometry? logo = top.AgentId is { } agent ? TryFindResource("AgentLogo." + agent) as Geometry : null;
        bool showArt = CompactArt.Background is not null;
        CompactArt.Visibility = showArt ? Visibility.Visible : Visibility.Collapsed;
        CompactLogo.Data = logo;
        CompactLogo.Visibility = !showArt && logo is not null ? Visibility.Visible : Visibility.Collapsed;
        CompactGlyph.Visibility = !showArt && logo is null ? Visibility.Visible : Visibility.Collapsed;
        CompactGlyph.Text = top.Glyph;

        // A plain glyph takes the colour of the activity's glow; a logo never changes colour.
        if (top.Glow is { } glow)
        {
            CompactGlyph.Foreground = ThemeManager.Brush(glow.Color);
        }
        else
        {
            CompactGlyph.ClearValue(ForegroundProperty);
        }

        CompactTitle.Text = top.Title;
        CompactSubtitle.Text = "";
        CompactDetail.Text = top.CountingFrom is { } from ? ElapsedClock.Since(from) : top.Detail;
        CompactDetail.SetResourceReference(ForegroundProperty, "SecondaryTextBrush");
        CompactStateGlyph.Visibility = Visibility.Collapsed;
        bool dots = false;
        bool wave = false;

        // A transient HUD with a level (volume, brightness) is its glyph and a bar: nothing to read.
        bool bar = top is { Tier: ActivityTier.Transient, Progress: not null };

        if (IsMedia(top))
        {
            CompactSubtitle.Text = string.IsNullOrWhiteSpace(top.Detail) ? "" : " · " + top.Detail;
            CompactDetail.Text = null;
            wave = true;
        }
        else if (top.AgentState is { } state)
        {
            // The title is "Name · folder". With the logo showing who it is, the folder is enough.
            string[] parts = top.Title.Split(" · ", 2);
            if (logo is not null && parts.Length == 2)
            {
                CompactTitle.Text = "";
                CompactSubtitle.Text = parts[1];
            }

            // "Needs input · +1": what follows the state counts the other sessions with something to say.
            string others = top.Detail?.Split(" · ", 2) is [_, var more] ? " " + more : "";
            switch (state)
            {
                case AgentState.Working:
                    dots = true;
                    CompactDetail.Text = others.Trim();
                    break;
                case AgentState.NeedsInput:
                    CompactDetail.Text = "Needs you" + others;
                    CompactDetail.SetResourceReference(ForegroundProperty, "AttentionBrush");
                    break;
                case AgentState.Done:
                    CompactDetail.Text = "Done" + others;
                    CompactDetail.SetResourceReference(ForegroundProperty, "DoneBrush");
                    CompactStateGlyph.Text = DoneGlyph;
                    CompactStateGlyph.SetResourceReference(ForegroundProperty, "DoneBrush");
                    CompactStateGlyph.Visibility = Visibility.Visible;
                    break;
            }
        }
        else if (bar)
        {
            CompactTitle.Text = "";
        }

        CompactProgress.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
        CompactLevel.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
        CompactProgress.Value = top.Progress ?? 0;
        CompactLevel.Text = bar ? Math.Round((top.Progress ?? 0) * 100).ToString("0") : "";
        CompactDetail.Visibility = bar || string.IsNullOrEmpty(CompactDetail.Text) ? Visibility.Collapsed : Visibility.Visible;

        foreach (Rectangle stroke in CompactWave.Children.OfType<Rectangle>())
        {
            stroke.Fill = _artAccent is { } accent && top.Image is not null ? ThemeManager.Brush(accent) : (Brush)FindResource("TextBrush");
        }

        RunDots(dots);
        RunWave(wave);
    }

    /// <summary>Stops the pill's moving parts while it is not showing them.</summary>
    private void StopPillAnimations()
    {
        RunDots(false);
        RunWave(false);
    }

    private void RunDots(bool run)
    {
        CompactDots.Visibility = run ? Visibility.Visible : Visibility.Collapsed;
        if (run == _dotsRunning)
        {
            return;
        }

        _dotsRunning = run;
        int i = 0;
        foreach (UIElement dot in CompactDots.Children)
        {
            AnimationTimeline? blink = run
                ? new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(0.6))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(0.2 * i++),
                    EasingFunction = new SineEase(),
                }
                : null;
            dot.BeginAnimation(OpacityProperty, blink);
        }
    }

    private void RunWave(bool run)
    {
        CompactWave.Visibility = run ? Visibility.Visible : Visibility.Collapsed;
        if (run == _waveRunning)
        {
            return;
        }

        _waveRunning = run;
        double[] offsets = [0, 0.3, 0.15, 0.45];
        int i = 0;
        foreach (Rectangle stroke in CompactWave.Children.OfType<Rectangle>())
        {
            if (stroke.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
            {
                scale = new ScaleTransform(1, 0.35);
                stroke.RenderTransform = scale;
            }

            AnimationTimeline? move = run
                ? new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(0.45))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(offsets[i++ % offsets.Length]),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                }
                : null;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, move);
        }
    }
}
