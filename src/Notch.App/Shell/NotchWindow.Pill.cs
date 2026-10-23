using System.Windows;
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
