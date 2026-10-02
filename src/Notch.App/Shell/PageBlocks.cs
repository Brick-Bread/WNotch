using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Notch.Core.Plugins;

namespace Notch.App.Shell;

/// <summary>
/// Draws a page's <see cref="PluginBlock"/>s into a column. Sending an updated page changes the
/// controls that are already there instead of making new ones, so a text box keeps its caret and
/// what the user typed, and a slider is not snatched out of their hand.
/// </summary>
internal sealed class PageBlocks(Panel host)
{
    private readonly List<Slot> _slots = [];

    /// <summary>True while one of the blocks has keyboard focus or text the user has not submitted.</summary>
    public bool HasTextInput => _slots.Any(s => s.Block is PluginTextField);

    public void Render(IReadOnlyList<PluginBlock> blocks)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            PluginBlock block = blocks[i];
            if (i < _slots.Count && _slots[i].Block.GetType() == block.GetType())
            {
                _slots[i].Update(block);
                _slots[i].Block = block;
                continue;
            }

            Slot slot = Create(block);
            if (i < _slots.Count)
            {
                host.Children.RemoveAt(i);
                host.Children.Insert(i, slot.Element);
                _slots[i] = slot;
            }
            else
            {
                host.Children.Add(slot.Element);
                _slots.Add(slot);
            }
        }

        while (_slots.Count > blocks.Count)
        {
            host.Children.RemoveAt(_slots.Count - 1);
            _slots.RemoveAt(_slots.Count - 1);
        }
    }

    private sealed class Slot(PluginBlock block, FrameworkElement element, Action<PluginBlock> update)
    {
        public PluginBlock Block { get; set; } = block;

        public FrameworkElement Element { get; } = element;

        public Action<PluginBlock> Update { get; } = update;
    }

    private static Brush Themed(string key) => (Brush)Application.Current.FindResource(key);

    private static Brush Tint(Notch.Core.Activities.GlowColor? color, string fallback = "TextBrush") =>
        color is { } c ? ThemeManager.Brush(c) : Themed(fallback);

    private static Slot Create(PluginBlock block) => block switch
    {
        PluginText b => Text(b),
        PluginValueRow b => ValueRow(b),
        PluginProgress b => Progress(b),
        PluginTable b => Table(b),
        PluginChart b => Chart(b),
        PluginButtons b => Buttons(b),
        PluginToggle b => Toggle(b),
        PluginSlider b => SliderBlock(b),
        PluginSelect b => Select(b),
        PluginTextField b => TextField(b),
        PluginImage b => Picture(b),
        PluginSeparator => Separator(),
        _ => new Slot(block, new Border(), _ => { }),
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.6,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static Slot Text(PluginText initial)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        void Apply(PluginBlock block)
        {
            var b = (PluginText)block;
            text.Text = b.Text;
            text.Foreground = Tint(b.Color);
            text.Opacity = b.Style == PluginTextStyle.Muted ? 0.55 : 1;
            text.FontSize = b.Style switch { PluginTextStyle.Heading => 15, PluginTextStyle.Muted => 11, PluginTextStyle.Code => 12, _ => 13 };
            text.FontWeight = b.Style == PluginTextStyle.Heading ? FontWeights.SemiBold : FontWeights.Normal;
            text.SetResourceReference(TextBlock.FontFamilyProperty, b.Style == PluginTextStyle.Code ? "CodeFontFamily" : "UiFontFamily");
        }

        Apply(initial);
        return new Slot(initial, text, Apply);
    }

    private static Slot ValueRow(PluginValueRow initial)
    {
        var label = Caption("");
        var value = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);

        void Apply(PluginBlock block)
        {
            var b = (PluginValueRow)block;
            label.Text = b.Label;
            value.Text = b.Value ?? "";
            value.Foreground = Tint(b.Color);
        }

        Apply(initial);
        return new Slot(initial, grid, Apply);
    }

    private static Slot Progress(PluginProgress initial)
    {
        var label = Caption("");
        var value = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var bar = new ProgressBar { Height = 4, Maximum = 1, BorderThickness = new Thickness(0), Background = Themed("TrackBrush"), Margin = new Thickness(0, 3, 0, 0) };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(value, 1);
        top.Children.Add(label);
        top.Children.Add(value);
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(top);
        stack.Children.Add(bar);

        void Apply(PluginBlock block)
        {
            var b = (PluginProgress)block;
            label.Text = b.Label;
            value.Text = b.Value ?? "";
            value.Foreground = Tint(b.Color);
            bar.Foreground = Tint(b.Color, "AccentBrush");
            bar.Value = Math.Clamp(b.Progress, 0, 1);
        }

        Apply(initial);
        return new Slot(initial, stack, Apply);
    }

    private static Slot Table(PluginTable initial)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };

        void Apply(PluginBlock block)
        {
            var b = (PluginTable)block;
            int columns = Math.Clamp(b.Columns.Count, 1, 8);
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Clear();
            for (int c = 0; c < columns; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            void Cell(int row, int column, string text, bool header)
            {
                TextBlock cell = header ? Caption(text) : new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                cell.Margin = new Thickness(0, 2, 8, 2);
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < columns; c++)
            {
                Cell(0, c, b.Columns[c], header: true);
            }

            int r = 1;
            foreach (IReadOnlyList<string> row in b.Rows.Take(200))
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int c = 0; c < Math.Min(columns, row.Count); c++)
                {
                    Cell(r, c, row[c], header: false);
                }

                r++;
            }
        }

        Apply(initial);
        return new Slot(initial, grid, Apply);
    }

    private static Slot Chart(PluginChart initial)
    {
        var label = Caption("");
        var plot = new ChartPlot();
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(label);
        stack.Children.Add(plot);

        void Apply(PluginBlock block)
        {
            var b = (PluginChart)block;
            label.Text = b.Label ?? "";
            label.Visibility = b.Label is null ? Visibility.Collapsed : Visibility.Visible;
            plot.Height = Math.Clamp(b.Height, 40, 240);
            plot.Stroke = Tint(b.Color, "AccentBrush");
            plot.Set([.. b.Values.Take(240)], b.Max);
        }

        Apply(initial);
        return new Slot(initial, stack, Apply);
    }

    private static Slot Buttons(PluginButtons initial)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        string? armed = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        PluginButtons current = initial;

        void Apply(PluginBlock block)
        {
            current = (PluginButtons)block;
            row.Children.Clear();
            foreach (PluginAction action in current.Actions)
            {
                var button = new Button
                {
                    Style = (Style)Application.Current.FindResource("PillButton"),
                    Content = armed == action.Label ? "Click again" : action.Label,
                    IsEnabled = action.Enabled,
                    Opacity = action.Enabled ? 1 : 0.4,
                    ToolTip = action.Hint,
                    Margin = new Thickness(0, 0, 6, 6),
                };
                if (action.Color is { } color)
                {
                    button.Foreground = ThemeManager.Brush(color);
                }

                button.Click += (_, _) =>
                {
                    if (action.Confirm && armed != action.Label)
                    {
                        armed = action.Label;
                        timer.Stop();
                        timer.Start();
                        Apply(current);
                        return;
                    }

                    timer.Stop();
                    armed = null;
                    Apply(current);
                    action.Clicked?.Invoke();
                };
                row.Children.Add(button);
            }
        }

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            armed = null;
            Apply(current);
        };
        Apply(initial);
        return new Slot(initial, row, Apply);
    }

    private static Slot Toggle(PluginToggle initial)
    {
        PluginToggle current = initial;
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = Caption("");
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(label);
        texts.Children.Add(detail);

        var knob = new Ellipse { Width = 14, Height = 14, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var track = new Border { Width = 38, Height = 20, CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Child = knob, VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(track, 1);
        grid.Children.Add(texts);
        grid.Children.Add(track);

        bool on = initial.Value;
        void Paint()
        {
            track.Background = on ? Themed("AccentBrush") : Themed("ControlBrush");
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            knob.Margin = new Thickness(3, 0, 3, 0);
        }

        void Apply(PluginBlock block)
        {
            current = (PluginToggle)block;
            label.Text = current.Label;
            detail.Text = current.Detail ?? "";
            detail.Visibility = current.Detail is null ? Visibility.Collapsed : Visibility.Visible;
            on = current.Value;
            Paint();
        }

        track.MouseLeftButtonUp += (_, _) =>
        {
            on = !on;
            Paint();
            current.Changed?.Invoke(on);
        };
        Apply(initial);
        return new Slot(initial, grid, Apply);
    }

    private static Slot SliderBlock(PluginSlider initial)
    {
        PluginSlider current = initial;
        var label = Caption("");
        var value = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(value, 1);
        top.Children.Add(label);
        top.Children.Add(value);

        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Themed("TrackBrush"), VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Themed("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var thumb = new Ellipse { Width = 14, Height = 14, Fill = Themed("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var hit = new Grid { Height = 22, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 2, 0, 0) };
        hit.Children.Add(track);
        hit.Children.Add(fill);
        hit.Children.Add(thumb);

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(top);
        stack.Children.Add(hit);

        double shown = initial.Value;
        bool dragging = false;

        double Snap(double v)
        {
            v = Math.Clamp(v, current.Min, current.Max);
            return current.Step > 0 ? Math.Clamp(current.Min + Math.Round((v - current.Min) / current.Step) * current.Step, current.Min, current.Max) : v;
        }

        void Paint()
        {
            double range = Math.Max(current.Max - current.Min, double.Epsilon);
            double fraction = Math.Clamp((shown - current.Min) / range, 0, 1);
            double width = Math.Max(hit.ActualWidth - thumb.Width, 0);
            fill.Width = (width * fraction) + (thumb.Width / 2);
            thumb.Margin = new Thickness(width * fraction, 0, 0, 0);
            value.Text = shown.ToString(current.Step is > 0 and < 1 ? "0.##" : "0", System.Globalization.CultureInfo.InvariantCulture) + current.Unit;
        }

        void Move(MouseEventArgs e)
        {
            double width = Math.Max(hit.ActualWidth - thumb.Width, 1);
            double x = Math.Clamp(e.GetPosition(hit).X - (thumb.Width / 2), 0, width);
            shown = Snap(current.Min + ((current.Max - current.Min) * x / width));
            Paint();
        }

        hit.SizeChanged += (_, _) => Paint();
        hit.MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
            hit.CaptureMouse();
            Move(e);
        };
        hit.MouseMove += (_, e) =>
        {
            if (dragging)
            {
                Move(e);
            }
        };
        hit.MouseLeftButtonUp += (_, _) =>
        {
            if (!dragging)
            {
                return;
            }

            dragging = false;
            hit.ReleaseMouseCapture();
            current.Changed?.Invoke(shown);
        };

        void Apply(PluginBlock block)
        {
            current = (PluginSlider)block;
            label.Text = current.Label;
            if (!dragging)
            {
                shown = Snap(current.Value);
            }

            Paint();
        }

        Apply(initial);
        return new Slot(initial, stack, Apply);
    }

    private static Slot Select(PluginSelect initial)
    {
        PluginSelect current = initial;
        var label = Caption("");
        var options = new WrapPanel { Margin = new Thickness(-6, 2, 0, 0) };
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(label);
        stack.Children.Add(options);
        string? selected = initial.Selected;

        void Paint()
        {
            options.Children.Clear();
            foreach (string option in current.Options)
            {
                var button = new Button
                {
                    Style = (Style)Application.Current.FindResource("PillButton"),
                    Content = option,
                    Margin = new Thickness(6, 0, 0, 6),
                };
                if (option == selected)
                {
                    button.Background = Themed("AccentPressedBrush");
                }

                button.Click += (_, _) =>
                {
                    selected = option;
                    Paint();
                    current.Changed?.Invoke(option);
                };
                options.Children.Add(button);
            }
        }

        void Apply(PluginBlock block)
        {
            current = (PluginSelect)block;
            label.Text = current.Label;
            selected = current.Selected;
            Paint();
        }

        Apply(initial);
        return new Slot(initial, stack, Apply);
    }

    private static Slot TextField(PluginTextField initial)
    {
        PluginTextField current = initial;
        var label = Caption("");
        var hint = new TextBlock { IsHitTestVisible = false, Opacity = 0.4, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var plain = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Themed("TextBrush"), CaretBrush = Themed("TextBrush"), SelectionBrush = Themed("AccentBrush"), Padding = new Thickness(10, 5, 10, 5), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
        var secret = new PasswordBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Themed("TextBrush"), CaretBrush = Themed("TextBrush"), SelectionBrush = Themed("AccentBrush"), Padding = new Thickness(10, 5, 10, 5), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
        var box = new Border { Background = Themed("ControlBrush"), BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent };
        box.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        var inner = new Grid();
        inner.Children.Add(hint);
        inner.Children.Add(plain);
        inner.Children.Add(secret);
        box.Child = inner;
        var submit = new Button { Style = (Style)Application.Current.FindResource("PillButton"), Margin = new Thickness(8, 0, 0, 0), Background = Themed("AccentHoverBrush") };
        var row = new DockPanel();
        DockPanel.SetDock(submit, Dock.Right);
        row.Children.Add(submit);
        row.Children.Add(box);
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(label);
        stack.Children.Add(row);

        string Text() => current.Secret ? secret.Password : plain.Text;
        void UpdateHint() => hint.Visibility = Text().Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        void Send()
        {
            string text = Text();
            current.Submitted?.Invoke(text);
            if (current.Secret)
            {
                secret.Clear();
            }

            UpdateHint();
        }

        plain.TextChanged += (_, _) => UpdateHint();
        secret.PasswordChanged += (_, _) => UpdateHint();
        submit.Click += (_, _) => Send();
        foreach (Control control in new Control[] { plain, secret })
        {
            control.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    Send();
                }
            };
            control.GotKeyboardFocus += (_, _) => box.BorderBrush = Themed("AccentBrush");
            control.LostKeyboardFocus += (_, _) => box.BorderBrush = Brushes.Transparent;
        }

        void Apply(PluginBlock block)
        {
            current = (PluginTextField)block;
            label.Text = current.Label;
            hint.Text = current.Hint ?? "";
            submit.Content = current.SubmitLabel ?? "Save";
            plain.Visibility = current.Secret ? Visibility.Collapsed : Visibility.Visible;
            secret.Visibility = current.Secret ? Visibility.Visible : Visibility.Collapsed;

            // Leave alone what the user is typing; only take the plugin's text when they are not in the box.
            if (!current.Secret && !plain.IsKeyboardFocusWithin && current.Value is { } value && plain.Text != value)
            {
                plain.Text = value;
            }

            UpdateHint();
        }

        Apply(initial);
        return new Slot(initial, stack, Apply);
    }

    private static Slot Picture(PluginImage initial)
    {
        var image = new Image { HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 10) };
        byte[]? shown = null;

        void Apply(PluginBlock block)
        {
            var b = (PluginImage)block;
            image.Height = Math.Clamp(b.Height, 20, 400);
            if (ReferenceEquals(shown, b.Data))
            {
                return;
            }

            shown = b.Data;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(b.Data);
                bitmap.EndInit();
                bitmap.Freeze();
                image.Source = bitmap;
            }
            catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException)
            {
                // Not a picture WPF can read; show nothing rather than fail the page.
                image.Source = null;
            }
        }

        Apply(initial);
        return new Slot(initial, image, Apply);
    }

    private static Slot Separator() => new(
        new PluginSeparator(),
        new Border { Height = 1, Background = Themed("TextBrush"), Opacity = 0.12, Margin = new Thickness(0, 4, 0, 12) },
        _ => { });

    /// <summary>A filled line chart of the values it was given.</summary>
    private sealed class ChartPlot : FrameworkElement
    {
        private double[] _values = [];
        private double _max = 1;
        private Brush _stroke = Brushes.White;

        public Brush Stroke
        {
            get => _stroke;
            set
            {
                _stroke = value;
                InvalidateVisual();
            }
        }

        public void Set(double[] values, double? max)
        {
            _values = values;
            _max = Math.Max(max ?? values.DefaultIfEmpty(1).Max(), 1e-9);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext context)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            context.DrawRoundedRectangle(Themed("CardBrush"), null, new Rect(0, 0, width, height), 8, 8);
            if (_values.Length < 2 || width <= 0 || height <= 0)
            {
                return;
            }

            const double pad = 6;
            Point At(int i) => new(
                pad + ((width - (2 * pad)) * i / (_values.Length - 1)),
                height - pad - ((height - (2 * pad)) * Math.Clamp(_values[i] / _max, 0, 1)));

            var line = new StreamGeometry();
            using (StreamGeometryContext g = line.Open())
            {
                g.BeginFigure(At(0), isFilled: false, isClosed: false);
                for (int i = 1; i < _values.Length; i++)
                {
                    g.LineTo(At(i), isStroked: true, isSmoothJoin: false);
                }
            }

            var area = new StreamGeometry();
            using (StreamGeometryContext g = area.Open())
            {
                g.BeginFigure(new Point(At(0).X, height - pad), isFilled: true, isClosed: true);
                for (int i = 0; i < _values.Length; i++)
                {
                    g.LineTo(At(i), isStroked: false, isSmoothJoin: false);
                }

                g.LineTo(new Point(At(_values.Length - 1).X, height - pad), isStroked: false, isSmoothJoin: false);
            }

            line.Freeze();
            area.Freeze();
            Brush wash = _stroke.Clone();
            wash.Opacity = 0.18;
            context.DrawGeometry(wash, null, area);
            context.DrawGeometry(null, new Pen(_stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
        }
    }
}
