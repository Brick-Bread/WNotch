using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notch.Core.Activities;
using Notch.Core.Clipboard;
using Notch.Platform.Clipboard;

namespace Notch.App.Shell;

// The Clipboard tab: what was copied lately, to copy again. Off until the user turns it on in settings.
public partial class NotchWindow
{
    private const double ClipboardTabHeight = 430;
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeFormat = "CanIncludeInClipboardHistory";

    private readonly ClipboardHistory _clipboard = new();
    private readonly string _clipboardPinsFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch", "clipboard-pins.json");

    private bool _clipboardRegistered;
    private uint _ownClipboardChange;

    private void InitializeClipboard()
    {
        _clipboard.Changed += () => Dispatcher.BeginInvoke(UpdateClipboardList);
        ClipboardSearch.TextChanged += (_, _) =>
        {
            ClipboardSearchHint.Visibility = ClipboardSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateClipboardList();
        };
        ClipboardClear.Click += (_, _) =>
        {
            _clipboard.Clear();
            SaveClipboardPins();
        };
        TabClipboard.Checked += (_, _) => SelectTab(NotchTab.Clipboard);
    }

    /// <summary>Starts or stops keeping the history to match the setting. Called when the window is made and after settings are saved.</summary>
    private void ApplyClipboardSetting()
    {
        bool want = _settings.ClipboardHistory;
        TabClipboard.Visibility = want ? Visibility.Visible : Visibility.Collapsed;

        if (want && !_clipboardRegistered && _hwnd != 0)
        {
            _clipboardRegistered = ClipboardListener.Register(_hwnd);
            _clipboard.Restore(ClipboardPins.Load(_clipboardPinsFile));
        }
        else if (!want && _clipboardRegistered)
        {
            ClipboardListener.Unregister(_hwnd);
            _clipboardRegistered = false;

            // Switching it off forgets it all, pins included, and takes the file with it.
            _clipboard.Clear(includePinned: true);
            ClipboardPins.Save(_clipboardPinsFile, []);
            if (_tab == NotchTab.Clipboard)
            {
                TabHome.IsChecked = true;
            }
        }
    }

    /// <summary>Forgets the whole history, pins included.</summary>
    public void ClearClipboardHistory()
    {
        _clipboard.Clear(includePinned: true);
        ClipboardPins.Save(_clipboardPinsFile, []);
    }

    private void OnClipboardUpdate()
    {
        if (!_clipboardRegistered || ClipboardListener.SequenceNumber == _ownClipboardChange)
        {
            return;
        }

        string? source = ClipboardListener.OwnerProgram();

        // The program that copied may still hold the clipboard open a moment; try again shortly.
        _ = ReadClipboardAsync(source);
    }

    private async Task ReadClipboardAsync(string? source)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                ReadClipboardOnce(source);
                return;
            }
            catch (COMException)
            {
                await Task.Delay(50 * (attempt + 1));
            }
        }
    }

    private void ReadClipboardOnce(string? source)
    {
        IDataObject? data = Clipboard.GetDataObject();
        if (data is null)
        {
            return;
        }

        bool excluded = data.GetDataPresent(ExcludeFormat);
        int? canInclude = null;
        if (data.GetDataPresent(CanIncludeFormat) && data.GetData(CanIncludeFormat) is MemoryStream stream && stream.Length >= 4)
        {
            canInclude = BitConverter.ToInt32(stream.ToArray(), 0);
        }

        if (ClipboardPrivacy.ShouldSkip(excluded, canInclude, source))
        {
            return;
        }

        DateTime now = DateTime.Now;
        if (Clipboard.ContainsFileDropList())
        {
            _clipboard.AddFiles(Clipboard.GetFileDropList().Cast<string>(), now, source);
        }
        else if (Clipboard.ContainsImage())
        {
            if (Clipboard.GetImage() is { } image)
            {
                _clipboard.AddImage(EncodePng(image), now, source);
            }
        }
        else if (Clipboard.ContainsText())
        {
            _clipboard.AddText(Clipboard.GetText(), now, source);
        }
    }

    private static byte[]? EncodePng(BitmapSource image)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return buffer.ToArray();
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private void UpdateClipboardList()
    {
        string? filter = ClipboardSearch.Text;
        ClipboardItem[] items = [.. _clipboard.Snapshot(filter)];
        ClipboardList.ItemsSource = items.Select(item => new ClipboardRow(item)).ToList();
        ClipboardEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipboardEmpty.Text = string.IsNullOrWhiteSpace(filter) ? "Things you copy will show up here" : "Nothing matches";
    }

    private void SaveClipboardPins() => ClipboardPins.Save(_clipboardPinsFile, _clipboard.Snapshot());

    private void OnClipboardRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipboardRow row })
        {
            e.Handled = true;
            CopyAgain(row.Item);
        }
    }

    private void OnClipboardPinClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipboardRow row })
        {
            e.Handled = true;
            _clipboard.SetPinned(row.Item.Id, !row.Item.Pinned);
            SaveClipboardPins();
        }
    }

    private void OnClipboardDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipboardRow row })
        {
            e.Handled = true;
            _clipboard.Remove(row.Item.Id);
            SaveClipboardPins();
        }
    }

    private void OnClipboardShelfClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipboardRow { Item.Files: { } files } })
        {
            e.Handled = true;
            _shelf.Add(files);
        }
    }

    /// <summary>Puts an item back on the clipboard, without it being listed as a new copy.</summary>
    private void CopyAgain(ClipboardItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case ClipboardKind.Text:
                    Clipboard.SetText(item.Text!);
                    break;
                case ClipboardKind.Files:
                    var list = new System.Collections.Specialized.StringCollection();
                    list.AddRange([.. item.Files!]);
                    Clipboard.SetFileDropList(list);
                    break;
                default:
                    var picture = new BitmapImage();
                    picture.BeginInit();
                    picture.StreamSource = new MemoryStream(item.Image!);
                    picture.CacheOption = BitmapCacheOption.OnLoad;
                    picture.EndInit();
                    Clipboard.SetImage(picture);
                    break;
            }

            _ownClipboardChange = ClipboardListener.SequenceNumber;
            _activities.Publish(new Activity
            {
                Id = "notice.clipboard",
                Tier = ActivityTier.Transient,
                Title = "Copied again",
                Detail = item.Preview,
                Glyph = "",
                Lifetime = TimeSpan.FromSeconds(1.5),
            });
        }
        catch (COMException)
        {
            // Another program has the clipboard open; clicking again will work.
        }
    }

    /// <summary>One row of the clipboard list.</summary>
    private sealed class ClipboardRow(ClipboardItem item)
    {
        public ClipboardItem Item { get; } = item;

        public string Preview => Item.Preview;

        public string Subtitle => (Item.Source is { Length: > 0 } source ? source + "  ·  " : "") + When(Item.CopiedAt);

        public string Glyph => Item.Kind switch
        {
            ClipboardKind.Image => "",
            ClipboardKind.Files => "",
            _ => "",
        };

        public ImageSource? Thumbnail
        {
            get
            {
                if (Item.Kind != ClipboardKind.Image || Item.Image is null)
                {
                    return null;
                }

                try
                {
                    var picture = new BitmapImage();
                    picture.BeginInit();
                    picture.StreamSource = new MemoryStream(Item.Image);
                    picture.DecodePixelWidth = 64;
                    picture.CacheOption = BitmapCacheOption.OnLoad;
                    picture.EndInit();
                    picture.Freeze();
                    return picture;
                }
                catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException)
                {
                    return null;
                }
            }
        }

        public string PinGlyph => Item.Pinned ? "" : "";

        public string PinTip => Item.Pinned ? "Unpin" : "Pin: keep it, even after Notch restarts";

        public Visibility ShelfVisibility => Item.Kind == ClipboardKind.Files ? Visibility.Visible : Visibility.Collapsed;

        private static string When(DateTime time)
        {
            TimeSpan age = DateTime.Now - time;
            return age.TotalMinutes < 1 ? "just now"
                : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
                : time.Date == DateTime.Today ? time.ToString("t")
                : time.ToString("g");
        }
    }
}
