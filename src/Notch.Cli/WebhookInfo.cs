using Notch.Core.Automation;
using Notch.Core.Settings;

namespace Notch.Cli;

/// <summary><c>notchctl webhook</c>: where the webhook listens and the token to send, read from the settings file.</summary>
internal static class WebhookInfo
{
    public static int Print()
    {
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch", "settings.json");
        AppSettings settings = new SettingsStore(file).Load();

        if (!settings.WebhookEnabled)
        {
            Console.WriteLine("The webhook is off. Turn it on in Settings > Automation.");
            return 1;
        }

        Console.WriteLine($"URL:   http://127.0.0.1:{settings.WebhookPort}/v1/");
        Console.WriteLine($"Token: {settings.WebhookToken}");
        Console.WriteLine();
        Console.WriteLine("curl -H \"Authorization: Bearer <token>\" -d '{\"id\":\"build\",\"title\":\"Build done\"}' "
            + $"http://127.0.0.1:{settings.WebhookPort}/v1/activity");
        return 0;
    }
}
