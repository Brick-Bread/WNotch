using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private const string LinkKey = @"Software\Classes\notch";

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

        RegisterLinkProtocol(Path.Combine(dir, AppExe));

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

    /// <summary>
    /// Makes notch:// links open Notch (per user, so no administrator is needed). The plugin list on
    /// the website uses them for its Install buttons.
    /// </summary>
    private static void RegisterLinkProtocol(string exePath)
    {
        using RegistryKey scheme = Registry.CurrentUser.CreateSubKey(LinkKey);
        scheme.SetValue("", "URL:Notch");
        scheme.SetValue("URL Protocol", "");
        using (RegistryKey icon = scheme.CreateSubKey("DefaultIcon"))
        {
            icon.SetValue("", "\"" + exePath + "\",0");
        }

        using RegistryKey command = scheme.CreateSubKey(@"shell\open\command");
        command.SetValue("", "\"" + exePath + "\" \"%1\"");
    }

    private const string EnvironmentKey = "Environment";

    /// <summary>Whether <c>notchctl.exe</c>'s folder is on the user's PATH.</summary>
    public static bool IsOnPath(string dir)
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(EnvironmentKey))
        {
            string raw = ReadPath(key);
            return raw.Split(';').Any(segment => SamePath(segment, dir));
        }
    }

    /// <summary>
    /// Adds or removes the install folder on the user's PATH, so <c>notchctl</c> works in any terminal.
    /// Does nothing when the PATH already is as wanted, and otherwise changes only that one entry:
    /// the rest of the value, its empty entries and its registry type (a PATH that uses %VARIABLES%
    /// is stored as an expandable string) are left exactly as they were.
    /// </summary>
    public static void SetOnPath(string dir, bool enabled)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(EnvironmentKey))
        {
            string raw = ReadPath(key);
            string[] segments = raw.Split(';');
            bool present = segments.Any(segment => SamePath(segment, dir));
            if (present == enabled)
            {
                return;
            }

            string next = enabled
                ? (raw.Length == 0 ? dir : raw.TrimEnd(';') + ";" + dir)
                : string.Join(";", segments.Where(segment => !SamePath(segment, dir)));

            RegistryValueKind kind = key.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase)
                ? key.GetValueKind("Path")
                : RegistryValueKind.ExpandString;
            key.SetValue("Path", next, kind == RegistryValueKind.ExpandString || next.Contains("%") ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        }

        BroadcastEnvironmentChange();
    }

    private static string ReadPath(RegistryKey key) =>
        key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";

    private static bool SamePath(string segment, string dir)
    {
        string a = Environment.ExpandEnvironmentVariables(segment.Trim().Trim('"')).TrimEnd('\\');
        return a.Length > 0 && string.Equals(a, dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    /// <summary>
    /// Tells running programs the environment changed, so new terminals see the new PATH. Bounded: a
    /// window that does not answer must not hold the installer up.
    /// </summary>
    private static void BroadcastEnvironmentChange()
    {
        var thread = new Thread(() =>
        {
            const uint WM_SETTINGCHANGE = 0x001A;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            SendMessageTimeout((IntPtr)0xFFFF, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 500, out _);
        }) { IsBackground = true };
        thread.Start();
        thread.Join(3000);
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(LinkKey, false);

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

    /// <summary>
    /// A running copy keeps its files locked, so stop the programs that live in <paramref name="dir"/>.
    /// With <paramref name="graceful"/> (an update, where the app is already shutting itself down) they
    /// get a few seconds to exit on their own before they are ended.
    /// </summary>
    public static void StopApp(string dir, bool graceful = false)
    {
        string prefix = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        foreach (string name in new[] { "Notch", "Notch.Hook", "notchctl" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (graceful && process.WaitForExit(4000))
                        {
                            continue;
                        }

                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
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

    // ---- A record of what setup did, and what the app should tell the user afterwards ----

    private static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string LogFile => Path.Combine(DataFolder, "setup.log");

    /// <summary>Set when an update failed; the app shows it the next time it starts, then deletes it.</summary>
    public static string UpdateErrorFile => Path.Combine(DataFolder, "update-error.txt");

    /// <summary>Appends a line to setup.log. Never fails: a log must not be the reason an install does not finish.</summary>
    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            FileInfo existing = new FileInfo(LogFile);
            if (existing.Exists && existing.Length > 256 * 1024)
            {
                File.Copy(LogFile, LogFile + ".old", true);
                File.Delete(LogFile);
            }

            File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Runs one optional part of an install; if it fails, logs it and carries on.</summary>
    public static void Attempt(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log("Could not set up " + what + ": " + e.Message);
        }
    }

    public static void WriteUpdateError(string message)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(UpdateErrorFile, message);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
        }
    }

    public static void ClearUpdateError() => TryDelete(UpdateErrorFile);

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
