using Microsoft.Extensions.Time.Testing;
using Notch.Core.Activities;
using Notch.Core.Plugins;

namespace Notch.Core.Tests;

public class PluginGlowBoardTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Shows_the_most_recent_frame_of_any_plugin()
    {
        var board = new PluginGlowBoard(_time);
        Assert.Null(board.Current);

        board.Set("a", new GlowFrame { Color = GlowColor.Red });
        board.Set("b", new GlowFrame { Color = GlowColor.Blue });
        Assert.Equal(GlowColor.Blue, board.Current!.Color);

        board.Set("a", new GlowFrame { Color = GlowColor.Green });
        Assert.Equal(GlowColor.Green, board.Current!.Color);

        board.Remove("a");
        Assert.Equal(GlowColor.Blue, board.Current!.Color);
    }

    [Fact]
    public void A_frame_expires_when_its_plugin_stops_refreshing_it()
    {
        var board = new PluginGlowBoard(_time);
        board.Set("a", new GlowFrame());

        _time.Advance(PluginGlowBoard.Lifetime - TimeSpan.FromMilliseconds(1));
        Assert.NotNull(board.Current);

        _time.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Null(board.Current);

        board.Set("a", new GlowFrame());
        Assert.NotNull(board.Current);
    }

    [Fact]
    public void Numbers_are_pulled_into_range_and_segments_are_capped()
    {
        var board = new PluginGlowBoard(_time);
        board.Set("a", new GlowFrame
        {
            Intensity = double.NaN,
            Reach = 100,
            Segments = [.. Enumerable.Range(0, 200).Select(i => new GlowSegment(GlowColor.Cyan, i == 0 ? 7 : -1))],
        });

        GlowFrame frame = board.Current!;
        Assert.Equal(0, frame.Intensity);
        Assert.Equal(3, frame.Reach);
        Assert.Equal(PluginGlowBoard.MaxSegments, frame.Segments!.Count);
        Assert.Equal(1, frame.Segments[0].Intensity);
        Assert.Equal(0, frame.Segments[1].Intensity);
    }

    [Fact]
    public void An_empty_segment_list_means_one_uniform_glow()
    {
        var board = new PluginGlowBoard(_time);
        board.Set("a", new GlowFrame { Segments = [] });

        Assert.Null(board.Current!.Segments);
    }

    [Fact]
    public void Reports_when_the_first_frame_arrives_and_the_last_is_removed()
    {
        var board = new PluginGlowBoard(_time);
        int raised = 0;
        board.ActiveChanged += (_, _) => raised++;

        board.Set("a", new GlowFrame());
        board.Set("a", new GlowFrame());
        board.Set("b", new GlowFrame());
        Assert.Equal(1, raised);

        board.Remove("a");
        Assert.Equal(1, raised);
        board.Remove("b");
        board.Remove("b");
        Assert.Equal(2, raised);
    }
}

public class PluginAudioHubTests
{
    private readonly FakeTimeProvider _time = new();

    private static AudioFrame Loud() => new(0.8, 0.9, 0.5, 0.2, new float[PluginAudio.BandCount]);

    [Fact]
    public void Listens_only_while_somebody_asks_for_frames()
    {
        var hub = new PluginAudioHub(_time);
        int started = 0, stopped = 0;
        hub.Starter = _ =>
        {
            started++;
            return new Stopper(() => stopped++);
        };

        IDisposable first = hub.Acquire();
        IDisposable second = hub.Acquire();
        Assert.Equal(1, started);

        first.Dispose();
        first.Dispose();
        Assert.Equal(0, stopped);
        second.Dispose();
        Assert.Equal(1, stopped);

        hub.Acquire().Dispose();
        Assert.Equal(2, started);
    }

    [Fact]
    public void Frames_go_stale_because_a_silent_output_delivers_nothing()
    {
        var hub = new PluginAudioHub(_time);
        Assert.Same(AudioFrame.Silent, hub.Latest);

        hub.Publish(Loud());
        Assert.Equal(0.8, hub.Latest.Level);

        _time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Same(AudioFrame.Silent, hub.Latest);
    }

    [Fact]
    public void A_starter_that_throws_means_silence_not_a_failure()
    {
        var hub = new PluginAudioHub(_time) { Starter = _ => throw new InvalidOperationException("no device") };

        hub.Acquire().Dispose();

        Assert.Same(AudioFrame.Silent, hub.Latest);
    }

    private sealed class Stopper(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}
