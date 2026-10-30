using System.IO.Pipes;
using System.Text;
using Notch.Core.Agents;

namespace Notch.Platform.Agents;

/// <summary>
/// Listens on named pipes for the events Notch.Hook.exe forwards from agent CLIs. One pipe is
/// private to this process, for sessions Notch's own terminal started; the other is shared by
/// every Notch of this user, for agents that were started anywhere else.
/// </summary>
public sealed class AgentHookServer : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    public AgentHookServer()
    {
        // Unique per process so two copies of the app (e.g. a dev build) never cross wires.
        PipeName = $"notch-agent-{Environment.ProcessId}";
        _ = Task.Run(() => ListenAsync(PipeName));
        _ = Task.Run(() => ListenAsync(AgentHooks.SharedPipeName));
    }

    public string PipeName { get; }

    /// <summary>Raised on a background thread with the session id and the event name, for sessions Notch's terminal started.</summary>
    public event Action<string, string>? EventReceived;

    /// <summary>Raised on a background thread for every event, including those from agents Notch did not start.</summary>
    public event Action<AgentHooks.HookMessage>? MessageReceived;

    /// <summary>
    /// Raised on a background thread, after <see cref="MessageReceived"/>, for an event whose hook waits for an
    /// answer. Whoever handles it answers through the <see cref="HookReply"/>; with no handler the hook gets none at once.
    /// </summary>
    public event Action<HookReply>? ReplyRequested;

    /// <summary>The longest a hook is kept waiting for an answer, whatever its handler does.</summary>
    public static readonly TimeSpan MaxReplyWait = TimeSpan.FromMinutes(15);

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task ListenAsync(string pipeName)
    {
        CancellationToken token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // Duplex: a hook that asks a question reads its answer back on the same connection, and opencode's
                // runtime opens the pipe for reading and writing anyway. Hooks that only write still connect.
                pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(token);

                // Hand the connection off so a slow client cannot hold up the next one.
                NamedPipeServerStream connected = pipe;
                pipe = null;
                _ = Task.Run(() => ReadAsync(connected, token), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                await Task.Delay(250, CancellationToken.None);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task ReadAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, Encoding.UTF8))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                string? line = await reader.ReadLineAsync(timeout.Token);
                if (line is not null && line.Length <= 256 * 1024 && AgentHooks.TryParseHookMessage(line, out AgentHooks.HookMessage? message))
                {
                    MessageReceived?.Invoke(message!);
                    if (message!.Session.Length > 0)
                    {
                        EventReceived?.Invoke(message.Session, message.EventName);
                    }

                    if (message.WantsReply)
                    {
                        await ReplyAsync(pipe, reader, message, token);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A hook that disconnects early or stalls is simply dropped.
        }
    }

    /// <summary>
    /// Holds the connection open until the hook's question is answered, then writes the answer as one line.
    /// A hook that hangs up first (its agent ended, or stopped waiting) cancels the question.
    /// </summary>
    private async Task ReplyAsync(NamedPipeServerStream pipe, StreamReader reader, AgentHooks.HookMessage message, CancellationToken token)
    {
        using var hungUp = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reply = new HookReply(message, hungUp.Token);
        if (ReplyRequested is { } handler)
        {
            handler(reply);
        }
        else
        {
            reply.Answer("");
        }

        // A hook sends nothing after its line, so a read that ends means it hung up.
        Task<string?> closed = reader.ReadLineAsync(hungUp.Token).AsTask();
        _ = closed.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        Task finished = await Task.WhenAny(reply.Answered, closed, Task.Delay(MaxReplyWait, hungUp.Token));
        if (finished != reply.Answered)
        {
            await hungUp.CancelAsync();
            reply.Answer("");
            return;
        }

        string answer = (await reply.Answered).ReplaceLineEndings(" ");
        byte[] bytes = Encoding.UTF8.GetBytes(answer + "\n");
        await pipe.WriteAsync(bytes, token);
        await pipe.FlushAsync(token);
        await hungUp.CancelAsync();
    }
}

/// <summary>A hook waiting on its connection for an answer. Answer it once; later answers are ignored.</summary>
public sealed class HookReply
{
    private readonly TaskCompletionSource<string> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal HookReply(AgentHooks.HookMessage message, CancellationToken hungUp)
    {
        Message = message;
        HungUp = hungUp;
    }

    public AgentHooks.HookMessage Message { get; }

    /// <summary>Cancelled when the hook stops waiting: it hung up, or was answered.</summary>
    public CancellationToken HungUp { get; }

    internal Task<string> Answered => _answer.Task;

    /// <summary>Sends <paramref name="text"/> back to the hook as one line; empty means no answer.</summary>
    /// <returns>False when the hook had its answer already, or had gone.</returns>
    public bool Answer(string text) => _answer.TrySetResult(text);
}
