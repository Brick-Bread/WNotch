using Notch.Core.Activities;
using Notch.Core.Automation;
using Notch.Core.Settings;
using Notch.Platform.Automation;

namespace Notch.App.Automation;

/// <summary>
/// Runs the webhook while the settings say it is on, and applies what callers ask for to the
/// notch. Starting, stopping and the token follow <see cref="AppSettings"/>; call
/// <see cref="Apply"/> after they change.
/// </summary>
internal sealed class WebhookService : IDisposable
{
    /// <summary>The most activities callers may have showing at once; a runaway script cannot fill the pill's list.</summary>
    private const int MaxLiveActivities = 20;

    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly ActivityManager _activities;
    private readonly CommandDispatcher _commands;
    private readonly WebhookServer _server;
    private readonly HashSet<string> _live = [];

    public WebhookService(AppSettings settings, SettingsStore store, ActivityManager activities, CommandDispatcher commands)
    {
        _settings = settings;
        _store = store;
        _activities = activities;
        _commands = commands;
        _server = new WebhookServer(HandleAsync);
    }

    /// <summary>Why the webhook is not running even though it is switched on; null when it is fine or off.</summary>
    public string? Problem { get; private set; }

    /// <summary>Starts, restarts or stops the listener to match the settings.</summary>
    public void Apply()
    {
        Problem = null;
        if (!_settings.WebhookEnabled)
        {
            _server.Stop();
            return;
        }

        if (string.IsNullOrEmpty(_settings.WebhookToken))
        {
            _settings.WebhookToken = WebhookRequest.NewToken();
            _store.Save(_settings);
        }

        Problem = _server.Start(_settings.WebhookPort, _settings.WebhookToken);
    }

    public void Dispose() => _server.Dispose();

    private async Task<CommandResult> HandleAsync(WebhookAction action)
    {
        switch (action)
        {
            case PublishActivityAction publish:
                // Transient ones expire by themselves; only those that stay until removed are counted.
                if (publish.Activity.Tier != ActivityTier.Transient)
                {
                    lock (_live)
                    {
                        if (!_live.Contains(publish.Activity.Id) && _live.Count >= MaxLiveActivities)
                        {
                            return new CommandResult(false, $"Too many activities are showing (the limit is {MaxLiveActivities}). Remove some first.");
                        }

                        _live.Add(publish.Activity.Id);
                    }
                }
                else
                {
                    lock (_live)
                    {
                        _live.Remove(publish.Activity.Id);
                    }
                }

                _activities.Publish(publish.Activity);
                return new CommandResult(true);

            case RemoveActivityAction remove:
                lock (_live)
                {
                    _live.Remove(remove.ActivityId);
                }

                _activities.Remove(remove.ActivityId);
                return new CommandResult(true);

            case RunCommandAction run:
                return await _commands.RunAsync(run.Command);

            default:
                return new CommandResult(false, "Unknown request.");
        }
    }
}
