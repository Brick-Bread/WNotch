using System.Net;
using System.Text;
using System.Text.Json;
using Notch.Core.Automation;

namespace Notch.Platform.Automation;

/// <summary>
/// The local webhook: an HTTP listener on this computer's loopback address only. It moves bytes;
/// who may call and what a request may say is decided by <see cref="WebhookRequest"/>.
/// </summary>
public sealed class WebhookServer : IDisposable
{
    private const int MaxRequestsPerSecond = 30;

    private readonly Func<WebhookAction, Task<CommandResult>> _handler;
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _stop;
    private string _token = "";
    private int _port;

    // A fixed window is enough to stop a runaway script or a guessing attack from hammering the app.
    private DateTime _windowStart = DateTime.UtcNow;
    private int _windowCount;

    public WebhookServer(Func<WebhookAction, Task<CommandResult>> handler) => _handler = handler;

    public bool IsRunning => _listener is { IsListening: true };

    /// <summary>Starts listening, replacing any earlier settings. Returns null, or why it could not.</summary>
    public string? Start(int port, string token)
    {
        Stop();
        if (port is < 1024 or > 65535)
        {
            return "The webhook port must be between 1024 and 65535.";
        }

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
        }
        catch (Exception e) when (e is HttpListenerException or InvalidOperationException)
        {
            listener.Close();
            return $"The webhook could not listen on port {port}: {e.Message}";
        }

        _token = token;
        _port = port;
        _listener = listener;
        _stop = new CancellationTokenSource();
        _ = Task.Run(() => AcceptAsync(listener, _stop.Token));
        return null;
    }

    public void Stop()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }

        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            _ = Task.Run(() => ServeAsync(context));
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            HttpListenerRequest request = context.Request;

            if (TooMany())
            {
                await Reply(context, 429, "Too many requests.");
                return;
            }

            WebhookRefusal? refusal = WebhookRequest.Authorize(
                request.Headers["Host"],
                request.Headers["Origin"],
                request.Headers["Authorization"],
                _token,
                _port);
            if (refusal is not null)
            {
                await Reply(context, refusal.Status, refusal.Message);
                return;
            }

            string? body = null;
            if (request.HasEntityBody)
            {
                if (request.ContentLength64 > WebhookRequest.MaxBodyBytes)
                {
                    await Reply(context, 413, "The body is too large.");
                    return;
                }

                body = await ReadBodyAsync(request);
                if (body is null)
                {
                    await Reply(context, 413, "The body is too large.");
                    return;
                }
            }

            string path = request.Url?.PathAndQuery ?? "/";
            refusal = WebhookRequest.Route(request.HttpMethod, path, body, out WebhookAction? action);
            if (refusal is not null || action is null)
            {
                await Reply(context, refusal?.Status ?? 400, refusal?.Message ?? "Bad request.");
                return;
            }

            CommandResult result = await _handler(action);
            await Reply(context, result.Ok ? 200 : 422, result.Message ?? (result.Ok ? "ok" : "Not done."), result.Ok);
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The caller went away.
        }
        catch (Exception)
        {
            try
            {
                await Reply(context, 500, "Something went wrong.");
            }
            catch (Exception)
            {
                // Nothing more can be said to this caller.
            }
        }
    }

    private bool TooMany()
    {
        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _windowStart >= TimeSpan.FromSeconds(1))
            {
                _windowStart = now;
                _windowCount = 0;
            }

            return ++_windowCount > MaxRequestsPerSecond;
        }
    }

    /// <summary>The body, or null when it grows past the limit (a caller may lie about its length).</summary>
    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > WebhookRequest.MaxBodyBytes)
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task Reply(HttpListenerContext context, int status, string message, bool? ok = null)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { ok = ok ?? status < 300, message });
        HttpListenerResponse response = context.Response;
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        // No CORS headers on purpose: a web page must not be able to read or send anything here.
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }
}
