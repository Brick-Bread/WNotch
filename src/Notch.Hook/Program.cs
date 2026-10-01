using System.IO.Pipes;
using System.Text;
using Notch.Core.Agents;

// Run by the Claude and Codex CLIs when something happens in a session that Notch started.
// It forwards the event to the app and must never get in the CLI's way: it prints nothing
// (Claude adds hook output to the conversation) and always exits 0.
try
{
    string? pipeName = Environment.GetEnvironmentVariable(AgentHooks.PipeVariable);
    string? session = Environment.GetEnvironmentVariable(AgentHooks.SessionVariable);
    if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(session))
    {
        return 0;
    }

    string payload = ReadPayload(args);
    if (payload.Length == 0)
    {
        return 0;
    }

    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
    pipe.Connect(timeout: 1000);
    byte[] line = Encoding.UTF8.GetBytes(AgentHooks.FormatMessage(session, payload) + "\n");
    pipe.Write(line);
    pipe.Flush();
}
catch (Exception)
{
    // The app is gone or busy; the CLI carries on regardless.
}

return 0;

static string ReadPayload(string[] args)
{
    // Codex passes its notification as the last argument; Claude writes the event to stdin.
    if (args.Length > 0 && args[^1].TrimStart().StartsWith('{'))
    {
        return args[^1];
    }

    if (!Console.IsInputRedirected)
    {
        return "";
    }

    Task<string> read = Task.Run(() =>
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        return reader.ReadToEnd();
    });

    return read.Wait(TimeSpan.FromSeconds(2)) ? read.Result.Trim() : "";
}
