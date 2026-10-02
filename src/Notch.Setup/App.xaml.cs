using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Notch.Setup;

public partial class App : Application
{
    private const string TempCopyPrefix = "Notch-Uninstall-";
    private string? _tempCopyToDelete;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        Options options = Options.Parse(e.Args);

        if (options.Uninstall)
        {
            Uninstall(options);
            return;
        }

        if (!Installer.HasPayload)
        {
            // Uninstall.exe, started the way an older installer's uninstall entry starts it (no switches):
            // a copy of setup with no app inside has nothing to install, so uninstalling is what it is for.
            Uninstall(options);
            return;
        }

        if (options.Silent)
        {
            Shutdown(InstallSilently(options));
            return;
        }

        MainWindow = new SetupWindow(options, false, "");
        MainWindow.Show();
    }

    private static int InstallSilently(Options options)
    {
        string? existing = Machine.InstalledDir();
        string dir = options.InstallDir ?? existing ?? Machine.DefaultInstallDir;
        var choices = new InstallChoices
        {
            Dir = dir,
            IsUpdate = options.Update,
            StartWithWindows = true,
            // Keeps whatever is there: the PATH is only added to when asked for with /PATH.
            AddToPath = options.AddToPath || Machine.IsOnPath(dir),
        };

        try
        {
            Installer.Install(choices, new Progress<(double, string)>());
            if (options.Update)
            {
                // The app's updater closed Notch for this; put it back on screen.
                Installer.Launch(choices.Dir, true);
            }

            return 0;
        }
        catch (Exception e)
        {
            // Silent, so nobody is looking at a window: write down what happened and, for an update, put
            // the version that was there back on screen. The installer only replaces the old version once
            // the new one is unpacked, so the old one is still intact when this happens.
            Machine.Log("Install failed: " + e);
            Machine.WriteUpdateError(e.Message);
            if (options.Update)
            {
                try
                {
                    Installer.Launch(choices.Dir, false);
                }
                catch (Exception launch)
                {
                    Machine.Log("Could not restart the previous version: " + launch.Message);
                }
            }

            return 1;
        }
    }

    private void Uninstall(Options options)
    {
        string self = Payload.SelfPath;
        string dir = options.InstallDir ?? Machine.InstalledDir() ?? Path.GetDirectoryName(self)!;
        string prefix = Path.GetFullPath(dir).TrimEnd('\\') + "\\";

        // A program cannot delete the folder it runs from, so carry on from a copy in %TEMP%.
        if (self.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string copy = Path.Combine(Path.GetTempPath(), TempCopyPrefix + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(self, copy, true);
            string args = "/uninstall /D=\"" + dir.TrimEnd('\\') + "\"" + (options.Silent ? " /S" : "");
            Process.Start(new ProcessStartInfo(copy, args) { UseShellExecute = false })?.Dispose();
            Shutdown(0);
            return;
        }

        if (Path.GetFileName(self).StartsWith(TempCopyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            _tempCopyToDelete = self;
        }

        if (options.Silent)
        {
            try
            {
                Installer.Uninstall(dir, false, new Progress<(double, string)>());
                Shutdown(0);
            }
            catch (Exception)
            {
                Shutdown(1);
            }

            return;
        }

        MainWindow = new SetupWindow(options, true, dir);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        if (_tempCopyToDelete != null)
        {
            // Wait for this process to end, then remove the copy it ran from.
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + _tempCopyToDelete + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();
        }
    }
}
