using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Notch.App.Terminal;

/// <summary>
/// Hosts the xterm.js page (Assets/terminal) in a WebView2 and exchanges JSON messages with
/// it. The composition control is used because a classic WebView2 is a child HWND, which
/// cannot draw inside the notch's transparent layered window.
/// </summary>
internal sealed class TerminalBridge
{
    private const string HostName = "terminal.notch";

    private readonly TaskCompletionSource _pageLoaded = new();
    private Task? _initialization;

    public WebView2CompositionControl Control { get; } = new()
    {
        DefaultBackgroundColor = System.Drawing.Color.Black,
        Focusable = true,
    };

    /// <summary>The page created a terminal and measured it: id, columns, rows.</summary>
    public event Action<string, int, int>? Created;

    public event Action<string, int, int>? Resized;

    /// <summary>Keystrokes and pastes, already encoded as terminal input.</summary>
    public event Action<string, string>? Input;

    public event Action<string>? Bell;

    /// <summary>Safe to call repeatedly; the WebView2 is only created once.</summary>
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    public void Create(string id) => Post(new JsonObject { ["type"] = "create", ["id"] = id });

    public void Activate(string id) => Post(new JsonObject { ["type"] = "activate", ["id"] = id });

    public void Close(string id) => Post(new JsonObject { ["type"] = "close", ["id"] = id });

    public void Write(string id, ReadOnlySpan<byte> data) =>
        Post(new JsonObject { ["type"] = "output", ["id"] = id, ["data"] = Convert.ToBase64String(data) });

    public void Focus()
    {
        Control.Focus();
        Post(new JsonObject { ["type"] = "focus" });
    }

    private async Task InitializeCoreAsync()
    {
        string dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notch", "WebView2");
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
        await Control.EnsureCoreWebView2Async(environment);

        CoreWebView2 webView = Control.CoreWebView2;
        webView.Settings.AreDefaultContextMenusEnabled = false;
        webView.Settings.AreDevToolsEnabled = false;
        webView.Settings.IsStatusBarEnabled = false;
        webView.Settings.IsZoomControlEnabled = false;

        // Otherwise Ctrl+R, F5 and friends act on the page instead of reaching the terminal.
        webView.Settings.AreBrowserAcceleratorKeysEnabled = false;

        webView.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
            {
                e.State = CoreWebView2PermissionState.Allow;
            }
        };
        webView.WebMessageReceived += OnMessage;

        // Serve the bundled page from disk under a fake host, so it gets a normal https origin.
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
        webView.SetVirtualHostNameToFolderMapping(HostName, assets, CoreWebView2HostResourceAccessKind.Deny);
        webView.Navigate($"https://{HostName}/index.html");

        await _pageLoaded.Task;
    }

    private void Post(JsonObject message)
    {
        if (Control.CoreWebView2 is { } webView)
        {
            webView.PostWebMessageAsJson(message.ToJsonString());
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        string id = (string?)message?["id"] ?? "";
        switch ((string?)message?["type"])
        {
            case "loaded":
                _pageLoaded.TrySetResult();
                break;
            case "created":
                Created?.Invoke(id, (int)message!["cols"]!, (int)message["rows"]!);
                break;
            case "resize":
                Resized?.Invoke(id, (int)message!["cols"]!, (int)message["rows"]!);
                break;
            case "input":
                Input?.Invoke(id, (string?)message!["data"] ?? "");
                break;
            case "bell":
                Bell?.Invoke(id);
                break;
        }
    }
}
