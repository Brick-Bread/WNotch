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
                pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
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
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // A hook that disconnects early or stalls is simply dropped.
        }
    }
}
