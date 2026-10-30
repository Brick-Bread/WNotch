using System.Windows.Threading;
using Notch.Core.Agents;
using Notch.Core.Settings;
using Notch.Platform.Agents;

namespace Notch.App.Agents;

/// <summary>A prompt waiting in the notch for the user's answer.</summary>
internal sealed class PendingApproval
{
    public PendingApproval(ApprovalRequest request, HookReply reply, DispatcherTimer expiry)
    {
        Request = request;
        Reply = reply;
        Expiry = expiry;
    }

    public ApprovalRequest Request { get; }

    public HookReply Reply { get; }

    public DispatcherTimer Expiry { get; }

    public DateTime Received { get; } = DateTime.UtcNow;
}

/// <summary>
/// Holds the permission prompts and questions agents send through their hooks until the user answers them in
/// the notch. Each one goes back to its agent's own window when the user does not answer in time, when it was
/// answered there instead, or when its agent stops waiting. Everything here runs on the UI thread.
/// </summary>
internal sealed class ApprovalService : IDisposable
{
    private readonly AgentHookServer _hooks;
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly List<PendingApproval> _pending = [];

    public ApprovalService(AgentHookServer hooks, AppSettings settings, Dispatcher ui)
    {
        _hooks = hooks;
        _settings = settings;
        _ui = ui;
        _hooks.ReplyRequested += OnReplyRequested;
        _hooks.MessageReceived += OnMessage;
    }

    /// <summary>Raised on the UI thread when a prompt came or went.</summary>
    public event Action? Changed;

    /// <summary>Raised on the UI thread when a new prompt arrived.</summary>
    public event Action<PendingApproval>? Arrived;

    /// <summary>The prompts waiting, oldest first.</summary>
    public IReadOnlyList<PendingApproval> Pending => _pending;

    public void Dispose()
    {
        _hooks.ReplyRequested -= OnReplyRequested;
        _hooks.MessageReceived -= OnMessage;
        foreach (PendingApproval pending in _pending.ToArray())
        {
            Settle(pending, "");
        }
    }

    /// <summary>Sends the user's answer to the agent and takes the prompt out of the notch.</summary>
    public void Answer(PendingApproval pending, ApprovalAnswer answer) =>
        Settle(pending, AgentApprovals.Reply(pending.Reply.Message, answer));

    /// <summary>Leaves the prompt to the agent's own window.</summary>
    public void Dismiss(PendingApproval pending) => Settle(pending, "");

    private void OnReplyRequested(HookReply reply)
    {
        ApprovalRequest? request = _settings.AnswerPromptsInNotch ? AgentApprovals.Read(reply.Message) : null;
        if (request is null)
        {
            reply.Answer("");
            return;
        }

        _ = _ui.BeginInvoke(() =>
        {
            var expiry = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = _settings.PromptWait() };
            var pending = new PendingApproval(request, reply, expiry);
            expiry.Tick += (_, _) => Settle(pending, "");
            _pending.Add(pending);
            expiry.Start();

            // The agent stopped waiting: it was answered in its own window, or it ended.
            reply.HungUp.Register(() => _ui.BeginInvoke(() => Settle(pending, "")));

            Arrived?.Invoke(pending);
            Changed?.Invoke();
        });
    }

    private void OnMessage(AgentHooks.HookMessage message)
    {
        if (message.WantsReply)
        {
            return;
        }

        _ = _ui.BeginInvoke(() =>
        {
            foreach (PendingApproval pending in _pending.Where(p => AgentApprovals.IsSettledBy(p.Reply.Message, message)).ToArray())
            {
                Settle(pending, "");
            }
        });
    }

    private void Settle(PendingApproval pending, string answer)
    {
        pending.Expiry.Stop();
        pending.Reply.Answer(answer);
        if (_pending.Remove(pending))
        {
            Changed?.Invoke();
        }
    }
}
