using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace Notch.Setup;

/// <summary>What setup touches outside the install folder: registry, shortcuts and running processes.</summary>
internal static class Machine
{
    public const string AppName = "Notch";
    public const string AppExe = "Notch.exe";

    // The same keys the NSIS installer used, so a new install takes over an old one cleanly.
    private const string AppKey = @"Software\Notch";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Notch";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    private static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    private static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    public static string? InstalledDir()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AppKey);
        string? dir = key?.GetValue("InstallDir") as string;
        return !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, AppExe)) ? dir : null;
    }

    public static string? InstalledVersion()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("DisplayVersion") as string;
    }

    public static bool StartsWithWindows()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is string { Length: > 0 };
    }

    public static void SetStartWithWindows(bool enabled, string exePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(AppName, "\"" + exePath + "\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    public static void RegisterInstall(string dir, string version, long sizeBytes)
    {
        using (RegistryKey app = Registry.CurrentUser.CreateSubKey(AppKey))
        {
            app.SetValue("InstallDir", dir);
        }

        string uninstaller = Path.Combine(dir, "Uninstall.exe");
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Notch contributors");
        key.SetValue("DisplayIcon", Path.Combine(dir, AppExe));
        key.SetValue("InstallLocation", dir);
        key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
        key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /S");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeBytes / 1024), RegistryValueKind.DWord);
    }

    public static void Unregister()
    {
        using (RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            run?.DeleteValue(AppName, false);
        }

        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(AppKey, false);
    }

    public static void CreateShortcuts(string exePath, bool desktop)
    {
        MakeShortcut(StartMenuShortcut, exePath);
        if (desktop)
        {
            MakeShortcut(DesktopShortcut, exePath);
        }
    }

    public static void RemoveShortcuts(string dir)
    {
        TryDelete(StartMenuShortcut);
        // The desktop one is optional; only remove it if it is ours.
        string exe = Path.Combine(dir, AppExe);
        if (File.Exists(DesktopShortcut) && string.Equals(ShortcutTarget(DesktopShortcut), exe, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(DesktopShortcut);
        }
    }

    private static void MakeShortcut(string path, string target)
    {
        Type? type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null)
        {
            return;
        }

        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        try
        {
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = "Notch";
            link.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string? ShortcutTarget(string path)
    {
        Type? type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null)
        {
            return null;
        }

        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        try
        {
            return link.TargetPath as string;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>A running copy keeps its files locked, so stop the ones that live in <paramref name="dir"/>.</summary>
    public static void StopApp(string dir)
    {
        string prefix = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        foreach (string name in new[] { "Notch", "Notch.Hook" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone, or not ours to inspect.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    /// <summary>Deletes a folder, retrying briefly because a process that just exited can still hold files.</summary>
    public static void DeleteFolder(string dir)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 15)
                {
                    throw;
                }

                Thread.Sleep(300);
            }
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
