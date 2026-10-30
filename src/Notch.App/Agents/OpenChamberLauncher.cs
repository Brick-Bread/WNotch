using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Win32;
using Notch.Core.Agents;

namespace Notch.App.Agents;

/// <summary>
/// Opens a folder in OpenChamber. OpenChamber has no command line or link for a folder, so Notch asks the
/// desktop app's own local server (the one OpenChamber's command line tool talks to) for a new session in
/// the folder and opens that session's link. Starts the app first when it is not running.
/// </summary>
internal static class OpenChamberLauncher
{
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(25);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static string SettingsPath => OpenChamber.SettingsPath(
        Environment.GetEnvironmentVariable("OPENCHAMBER_DATA_DIR"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Whether OpenChamber is installed: its links are registered, or it has left its settings.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using RegistryKey? links = Registry.CurrentUser.OpenSubKey(@"Software\Classes\openchamber");
            return links is not null || File.Exists(SettingsPath);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a new OpenChamber session in <paramref name="folder"/>. False when OpenChamber came up but the
    /// session could not be made there, so the user picks the folder in OpenChamber themselves.
    /// </summary>
    public static async Task<bool> OpenFolderAsync(string folder)
    {
        (int Port, string? Token)? server = await RunningServerAsync();
        if (server is null)
        {
            Open(OpenChamber.FocusLink);
            DateTime giveUp = DateTime.UtcNow + StartWait;
            while (server is null && DateTime.UtcNow < giveUp)
            {
                await Task.Delay(500);
                server = await RunningServerAsync();
            }
        }

        if (server is { } running && await NewSessionAsync(running.Port, running.Token, folder) is { } id)
        {
            Open(OpenChamber.SessionLink(id));
            return true;
        }

        Open(OpenChamber.FocusLink);
        return false;
    }

    /// <summary>The desktop app's server, when it answers.</summary>
    private static async Task<(int Port, string? Token)?> RunningServerAsync()
    {
        string? settings;
        try
        {
            settings = File.Exists(SettingsPath) ? await File.ReadAllTextAsync(SettingsPath) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (OpenChamber.ReadServer(settings) is not { } server)
        {
            return null;
        }

        try
        {
            using var check = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
            using HttpResponseMessage response = await Http.GetAsync($"http://127.0.0.1:{server.Port}/health", check.Token);
            return response.IsSuccessStatusCode ? server : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> NewSessionAsync(int port, string? token, string folder)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/openchamber/control")
            {
                Content = new StringContent(OpenChamber.NewSessionRequest(folder), Encoding.UTF8, "application/json"),
            };
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using HttpResponseMessage response = await Http.SendAsync(request);
            return response.IsSuccessStatusCode ? OpenChamber.ReadSessionId(await response.Content.ReadAsStringAsync()) : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    private static void Open(string link)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // OpenChamber is not installed after all; the caller says so.
        }
    }
}
