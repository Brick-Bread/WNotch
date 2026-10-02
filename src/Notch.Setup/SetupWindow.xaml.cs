using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Notch.Setup;

public partial class SetupWindow : Window
{
    private enum Step { Welcome, Working, Done, Failed }

    private readonly bool _uninstall;
    private readonly bool _isUpdate;
    private string _dir;
    private Step _step;

    internal SetupWindow(Options options, bool uninstall, string dir)
    {
        InitializeComponent();
        _uninstall = uninstall;
        _dir = dir;

        if (uninstall)
        {
            ShowUninstallWelcome();
            return;
        }

        string? existing = Machine.InstalledDir();
        _isUpdate = existing != null;
        _dir = options.InstallDir ?? existing ?? Machine.DefaultInstallDir;
        DirText.Text = _dir;
        string version = Installer.Version;
        if (_isUpdate)
        {
            string? old = Machine.InstalledVersion();
            TitleText.Text = "Update Notch";
            Subtitle.Text = string.IsNullOrEmpty(old) || old == version
                ? "Notch " + version + " will replace the copy that is installed."
                : "Version " + old + " is installed. This updates it to " + version + ".";
            PrimaryButton.Content = "Update";
            StartupCheck.IsChecked = Machine.StartsWithWindows();
            // Updates go in place; moving an install is a reinstall.
            ChangeButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            Subtitle.Text = "A dynamic notch for Windows 11. Version " + version + ".";
        }

        Note.Text = "Installs for your account only. No admin rights needed.";
    }

    private void ShowUninstallWelcome()
    {
        TitleText.Text = "Uninstall Notch";
        Subtitle.Text = "This removes Notch from this computer.";
        OptionsPanel.Visibility = Visibility.Collapsed;
        UninstallPanel.Visibility = Visibility.Visible;
        PrimaryButton.Content = "Uninstall";
        Note.Text = "";
    }

    private void SetStep(Step step)
    {
        _step = step;
        OptionsPanel.Visibility = step == Step.Welcome && !_uninstall ? Visibility.Visible : Visibility.Collapsed;
        UninstallPanel.Visibility = step == Step.Welcome && _uninstall ? Visibility.Visible : Visibility.Collapsed;
        WorkPanel.Visibility = step == Step.Working ? Visibility.Visible : Visibility.Collapsed;
        DonePanel.Visibility = step == Step.Done && !_uninstall ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = step == Step.Failed ? Visibility.Visible : Visibility.Collapsed;

        SecondaryButton.Visibility = step == Step.Welcome ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsEnabled = step != Step.Working;
        if (step != Step.Welcome)
        {
            Note.Text = "";
        }
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Welcome:
                _ = RunAsync();
                break;
            case Step.Done:
                if (!_uninstall && LaunchCheck.IsChecked == true)
                {
                    try
                    {
                        Installer.Launch(_dir, false);
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                    }
                }

                Close();
                break;
            case Step.Failed:
                Close();
                break;
        }
    }

    private void OnSecondary(object sender, RoutedEventArgs e) => Close();

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_step != Step.Working)
        {
            Close();
        }
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async Task RunAsync()
    {
        SetStep(Step.Working);
        TitleText.Text = _uninstall ? "Uninstalling…" : _isUpdate ? "Updating…" : "Installing…";
        Subtitle.Text = "";
        Bar.Value = 0;

        var progress = new Progress<(double Fraction, string Status)>(p =>
        {
            Bar.Value = p.Fraction;
            Status.Text = p.Status;
        });

        string dir = _dir;
        bool startup = StartupCheck.IsChecked == true;
        bool desktop = DesktopCheck.IsChecked == true;
        bool removeSettings = SettingsCheck.IsChecked == true;
        bool update = _isUpdate;
        bool uninstall = _uninstall;

        try
        {
            await Task.Run(() =>
            {
                if (uninstall)
                {
                    Installer.Uninstall(dir, removeSettings, progress);
                }
                else
                {
                    Installer.Install(new InstallChoices { Dir = dir, StartWithWindows = startup, DesktopShortcut = desktop, IsUpdate = update }, progress);
                }
            });
        }
        catch (Exception ex)
        {
            TitleText.Text = uninstall ? "Could not uninstall" : "Could not install";
            Subtitle.Text = "";
            ErrorText.Text = ex.Message;
            PrimaryButton.Content = "Close";
            SetStep(Step.Failed);
            return;
        }

        if (uninstall)
        {
            TitleText.Text = "Notch was removed";
            Subtitle.Text = removeSettings ? "Your settings were deleted too." : "Your settings were kept in case you come back.";
        }
        else
        {
            TitleText.Text = update ? "Notch is up to date" : "Notch is ready";
            Subtitle.Text = "Look for the pill at the top of your screen. Right-click its tray icon for settings.";
        }

        PrimaryButton.Content = uninstall ? "Close" : "Finish";
        SetStep(Step.Done);
    }

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where to install Notch",
            SelectedPath = Directory.Exists(_dir) ? _dir : Path.GetDirectoryName(_dir) ?? "",
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle)) != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        string chosen = dialog.SelectedPath.TrimEnd('\\');
        // Notch gets a folder of its own, so uninstalling never touches anything else.
        if (!string.Equals(Path.GetFileName(chosen), "Notch", StringComparison.OrdinalIgnoreCase))
        {
            chosen = Path.Combine(chosen, "Notch");
        }

        _dir = chosen;
        DirText.Text = _dir;
    }

    private sealed class DialogOwner : System.Windows.Forms.IWin32Window
    {
        public DialogOwner(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }
}
