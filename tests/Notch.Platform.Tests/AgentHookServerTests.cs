using System.IO.Pipes;
using System.Text;
using Notch.Core.Agents;
using Notch.Platform.Agents;

namespace Notch.Platform.Tests;

/// <summary>One server for the whole class: servers on the same pipe name would otherwise catch each other's connections.</summary>
public sealed class HookServerFixture : IDisposable
{
    public AgentHookServer Server { get; } = new();

    public void Dispose() => Server.Dispose();
}

public class AgentHookServerTests(HookServerFixture fixture) : IClassFixture<HookServerFixture>
{
    private static string Line(string session, string eventName, string? agent = null)
    {
        string payload = agent is null
            ? $$"""{"hook_event_name":"{{eventName}}","cwd":"C:\\work\\app"}"""
            : $$"""{"hook_event_name":"{{eventName}}","cwd":"C:\\work\\app","agent":"{{agent}}"}""";
        return AgentHooks.FormatMessage(session, payload, hookPid: 123) + "\n";
    }

    private async Task<AgentHooks.HookMessage> ReceiveAsync(PipeDirection clientDirection, string line)
    {
        var received = new TaskCompletionSource<AgentHooks.HookMessage>();
        Action<AgentHooks.HookMessage> handler = m => received.TrySetResult(m);
        fixture.Server.MessageReceived += handler;
        try
        {
            Send(clientDirection, line);
            return await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            fixture.Server.MessageReceived -= handler;
        }
    }

    private void Send(PipeDirection clientDirection, string line)
    {
        using var client = new NamedPipeClientStream(".", fixture.Server.PipeName, clientDirection);
        client.Connect(timeout: 5000);
        client.Write(Encoding.UTF8.GetBytes(line));
        client.Flush();
    }

    [Fact]
    public async Task The_hook_program_which_only_writes_still_connects()
    {
        AgentHooks.HookMessage message = await ReceiveAsync(PipeDirection.Out, Line("session-1", "Stop"));

        Assert.Equal("session-1", message.Session);
        Assert.Equal("Stop", message.EventName);
        Assert.Null(message.Agent);
    }

    [Fact]
    public async Task A_client_that_opens_the_pipe_for_reading_and_writing_connects_too()
    {
        // opencode's runtime does this; with an inbound-only server pipe it is refused and its events are lost.
        AgentHooks.HookMessage message = await ReceiveAsync(PipeDirection.InOut, Line("", "Notification", agent: "opencode"));

        Assert.Equal("Notification", message.EventName);
        Assert.Equal("opencode", message.Agent);
        Assert.Equal(@"C:\work\app", message.Folder);
        Assert.Equal("", message.Session);
    }

    [Fact]
    public async Task Sessions_notch_started_are_reported_by_session_to_the_terminal()
    {
        var received = new TaskCompletionSource<(string Session, string Event)>();
        Action<string, string> handler = (session, name) => received.TrySetResult((session, name));
        fixture.Server.EventReceived += handler;
        try
        {
            Send(PipeDirection.Out, Line("abc", "UserPromptSubmit"));

            Assert.Equal(("abc", "UserPromptSubmit"), await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            fixture.Server.EventReceived -= handler;
        }
    }
}
