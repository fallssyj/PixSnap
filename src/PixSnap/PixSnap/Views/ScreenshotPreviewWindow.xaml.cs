using System.Windows;
using System.Windows.Input;
using PixSnap.ViewModels;
using System;
using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PixSnap.Views;

public partial class ScreenshotPreviewWindow : Window
{
    private bool _isPanningPreview;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    public ScreenshotPreviewWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModelHandlers(DataContext as ScreenshotPreviewViewModel);
        UpdateFitZoomFactor();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModelHandlers(e.OldValue as ScreenshotPreviewViewModel);
        AttachViewModelHandlers(e.NewValue as ScreenshotPreviewViewModel);
        UpdateFitZoomFactor();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScreenshotPreviewViewModel.ScreenshotImage))
        {
            UpdateFitZoomFactor();
        }
    }

    private void AttachViewModelHandlers(ScreenshotPreviewViewModel? viewModel)
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void DetachViewModelHandlers(ScreenshotPreviewViewModel? viewModel)
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFitZoomFactor();
    }

    private void UpdateFitZoomFactor()
    {
        if (DataContext is not ScreenshotPreviewViewModel viewModel)
        {
            return;
        }

        if (viewModel.ScreenshotImage is null || PreviewViewport.ActualWidth <= 0 || PreviewViewport.ActualHeight <= 0)
        {
            viewModel.FitZoomFactor = 1.0;
            return;
        }

        var imageWidth = viewModel.ScreenshotImage.Width > 0 ? viewModel.ScreenshotImage.Width : viewModel.ScreenshotImage.PixelWidth;
        var imageHeight = viewModel.ScreenshotImage.Height > 0 ? viewModel.ScreenshotImage.Height : viewModel.ScreenshotImage.PixelHeight;
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            viewModel.FitZoomFactor = 1.0;
            return;
        }

        var fitZoom = Math.Min(PreviewViewport.ActualWidth / imageWidth, PreviewViewport.ActualHeight / imageHeight);
        viewModel.FitZoomFactor = Math.Clamp(fitZoom, ScreenshotPreviewViewModel.MinZoomFactor, 1.0);
    }

    private void PreviewHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel viewModel || viewModel.ScreenshotImage is null)
        {
            return;
        }

        e.Handled = true;

        var pointerPosition = e.GetPosition(PreviewViewport);
        var oldZoom = viewModel.IsActualSize ? viewModel.ZoomFactor : viewModel.FitZoomFactor;
        if (!viewModel.IsActualSize)
        {
            viewModel.ZoomFactor = oldZoom;
            viewModel.IsActualSize = true;
            ActualSizeScrollViewer.UpdateLayout();
        }

        var zoomStep = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        var newZoom = Math.Clamp(
            oldZoom * zoomStep,
            ScreenshotPreviewViewModel.MinZoomFactor,
            ScreenshotPreviewViewModel.MaxZoomFactor);

        if (Math.Abs(newZoom - oldZoom) < 0.001)
        {
            return;
        }

        var contentX = (ActualSizeScrollViewer.HorizontalOffset + pointerPosition.X) / oldZoom;
        var contentY = (ActualSizeScrollViewer.VerticalOffset + pointerPosition.Y) / oldZoom;

        viewModel.ZoomFactor = newZoom;

        ActualSizeScrollViewer.UpdateLayout();
        ActualSizeScrollViewer.ScrollToHorizontalOffset(Math.Max(0, contentX * newZoom - pointerPosition.X));
        ActualSizeScrollViewer.ScrollToVerticalOffset(Math.Max(0, contentY * newZoom - pointerPosition.Y));
    }

    private void ActualSizeScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel viewModel || !viewModel.IsActualSize)
        {
            return;
        }

        if (IsScrollBarInteraction(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (ActualSizeScrollViewer.ScrollableWidth <= 0 && ActualSizeScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        _isPanningPreview = true;
        _panStartPoint = e.GetPosition(ActualSizeScrollViewer);
        _panStartHorizontalOffset = ActualSizeScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = ActualSizeScrollViewer.VerticalOffset;
        ActualSizeScrollViewer.Cursor = Cursors.SizeAll;
        ActualSizeScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void ActualSizeScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanningPreview)
        {
            return;
        }

        var currentPoint = e.GetPosition(ActualSizeScrollViewer);
        var delta = currentPoint - _panStartPoint;

        ActualSizeScrollViewer.ScrollToHorizontalOffset(Math.Max(0, _panStartHorizontalOffset - delta.X));
        ActualSizeScrollViewer.ScrollToVerticalOffset(Math.Max(0, _panStartVerticalOffset - delta.Y));
        e.Handled = true;
    }

    private void ActualSizeScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndPreviewPan();
    }

    private void ActualSizeScrollViewer_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPreviewPan();
        }
    }

    private void EndPreviewPan()
    {
        if (!_isPanningPreview)
        {
            return;
        }

        _isPanningPreview = false;
        ActualSizeScrollViewer.ReleaseMouseCapture();
        ActualSizeScrollViewer.ClearValue(CursorProperty);
    }

    private static bool IsScrollBarInteraction(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ScrollBar || current is Thumb)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            viewModel.CloseCommand.Execute(this);
            e.Handled = true;
        }
    }
}