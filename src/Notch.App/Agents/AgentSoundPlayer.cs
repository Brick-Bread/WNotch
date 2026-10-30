using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Notch.Core.Agents;
using Notch.Core.Settings;

namespace Notch.App.Agents;

/// <summary>
/// Plays a sound when an agent finishes or needs the user, when the user switched sounds on. Never
/// while a game or other fullscreen app has the notch hidden: a sound would be as much in the way as the pill.
/// </summary>
internal sealed class AgentSoundPlayer : IDisposable
{
    /// <summary>One for the whole app, on the UI thread, kept here so it is not collected halfway through a sound.</summary>
    private static MediaPlayer? s_player;

    private readonly AgentBoard _board;
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly Func<bool> _quiet;
    private readonly AgentChimes _chimes = new();

    /// <param name="quiet">True while sounds must not play, such as while a fullscreen app shows.</param>
    public AgentSoundPlayer(AgentBoard board, AppSettings settings, Dispatcher ui, Func<bool> quiet)
    {
        _board = board;
        _settings = settings;
        _ui = ui;
        _quiet = quiet;
        _board.Changed += OnBoardChanged;
    }

    /// <summary>The sound Notch comes with for <paramref name="chime"/>.</summary>
    public static string BuiltInFile(AgentChime chime) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "sounds", chime == AgentChime.Done ? "done.wav" : "needs-input.wav");

    /// <summary>Plays the sound for <paramref name="chime"/> now, from <paramref name="file"/> or the built-in one. Call on the UI thread.</summary>
    public static void Play(AgentChime chime, string? file, int volume)
    {
        string path = !string.IsNullOrWhiteSpace(file) && File.Exists(file) ? file : BuiltInFile(chime);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            s_player ??= new MediaPlayer();
            s_player.Open(new Uri(path, UriKind.Absolute));
            s_player.Volume = Math.Clamp(volume, 0, 100) / 100.0;
            s_player.Play();
        }
        catch (Exception e) when (e is InvalidOperationException or UriFormatException or IOException)
        {
            // A file that cannot be played is no reason to bother the user; the pill still shows the state.
        }
    }

    public void Dispose() => _board.Changed -= OnBoardChanged;

    private void OnBoardChanged()
    {
        // The board changes on whichever thread changed it; the player and the bookkeeping live on the UI thread.
        _ = _ui.BeginInvoke(() =>
        {
            AgentChime? chime = _chimes.Update(_board.Snapshot(), DateTime.UtcNow);
            if (chime is { } play && _settings.AgentSounds && !_quiet())
            {
                Play(play, play == AgentChime.Done ? _settings.DoneSound : _settings.NeedsInputSound, _settings.SoundVolume);
            }
        });
    }
}
