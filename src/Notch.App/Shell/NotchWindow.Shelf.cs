using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Notch.Core.Shelf;
using Notch.Platform.Shell;

namespace Notch.App.Shell;

// The Shelf tab: files dragged onto the notch, kept as paths, to drag out again.
public partial class NotchWindow
{
    private const int ShelfColumns = 6;
    private const int MaxVisibleShelfRows = 3;

    // A tile is 92 high plus a 6 margin on each side.
    private const double ShelfRowHeight = 104;

    // Twice the size a tile shows its picture at, so it stays sharp on a scaled display.
    private const int ShelfPictureSize = 88;

    private const string FileGlyph = "";
    private const string FolderGlyph = "";

    /// <summary>Pictures already fetched, by path; null for a file the shell has no picture of.</summary>
    private readonly Dictionary<string, ImageSource?> _shelfPictures = new(StringComparer.OrdinalIgnoreCase);

    private int _shelfCount;

    /// <summary>The tile the left button went down on and where, until it is released or becomes a drag.</summary>
    private (ShelfTile Tile, Point Point)? _shelfPress;

    /// <summary>Set while a tile is being dragged out; the pointer is then elsewhere and the notch must wait for it.</summary>
    private bool _dragOutActive;

    /// <summary>Set while a tile's menu is showing, which reaches outside the island.</summary>
    private bool _shelfMenuOpen;

    /// <summary>Leaves files that do not exist on the shelf (<c>--demo</c>, whose shelf is made up).</summary>
    internal bool ShelfKeepsMissingFiles { get; set; }

    /// <summary>Tall enough for the rows of tiles there are, up to the point where the tab scrolls instead.</summary>
    private double ShelfTabHeight
    {
        get
        {
            int rows = (_shelfCount + ShelfColumns - 1) / ShelfColumns;
            return 100 + (Math.Clamp(rows, 1, MaxVisibleShelfRows) * ShelfRowHeight);
        }
    }

    private void InitializeShelf()
    {
        _shelf.Changed += OnShelfChanged;
        ShelfClear.Click += (_, _) => _shelf.Clear();

        // Anywhere on the notch takes a drop, the terminal and text boxes included, which handle drags of their own.
        foreach (FrameworkElement surface in new FrameworkElement[] { Island, EdgeBridge })
        {
            surface.AllowDrop = true;
            surface.AddHandler(DragDrop.DragEnterEvent, new DragEventHandler(OnDraggedOver), handledEventsToo: true);
            surface.AddHandler(DragDrop.DragOverEvent, new DragEventHandler(OnDraggedOver), handledEventsToo: true);
            surface.AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler(OnDragLeft), handledEventsToo: true);
            surface.AddHandler(DragDrop.DropEvent, new DragEventHandler(OnDropped), handledEventsToo: true);
        }

        UpdateShelf();
    }

    private void OnShelfChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(UpdateShelf);

    private void UpdateShelf()
    {
        IReadOnlyList<string> paths = _shelf.Snapshot();
        foreach (string gone in _shelfPictures.Keys.Except(paths, StringComparer.OrdinalIgnoreCase).ToList())
        {
            _shelfPictures.Remove(gone);
        }

        ShelfTile[] tiles = [.. paths.Select(path => new ShelfTile(path, _shelfPictures.GetValueOrDefault(path)))];
        ShelfItems.ItemsSource = tiles;
        _shelfCount = tiles.Length;

        TabShelf.Content = tiles.Length > 0 ? $"Shelf {tiles.Length}" : "Shelf";
        ShelfSummary.Text = tiles.Length switch
        {
            0 => "",
            1 => "1 item · drag it out to use it",
            _ => $"{tiles.Length} items · drag one out to use it",
        };
        ShelfEmpty.Visibility = tiles.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfClear.Visibility = tiles.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        // One at a time and after the tiles are drawn: a thumbnail Windows has not cached yet takes a moment.
        foreach (ShelfTile tile in tiles.Where(tile => !_shelfPictures.ContainsKey(tile.Path)))
        {
            Dispatcher.BeginInvoke(() => LoadShelfPicture(tile), DispatcherPriority.Background);
        }

        if (_tab == NotchTab.Shelf)
        {
            Refresh();
        }
    }

    private void LoadShelfPicture(ShelfTile tile)
    {
        if (!_shelfPictures.TryGetValue(tile.Path, out ImageSource? picture))
        {
            picture = null;
            try
            {
                if (FileThumbnails.For(tile.Path, ShelfPictureSize) is { } thumbnail)
                {
                    var bitmap = BitmapSource.Create(
                        thumbnail.Width, thumbnail.Height, 96, 96, PixelFormats.Pbgra32, null, thumbnail.Pixels, thumbnail.Width * 4);
                    bitmap.Freeze();
                    picture = bitmap;
                }
            }
            catch (Exception)
            {
                // The tile keeps its glyph.
            }

            _shelfPictures[tile.Path] = picture;
        }

        tile.Picture = picture;
    }

    /// <summary>Takes files that are gone off the shelf: moved by a drag out, or deleted since they were put there.</summary>
    private void PruneShelf()
    {
        if (ShelfKeepsMissingFiles)
        {
            return;
        }

        // Off the UI thread: a path on a network share that is away can take seconds to answer.
        FileShelf shelf = _shelf;
        Task.Run(() => shelf.Prune(path => File.Exists(path) || Directory.Exists(path)));
    }

    private bool IsShelfDrag(DragEventArgs e) => !_dragOutActive && e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnDraggedOver(object sender, DragEventArgs e)
    {
        if (!IsShelfDrag(e))
        {
            // Text dragged into a text box is that box's business; anything else is not wanted here.
            if (!e.Handled)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }

            return;
        }

        e.Effects = ShelfEffect(e);
        e.Handled = true;
        if (!_settings.ShelfOpensOnDrag)
        {
            return;
        }

        if (_tab != NotchTab.Shelf)
        {
            TabShelf.IsChecked = true;
        }

        // Drag-over repeats while the pointer rests; only the first one starts the timer.
        bool openPending = _hoverTimer.IsEnabled && _hoverWantsExpanded;
        if (!_expanded && !openPending)
        {
            ScheduleExpanded(true, OpenDelay);
        }
    }

    private void OnDragLeft(object sender, DragEventArgs e)
    {
        if (!_dragOutActive)
        {
            OnPointerLeft();
        }
    }

    private void OnDropped(object sender, DragEventArgs e)
    {
        if (!IsShelfDrag(e) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        e.Effects = ShelfEffect(e);
        e.Handled = true;
        _shelf.Add(paths);
        if (!_expanded)
        {
            _activities.Publish(ShelfActivities.Added(paths.Length));
        }
    }

    /// <summary>The shelf only notes where the files are, so never "move": that would have the source delete them.</summary>
    private static DragDropEffects ShelfEffect(DragEventArgs e) =>
        e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
        : e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
        : DragDropEffects.None;

    private void OnShelfTilePressed(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ShelfTile tile)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _shelfPress = null;
            StartFromShelf(tile.Path);
        }
        else
        {
            _shelfPress = (tile, e.GetPosition(this));
        }
    }

    private void OnShelfTileMoved(object sender, MouseEventArgs e)
    {
        if (_shelfPress is not { } press)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _shelfPress = null;
            return;
        }

        Vector moved = e.GetPosition(this) - press.Point;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _shelfPress = null;
        var data = new DataObject(DataFormats.FileDrop, new[] { press.Tile.Path });
        _dragOutActive = true;
        try
        {
            // Returns when the file has been dropped somewhere or the drag was called off.
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _dragOutActive = false;
        }

        PruneShelf();
        if (!PointerOverIsland())
        {
            ScheduleExpanded(false, CloseDelay);
        }
    }

    private void OnShelfTileMenu(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ShelfTile tile)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        Add("Open", () => StartFromShelf(tile.Path));
        Add("Show in folder", () => ShowInFolder(tile.Path));
        menu.Items.Add(new Separator());
        Add("Take off the shelf", () => _shelf.Remove(tile.Path));

        menu.Closed += (_, _) =>
        {
            _shelfMenuOpen = false;
            if (!PointerOverIsland())
            {
                ScheduleExpanded(false, CloseDelay);
            }
        };
        _shelfMenuOpen = true;
        menu.IsOpen = true;
        e.Handled = true;

        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void OnShelfRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ShelfTile tile)
        {
            _shelf.Remove(tile.Path);
        }
    }

    /// <summary>Opens a shelved file with the app Windows has for it, or a shelved folder in Explorer.</summary>
    private void StartFromShelf(string path) => Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private void ShowInFolder(string path)
    {
        var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        explorer.ArgumentList.Add("/select," + path);
        Start(explorer);
    }

    private void Start(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // Most likely the file has gone since it was shelved.
            PruneShelf();
        }
    }

    private sealed class ShelfTile(string path, ImageSource? picture) : INotifyPropertyChanged
    {
        private ImageSource? _picture = picture;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Path { get; } = path;

        public string Name { get; } =
            System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;

        /// <summary>What stands in for the picture: a folder for a name without an extension, a document otherwise.</summary>
        public string Glyph { get; } = System.IO.Path.HasExtension(path) ? FileGlyph : FolderGlyph;

        public ImageSource? Picture
        {
            get => _picture;
            set
            {
                if (!ReferenceEquals(_picture, value))
                {
                    _picture = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlyphVisibility)));
                }
            }
        }

        public Visibility GlyphVisibility => _picture is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
