using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Notch.Core.Automation;

/// <summary>What the running notch answered to a command sent through <see cref="CommandPipe"/>.</summary>
public sealed record CommandResult(bool Ok, string? Message = null);

/// <summary>How sending a command went.</summary>
/// <param name="Connected">A notch was listening, whether or not it answered.</param>
/// <param name="Result">Its answer; null when none came.</param>
public sealed record PipeExchange(bool Connected, CommandResult? Result);

/// <summary>
/// How a second copy of Notch, a <c>notch://</c> link and the command line tool talk to the notch
/// that is already running: one request line (a JSON array of arguments), one answer line.
/// The pipe belongs to the current Windows user only.
/// </summary>
public sealed class CommandPipe : IDisposable
{
    /// <summary>Debug builds use their own pipe so they can run next to an installed copy.</summary>
#if DEBUG
    public const string DefaultName = "notch-command-debug";
#else
    public const string DefaultName = "notch-command";
#endif

    private const int MaxRequestBytes = 8 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly string _name;
    private readonly Func<string[], Task<CommandResult>> _handler;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Starts listening. <paramref name="handler"/> is called on a background thread for each request.</summary>
    public CommandPipe(Func<string[], Task<CommandResult>> handler, string name = DefaultName)
    {
        _handler = handler;
        _name = name;
        _ = Task.Run(ListenAsync);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>
    /// Sends the arguments to the running notch and returns its answer, or null when none is
    /// listening (or it does not answer in time).
    /// </summary>
    public static async Task<CommandResult?> SendAsync(string[] arguments, TimeSpan timeout, string name = DefaultName) =>
        (await ExchangeAsync(arguments, timeout, timeout, name)).Result;

    /// <summary>
    /// Sends the arguments to the running notch and waits for its answer. Some commands wait on the user (a
    /// plugin's review, say), so once the pipe is connected the answer may take up to <paramref name="answerTimeout"/>.
    /// </summary>
    /// <returns>The answer, or null with <see cref="PipeExchange.Connected"/> telling "not running" from "did not answer".</returns>
    public static async Task<PipeExchange> ExchangeAsync(string[] arguments, TimeSpan connectTimeout, TimeSpan answerTimeout, string name = DefaultName)
    {
        bool connected = false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var connecting = new CancellationTokenSource(connectTimeout))
            {
                await pipe.ConnectAsync(connecting.Token);
            }

            connected = true;
            using var cancel = new CancellationTokenSource(answerTimeout);
            byte[] request = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(arguments) + "\n");
            await pipe.WriteAsync(request, cancel.Token);
            await pipe.FlushAsync(cancel.Token);

            using var reader = new StreamReader(pipe, Encoding.UTF8);
            string? answer = await reader.ReadLineAsync(cancel.Token);
            return new PipeExchange(true, answer is null ? new CommandResult(true) : JsonSerializer.Deserialize<CommandResult>(answer, Json));
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or TimeoutException)
        {
            return new PipeExchange(connected, null);
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task ListenAsync()
    {
        CancellationToken token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _name,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);

                NamedPipeServerStream connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected, token), token);
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

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            using (pipe)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ReadTimeout);

                string? line = await ReadLineAsync(pipe, timeout.Token);
                CommandResult result;
                try
                {
                    string[]? arguments = line is null ? null : JsonSerializer.Deserialize<string[]>(line);
                    result = arguments is null
                        ? new CommandResult(false, "That request could not be read.")
                        : await _handler(arguments);
                }
                catch (JsonException)
                {
                    result = new CommandResult(false, "That request could not be read.");
                }

                byte[] answer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result, Json) + "\n");
                await pipe.WriteAsync(answer, token);
                await pipe.FlushAsync(token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // A caller that disconnects early or stalls is simply dropped.
        }
    }

    /// <summary>Reads up to the first line break, refusing requests larger than <see cref="MaxRequestBytes"/>.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count <= MaxRequestBytes)
        {
            if (await stream.ReadAsync(one, token) == 0 || one[0] == (byte)'\n')
            {
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString([.. bytes]);
            }

            bytes.Add(one[0]);
        }

        return null;
    }
}
