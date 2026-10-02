using System;
using System.IO;

namespace Notch.Setup;

/// <summary>
/// Command line: <c>/S</c> silent, <c>/UPDATE</c> (with /S, from the app's updater) leaves the
/// start-with-Windows choice alone and restarts the app afterwards, <c>/D=path</c> picks the folder,
/// <c>/uninstall</c> removes Notch. Same switches as the NSIS installer this replaced.
/// </summary>
internal sealed class Options
{
    public bool Silent { get; private set; }
    public bool Update { get; private set; }
    public bool Uninstall { get; private set; }
    public bool AddToPath { get; private set; }
    public string? InstallDir { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (Is(arg, "/S") || Is(arg, "--silent"))
            {
                options.Silent = true;
            }
            else if (Is(arg, "/UPDATE"))
            {
                options.Update = true;
            }
            else if (Is(arg, "/PATH"))
            {
                options.AddToPath = true;
            }
            else if (Is(arg, "/uninstall") || Is(arg, "--uninstall"))
            {
                options.Uninstall = true;
            }
            else if (arg.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
            {
                options.InstallDir = arg.Substring(3).Trim('"');
            }
            else if (Is(arg, "/D") && i + 1 < args.Length)
            {
                options.InstallDir = args[++i].Trim('"');
            }
        }

        if (!string.IsNullOrWhiteSpace(options.InstallDir))
        {
            options.InstallDir = Path.GetFullPath(options.InstallDir);
        }
        else
        {
            options.InstallDir = null;
        }

        return options;
    }

    private static bool Is(string arg, string name) => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase);
}
