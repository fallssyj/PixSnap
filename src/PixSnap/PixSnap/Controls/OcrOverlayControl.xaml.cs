using PixSnap.Models;
using PixSnap.Services;
using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixSnap.Controls;

public partial class OcrOverlayControl : UserControl
{
    private INotifyCollectionChanged? _regionsSubscription;
    private Border? _focusedBox;
    private TextBox? _focusedTextBox;

    public static readonly DependencyProperty RegionsProperty =
        DependencyProperty.Register(nameof(Regions), typeof(IEnumerable), typeof(OcrOverlayControl),
            new PropertyMetadata(null, OnVisualDataChanged));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(OcrOverlayControl),
            new PropertyMetadata(false, OnVisualDataChanged));

    public static readonly DependencyProperty ImageSourceProperty =
        DependencyProperty.Register(nameof(ImageSource), typeof(BitmapSource), typeof(OcrOverlayControl),
            new PropertyMetadata(null, OnVisualDataChanged));

    public IEnumerable? Regions
    {
        get => (IEnumerable?)GetValue(RegionsProperty);
        set => SetValue(RegionsProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public BitmapSource? ImageSource
    {
        get => (BitmapSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public OcrOverlayControl()
    {
        InitializeComponent();
        Unloaded += (_, _) => UnsubscribeRegions();
    }

    private static void OnVisualDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not OcrOverlayControl control)
            return;

        if (e.Property == RegionsProperty)
        {
            control.UnsubscribeRegions();
            control.SubscribeRegions();
        }

        control.RebuildOverlay();
    }

    private void SubscribeRegions()
    {
        if (Regions is INotifyCollectionChanged incc)
        {
            _regionsSubscription = incc;
            incc.CollectionChanged += OnRegionsCollectionChanged;
        }
    }

    private void UnsubscribeRegions()
    {
        if (_regionsSubscription is not null)
        {
            _regionsSubscription.CollectionChanged -= OnRegionsCollectionChanged;
            _regionsSubscription = null;
        }
    }

    private void OnRegionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildOverlay();

    private void RebuildOverlay()
    {
        OcrCanvas.Children.Clear();
        _focusedBox = null;
        _focusedTextBox = null;
        IsHitTestVisible = IsActive;
        OcrCanvas.IsHitTestVisible = IsActive;

        if (!IsActive || ImageSource is null || Regions is null)
            return;

        OcrCanvas.Width = ImageSource.Width;
        OcrCanvas.Height = ImageSource.Height;

        var accent = GetAccentBrush();
        foreach (OcrTextRegion region in Regions)
        {
            if (string.IsNullOrWhiteSpace(region.Text))
                continue;

            var bounds = PixelToCanvas(region.PixelBounds);
            if (bounds.Width < 2 || bounds.Height < 2)
                continue;

            double width = Math.Max(bounds.Width, 8);
            double height = Math.Max(bounds.Height, 8);
            double fontSize = ComputeFontSize(region.Text, width, height);

            var box = new Border
            {
                Width = width,
                Height = height,
                Background = HighlightFillBrush,
                BorderBrush = accent,
                BorderThickness = NormalBorderThickness,
                CornerRadius = new CornerRadius(2),
                ToolTip = CreateTooltip(region.Text)
            };

            var textBox = new TextBox
            {
                Text = region.Text,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Brushes.Transparent,
                CaretBrush = Brushes.White,
                SelectionBrush = SelectionBrush,
                SelectionOpacity = 0.45,
                FontSize = fontSize,
                Padding = new Thickness(1, 0, 1, 0),
                Width = width,
                Height = height,
                TextWrapping = TextWrapping.NoWrap,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.IBeam,
                Focusable = true
            };

            textBox.ContextMenu = CreateCopyContextMenu(textBox);
            textBox.GotFocus += (_, _) => SetFocusedRegion(box, textBox, accent);
            textBox.LostFocus += (_, _) =>
            {
                if (_focusedTextBox == textBox)
                    ClearFocusedRegion(accent);
            };

            textBox.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (!textBox.IsKeyboardFocusWithin)
                    textBox.Focus();
                e.Handled = false;
            };

            box.Child = textBox;
            Canvas.SetLeft(box, bounds.X);
            Canvas.SetTop(box, bounds.Y);
            OcrCanvas.Children.Add(box);
        }
    }

    private void SetFocusedRegion(Border box, TextBox textBox, Brush accent)
    {
        if (_focusedBox is not null && _focusedBox != box)
            ResetBoxStyle(_focusedBox, accent);

        _focusedBox = box;
        _focusedTextBox = textBox;
        box.BorderBrush = SelectedBorderBrush;
        box.BorderThickness = SelectedBorderThickness;
        box.Background = SelectedFillBrush;
        textBox.Background = ActiveTextBackgroundBrush;
        textBox.Foreground = Brushes.White;
    }

    private void ClearFocusedRegion(Brush accent)
    {
        if (_focusedBox is not null)
            ResetBoxStyle(_focusedBox, accent);

        if (_focusedTextBox is not null)
        {
            _focusedTextBox.Background = Brushes.Transparent;
            _focusedTextBox.Foreground = Brushes.Transparent;
        }

        _focusedBox = null;
        _focusedTextBox = null;
    }

    private static void ResetBoxStyle(Border box, Brush accent)
    {
        box.BorderBrush = accent;
        box.BorderThickness = NormalBorderThickness;
        box.Background = HighlightFillBrush;
    }

    private static double ComputeFontSize(string text, double width, double height)
    {
        if (string.IsNullOrEmpty(text))
            return 12;

        double byHeight = height * 0.88;
        double byWidth = width / (text.Length * 0.58);
        return Math.Clamp(Math.Min(byHeight, byWidth), 8, 36);
    }

    private static ToolTip CreateTooltip(string text) =>
        new()
        {
            Content = text,
            MaxWidth = 480,
            Padding = new Thickness(8, 6, 8, 6)
        };

    private static ContextMenu CreateCopyContextMenu(TextBox textBox)
    {
        var menu = new ContextMenu();

        var copy = new MenuItem { Header = "复制", InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) => CopyFromTextBox(textBox);
        menu.Items.Add(copy);

        var selectAll = new MenuItem { Header = "全选", InputGestureText = "Ctrl+A" };
        selectAll.Click += (_, _) => textBox.SelectAll();
        menu.Items.Add(selectAll);

        return menu;
    }

    private static void CopyFromTextBox(TextBox textBox)
    {
        string text = !string.IsNullOrEmpty(textBox.SelectedText) ? textBox.SelectedText : textBox.Text;
        if (!string.IsNullOrEmpty(text))
            ClipboardHelper.TrySetText(text);
    }

    private Rect PixelToCanvas(Rect pixelBounds)
    {
        if (ImageSource is null)
            return pixelBounds;

        double sx = ImageSource.Width / ImageSource.PixelWidth;
        double sy = ImageSource.Height / ImageSource.PixelHeight;
        return new Rect(
            pixelBounds.X * sx,
            pixelBounds.Y * sy,
            pixelBounds.Width * sx,
            pixelBounds.Height * sy);
    }

    private static Brush GetAccentBrush()
        => Application.Current.TryFindResource("SystemControlForegroundAccentBrush") as Brush
           ?? Brushes.DodgerBlue;

    private static readonly SolidColorBrush HighlightFillBrush =
        new(Color.FromArgb(48, 0, 120, 215));

    private static readonly SolidColorBrush SelectedFillBrush =
        new(Color.FromArgb(72, 0, 120, 215));

    private static readonly SolidColorBrush SelectedBorderBrush =
        new(Color.FromArgb(255, 0, 99, 177));

    private static readonly SolidColorBrush ActiveTextBackgroundBrush =
        new(Color.FromArgb(210, 0, 0, 0));

    private static readonly SolidColorBrush SelectionBrush =
        new(Color.FromArgb(180, 0, 120, 215));

    private static readonly Thickness NormalBorderThickness = new(1.5);
    private static readonly Thickness SelectedBorderThickness = new(2);
}
