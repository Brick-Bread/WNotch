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
            // Uninstall.exe started without /uninstall: it has nothing to install.
            MessageBox.Show("This is the Notch uninstaller. Run Notch-Setup to install Notch.", "Notch Setup",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
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
        var choices = new InstallChoices
        {
            Dir = options.InstallDir ?? existing ?? Machine.DefaultInstallDir,
            IsUpdate = options.Update,
            StartWithWindows = true,
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
        catch (Exception)
        {
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
