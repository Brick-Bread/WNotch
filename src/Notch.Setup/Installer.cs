using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

namespace Notch.Setup;

internal sealed class InstallChoices
{
    public string Dir { get; set; } = Machine.DefaultInstallDir;
    public bool StartWithWindows { get; set; } = true;
    public bool DesktopShortcut { get; set; }
    /// <summary>Put the install folder on the user's PATH so <c>notchctl</c> works in any terminal.</summary>
    public bool AddToPath { get; set; }
    public bool IsUpdate { get; set; }
}

internal static class Installer
{
    /// <summary>The version of the app this setup carries, e.g. 0.8.1 or 0.9.0-beta.</summary>
    public static string Version
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string version = info?.InformationalVersion ?? "0.0.0";
            int plus = version.IndexOf('+');
            return plus >= 0 ? version.Substring(0, plus) : version;
        }
    }

    public static bool HasPayload => Payload.TryLocate(out _, out _);

    public static void Install(InstallChoices choices, IProgress<(double Fraction, string Status)> progress)
    {
        if (!Payload.TryLocate(out long programLength, out long zipLength))
        {
            throw new InvalidOperationException("This setup file is incomplete. Download it again.");
        }

        string dir = Path.GetFullPath(choices.Dir);

        progress.Report((0, "Closing Notch…"));
        Machine.StopApp(dir);

        // Start from a clean folder so files a newer version no longer ships do not linger.
        if (File.Exists(Path.Combine(dir, Machine.AppExe)))
        {
            progress.Report((0.02, "Removing the previous version…"));
            Machine.DeleteFolder(dir);
        }

        Directory.CreateDirectory(dir);

        using (FileStream self = Payload.OpenSelf())
        using (var slice = new Payload.Slice(self, programLength, zipLength))
        using (var zip = new ZipArchive(slice, ZipArchiveMode.Read))
        {
            Extract(zip, dir, progress);
        }

        progress.Report((0.95, "Finishing up…"));
        WriteUninstaller(dir, programLength);

        string exe = Path.Combine(dir, Machine.AppExe);
        Machine.CreateShortcuts(exe, choices.DesktopShortcut);
        if (!choices.IsUpdate)
        {
            // An update must not override whether the user wants Notch to start with Windows.
            Machine.SetStartWithWindows(choices.StartWithWindows, exe);
        }

        Machine.SetOnPath(dir, choices.AddToPath);
        Machine.RegisterInstall(dir, Version, FolderSize(dir));
        progress.Report((1, "Done"));
    }

    private static void Extract(ZipArchive zip, string dir, IProgress<(double Fraction, string Status)> progress)
    {
        string root = dir.TrimEnd('\\') + "\\";
        long total = Math.Max(1, zip.Entries.Sum(e => e.Length));
        long done = 0;
        byte[] buffer = new byte[128 * 1024];

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(dir, entry.FullName.Replace('/', '\\')));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The installer contains an unexpected path.");
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream input = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                done += read;
                progress.Report((0.05 + 0.9 * done / total, "Copying files…"));
            }
        }
    }

    /// <summary>Uninstall.exe is this program without the app attached.</summary>
    private static void WriteUninstaller(string dir, long programLength)
    {
        using FileStream self = Payload.OpenSelf();
        using var output = new FileStream(Path.Combine(dir, "Uninstall.exe"), FileMode.Create, FileAccess.Write);
        byte[] buffer = new byte[64 * 1024];
        long remaining = programLength;
        while (remaining > 0)
        {
            int read = self.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static long FolderSize(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    public static void Launch(string dir, bool updated)
    {
        string exe = Path.Combine(dir, Machine.AppExe);
        if (File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo(exe, updated ? "--updated" : "") { WorkingDirectory = dir, UseShellExecute = false })?.Dispose();
        }
    }

    public static void Uninstall(string dir, bool removeSettings, IProgress<(double Fraction, string Status)> progress)
    {
        progress.Report((0, "Closing Notch…"));
        Machine.StopApp(dir);

        progress.Report((0.3, "Removing shortcuts…"));
        Machine.RemoveShortcuts(dir);
        Machine.SetOnPath(dir, false);
        Machine.Unregister();

        progress.Report((0.5, "Removing files…"));
        // Only remove the folder if it really is a Notch install.
        if (File.Exists(Path.Combine(dir, Machine.AppExe)))
        {
            Machine.DeleteFolder(dir);
        }

        if (removeSettings)
        {
            progress.Report((0.9, "Removing settings…"));
            Machine.DeleteFolder(Machine.SettingsDir);
        }

        progress.Report((1, "Done"));
    }
}
