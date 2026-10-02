using System.Diagnostics;
using Notch.Cli;
using Notch.Core.Automation;

// notchctl: tells the running Notch what to do.
//   notchctl timer 5m          notchctl toast "Build done" --color green
//   notchctl install <plugin>  notchctl open stats        notchctl webhook
// Exit code 0 when Notch did it, 1 when it refused, 2 when it could not be reached.

string[] arguments = args;
bool start = arguments.Contains("--start", StringComparer.OrdinalIgnoreCase);
arguments = [.. arguments.Where(a => !a.Equals("--start", StringComparison.OrdinalIgnoreCase))];

if (arguments.Length == 0 || arguments[0] is "-h" or "--help" or "help" or "/?")
{
    Console.WriteLine("""
        notchctl <command> [options]

          timer 5m | stop | pause | resume      start or control the timer
          toast "Title" [--detail t] [--glyph g] [--color green|#rrggbb] [--lifetime 5s]
          open home|terminal|stats|shelf|plugins|clipboard
          install <plugin-id>                   ask to install a plugin from the registry
          plugin <plugin-id> --enable true|false
          palette | settings
          webhook                               show the webhook address and token

          --start    start Notch first if it is not running
        """);
    return arguments.Length == 0 ? 1 : 0;
}

if (arguments[0].Equals("webhook", StringComparison.OrdinalIgnoreCase))
{
    return WebhookInfo.Print();
}

// Checked here too so a typo gets its explanation even when Notch is not running.
if (!CommandParser.TryParseArguments(arguments, out _, out string? problem))
{
    Console.Error.WriteLine(problem);
    return 1;
}

CommandResult? result = await CommandPipe.SendAsync(arguments, TimeSpan.FromSeconds(3));
if (result is null && start && StartNotch())
{
    for (int attempt = 0; attempt < 40 && result is null; attempt++)
    {
        await Task.Delay(250);
        result = await CommandPipe.SendAsync(arguments, TimeSpan.FromSeconds(1));
    }
}

if (result is null)
{
    Console.Error.WriteLine("Notch is not running. Start it, or use --start.");
    return 2;
}

if (!string.IsNullOrEmpty(result.Message))
{
    (result.Ok ? Console.Out : Console.Error).WriteLine(result.Message);
}

return result.Ok ? 0 : 1;

static bool StartNotch()
{
    // Notch.exe sits next to this tool.
    string path = Path.Combine(AppContext.BaseDirectory, "Notch.exe");
    if (!File.Exists(path))
    {
        return false;
    }

    try
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        return true;
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return false;
    }
}
