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

            var highlight = new Border
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Background = new SolidColorBrush(Color.FromArgb(48, 0, 120, 215)),
                BorderBrush = accent,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(2),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(highlight, bounds.X);
            Canvas.SetTop(highlight, bounds.Y);
            OcrCanvas.Children.Add(highlight);

            double fontSize = Math.Clamp(bounds.Height * 0.82, 9, 48);
            var textBox = new TextBox
            {
                Text = region.Text,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                Foreground = Brushes.White,
                FontSize = fontSize,
                Padding = new Thickness(2, 0, 2, 0),
                Width = Math.Max(bounds.Width, 8),
                Height = Math.Max(bounds.Height, fontSize + 4),
                TextWrapping = TextWrapping.NoWrap,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.IBeam,
                ToolTip = region.Text
            };
            textBox.Focusable = true;
            textBox.ContextMenu = CreateCopyContextMenu(textBox);
            textBox.PreviewMouseLeftButtonDown += (_, e) =>
            {
                textBox.Focus();
                textBox.SelectAll();
                e.Handled = false;
            };
            Canvas.SetLeft(textBox, bounds.X);
            Canvas.SetTop(textBox, bounds.Y);
            OcrCanvas.Children.Add(textBox);
        }
    }

    private static ContextMenu CreateCopyContextMenu(TextBox textBox)
    {
        var menu = new ContextMenu();

        var copy = new MenuItem { Header = "复制", InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) =>
        {
            string text = !string.IsNullOrEmpty(textBox.SelectedText) ? textBox.SelectedText : textBox.Text;
            if (!string.IsNullOrEmpty(text))
                ClipboardHelper.TrySetText(text);
        };
        menu.Items.Add(copy);

        var selectAll = new MenuItem { Header = "全选", InputGestureText = "Ctrl+A" };
        selectAll.Click += (_, _) => textBox.SelectAll();
        menu.Items.Add(selectAll);

        return menu;
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
}
