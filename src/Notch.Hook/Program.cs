using System.IO.Pipes;
using System.Text;
using Notch.Core.Agents;

// Run by the Claude and Codex CLIs when something happens in a session. It forwards the event to
// the app and must never get in the CLI's way: it prints nothing (Claude adds hook output to the
// conversation) and always exits 0.
//
// The one exception is Claude's PermissionRequest: the app may show the prompt in the notch, so the
// hook waits on the pipe for the answer and prints it, which is how Claude takes a hook's decision.
// Claude shows its own prompt in the terminal at the same time, and whichever is answered first wins.
// When the app answers nothing (the user did not, or the notch does not take prompts), nothing is
// printed and Claude carries on with its own prompt.
//
// A session Notch's own terminal started names its pipe and session in the environment. An agent
// started anywhere else (a hook in the user's own settings) does not, so the event goes to the
// pipe every Notch listens on, tagged with this program's process id so the app can tell which
// agent it came from.
try
{
    string? pipeName = Environment.GetEnvironmentVariable(AgentHooks.PipeVariable);
    string? session = Environment.GetEnvironmentVariable(AgentHooks.SessionVariable);
    if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(session))
    {
        pipeName = AgentHooks.SharedPipeName;
        session = "";
    }

    string payload = ReadPayload(args);
    if (payload.Length == 0)
    {
        return 0;
    }

    bool wantsReply = AgentHooks.WantsReply(payload);
    using var pipe = new NamedPipeClientStream(".", pipeName, wantsReply ? PipeDirection.InOut : PipeDirection.Out);
    pipe.Connect(timeout: 1000);
    byte[] line = Encoding.UTF8.GetBytes(AgentHooks.FormatMessage(session, payload, Environment.ProcessId, wantsReply) + "\n");
    pipe.Write(line);
    pipe.Flush();

    if (wantsReply)
    {
        // No time limit of its own: the app gives up when the user does not answer, and Claude stops a
        // hook that runs longer than it allows.
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        string? answer = reader.ReadLine();
        if (!string.IsNullOrWhiteSpace(answer))
        {
            using Stream output = Console.OpenStandardOutput();
            output.Write(Encoding.UTF8.GetBytes(answer.Trim()));
        }
    }
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
