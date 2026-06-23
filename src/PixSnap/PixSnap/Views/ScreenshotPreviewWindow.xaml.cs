using PixSnap.Controls;
using PixSnap.Services;
using PixSnap.ViewModels;
using Serilog;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace PixSnap.Views;

public partial class ScreenshotPreviewWindow : Window
{
    // ── 窗口尺寸常量（XAML 通过 x:Static 引用）─────────────────────────
    public static readonly double DefaultWidth = 1040;
    public static readonly double DefaultHeight = 760;
    public static readonly double MinWindowWidth = 1000;
    public static readonly double MinWindowHeight = 520;

    // ── 内部常量 ──────────────────────────────────────────────────────
    private const double OverscrollPadding = 200;
    private const double ZoomStepFactor = 1.1;
    private const double EraserSampleStepRatio = 0.30;
    private const double PopupWidthEstimate = 200;
    private const double PopupHeightEstimate = 94;
    private const double ColorGridWidth = 132;

    #region 字段

    // ── 缩放 / 平移 ──────────────────────────────────────────────────
    private bool _zoomTriggeredModeSwitch;
    private bool _isPanningPreview;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    // ── 裁剪 ─────────────────────────────────────────────────────────
    private bool _syncingCropRect;

    #endregion

    public ScreenshotPreviewWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        AllowDrop = true;
        Drop += OnDrop;
        DragOver += OnDragOver;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm)
            return;

        if (vm.IsOcrOverlayVisible && Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape)
        {
            vm.ExitOcrOverlayCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (!vm.IsAnnotateMode)
            return;

        if (IsAnnotationTextInputFocused())
            return;

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D)
        {
            vm.AnnotationPanel.DuplicateSelectedAnnotationCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        if (e.Key == Key.Enter)
        {
            vm.ApplyAnnotationCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (vm.AnnotationPanel.SelectedAnnotation is not null)
            {
                vm.AnnotationPanel.SelectedAnnotation = null;
                e.Handled = true;
                return;
            }

            vm.ExitAnnotationModeCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (vm.AnnotationPanel.TrySelectToolFromKey(e.Key))
            e.Handled = true;
    }

    private static bool IsAnnotationTextInputFocused()
    {
        return Keyboard.FocusedElement is TextBox or ComboBox or PasswordBox;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files?.Length > 0 && IsImageFile(files[0]))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0 && IsImageFile(files[0]) &&
            DataContext is ScreenshotPreviewViewModel vm)
        {
            await vm.LoadFromFileAsync(files[0]);
        }
    }

    private static bool IsImageFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 1. 断开所有事件订阅，释放 ViewModel 内部引用
        var viewModel = DataContext as ScreenshotPreviewViewModel;
        DetachViewModelHandlers(viewModel);
        viewModel?.Cleanup();

        // 2. 清除图片引用与绑定，便于释放 MIL 资源（OnLoaded 会按需恢复绑定）
        BindingOperations.ClearBinding(ActualSizeImage, System.Windows.Controls.Image.SourceProperty);
        ActualSizeImage.Source = null;

        // 3. 清除擦除画布视觉元素
        ClearEraserVisualStrokes();

        // 4. 断开 DataContext，避免关闭后仍持有单例 VM 的视图引用
        DataContext = null;

        // 5. GC 回收 + 释放工作集
        MemoryManagementService.TrimAfterUiRelease();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsurePreviewImageBinding();
        AttachViewModelHandlers(DataContext as ScreenshotPreviewViewModel);
        UpdatePreviewModeVisibility();
        UpdateFitZoomFactor();
    }

    private void EnsurePreviewImageBinding()
    {
        if (BindingOperations.GetBinding(ActualSizeImage, System.Windows.Controls.Image.SourceProperty) is not null)
            return;

        ActualSizeImage.SetBinding(
            System.Windows.Controls.Image.SourceProperty,
            new Binding(nameof(ScreenshotPreviewViewModel.ScreenshotImage))
            {
                Mode = BindingMode.OneWay
            });
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModelHandlers(e.OldValue as ScreenshotPreviewViewModel);
        AttachViewModelHandlers(e.NewValue as ScreenshotPreviewViewModel);
        EnsurePreviewImageBinding();
        UpdatePreviewModeVisibility();
        UpdateFitZoomFactor();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScreenshotPreviewViewModel.IsActualSize):
                UpdatePreviewModeVisibility();
                RefreshRoundCornerOverlayIfActive();
                break;

            case nameof(ScreenshotPreviewViewModel.ScreenshotImage):
                UpdateFitZoomFactor();
                if (DataContext is ScreenshotPreviewViewModel vmImg)
                    UpdateEraserCanvasSize(vmImg);
                RefreshRoundCornerOverlayIfActive();
                break;

            case nameof(ScreenshotPreviewViewModel.IsCropMode):
                UpdateCropOverlayState();
                break;

            case nameof(ScreenshotPreviewViewModel.IsRoundCornerMode):
                UpdateRoundCornerOverlayState();
                break;

            case nameof(ScreenshotPreviewViewModel.IsEraserMode):
                UpdateEraserCanvasState();
                break;

            case nameof(ScreenshotPreviewViewModel.ZoomFactor):
            case nameof(ScreenshotPreviewViewModel.FitZoomFactor):
                RefreshRoundCornerOverlayIfActive();
                break;

            case nameof(ScreenshotPreviewViewModel.IsPinned):
                Topmost = (DataContext as ScreenshotPreviewViewModel)?.IsPinned == true;
                break;
        }
    }

    private void AttachViewModelHandlers(ScreenshotPreviewViewModel? viewModel)
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.CropPanel.CropApplied += OnCropApplied;
            viewModel.CropPanel.CropCancelled += OnCropCancelled;
            viewModel.CropPanel.PropertyChanged += OnCropPanelPropertyChanged;
            viewModel.RoundCornerPanel.RoundCornerApplied += OnRoundCornerApplied;
            viewModel.RoundCornerPanel.RoundCornerCancelled += OnRoundCornerCancelled;
            viewModel.RoundCornerPanel.PropertyChanged += OnRoundCornerPanelPropertyChanged;
            viewModel.EraserPanel.StrokesCleared += OnEraserStrokesCleared;
            viewModel.EraserPanel.InpaintApplied += OnEraserInpaintApplied;

            CropOverlay.CropRectChanged += OnCropOverlayRectChanged;
            CropOverlay.EnterPressed += OnCropOverlayEnter;
            CropOverlay.EscapePressed += OnCropOverlayEscape;
        }
    }

    private void DetachViewModelHandlers(ScreenshotPreviewViewModel? viewModel)
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.CropPanel.CropApplied -= OnCropApplied;
            viewModel.CropPanel.CropCancelled -= OnCropCancelled;
            viewModel.CropPanel.PropertyChanged -= OnCropPanelPropertyChanged;
            viewModel.RoundCornerPanel.RoundCornerApplied -= OnRoundCornerApplied;
            viewModel.RoundCornerPanel.RoundCornerCancelled -= OnRoundCornerCancelled;
            viewModel.RoundCornerPanel.PropertyChanged -= OnRoundCornerPanelPropertyChanged;
            viewModel.EraserPanel.StrokesCleared -= OnEraserStrokesCleared;
            viewModel.EraserPanel.InpaintApplied -= OnEraserInpaintApplied;

            CropOverlay.CropRectChanged -= OnCropOverlayRectChanged;
            CropOverlay.EnterPressed -= OnCropOverlayEnter;
            CropOverlay.EscapePressed -= OnCropOverlayEscape;
        }
    }

    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFitZoomFactor();

        // 裁剪模式下窗口尺寸变化后，等布局完成再刷新裁剪框叠加层
        if (DataContext is ScreenshotPreviewViewModel vm && vm.IsCropMode && vm.ScreenshotImage is not null)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var imgRect = GetImageDisplayRect();
                CropOverlay.RefreshForResize(imgRect, vm.ScreenshotImage.PixelWidth, vm.ScreenshotImage.PixelHeight);
            }, DispatcherPriority.Loaded);
        }

        RefreshRoundCornerOverlayIfActive();
    }

    private void UpdatePreviewModeVisibility()
    {
        if (DataContext is not ScreenshotPreviewViewModel viewModel) return;

        if (viewModel.IsActualSize)
        {
            // 实际大小/手动缩放模式：显示滚动条
            ActualSizeScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            ActualSizeScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            // 由滚轮触发的切换由滚轮 handler 自行定位，此处不覆盖
            if (!_zoomTriggeredModeSwitch)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    ActualSizeScrollViewer.ScrollToHorizontalOffset(OverscrollPadding);
                    ActualSizeScrollViewer.ScrollToVerticalOffset(OverscrollPadding);
                }, DispatcherPriority.Loaded);
            }
        }
        else
        {
            // 缩放以适应模式：隐藏滚动条，居中显示
            ActualSizeScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ActualSizeScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            viewModel.ZoomFactor = viewModel.FitZoomFactor;
            CenterScrollViewerOnImage();
        }
    }

    /// <summary>将 ScrollViewer 滚动到图片居中位置。</summary>
    private void CenterScrollViewerOnImage()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null) return;
            ActualSizeScrollViewer.UpdateLayout();
            double displayW = vm.ScreenshotImage.Width * vm.ZoomFactor;
            double displayH = vm.ScreenshotImage.Height * vm.ZoomFactor;
            double vpW = ActualSizeScrollViewer.ViewportWidth;
            double vpH = ActualSizeScrollViewer.ViewportHeight;
            ActualSizeScrollViewer.ScrollToHorizontalOffset(
                Math.Max(0, OverscrollPadding - (vpW - displayW) / 2));
            ActualSizeScrollViewer.ScrollToVerticalOffset(
                Math.Max(0, OverscrollPadding - (vpH - displayH) / 2));
        }, DispatcherPriority.Loaded);
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

        // 适应模式下同步 ZoomFactor 并居中
        if (!viewModel.IsActualSize)
        {
            viewModel.ZoomFactor = viewModel.FitZoomFactor;
            CenterScrollViewerOnImage();
        }
    }

    private void PreviewHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel viewModel || viewModel.ScreenshotImage is null)
        {
            return;
        }

        // 裁剪模式下禁止缩放，防止 overlay 与图片显示错位
        if (viewModel.IsCropMode)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        var pointerPosition = e.GetPosition(ActualSizeScrollViewer);
        var oldZoom = viewModel.ZoomFactor;
        var zoomStep = e.Delta > 0 ? ZoomStepFactor : 1.0 / ZoomStepFactor;
        var newZoom = Math.Clamp(
            oldZoom * zoomStep,
            ScreenshotPreviewViewModel.MinZoomFactor,
            ScreenshotPreviewViewModel.MaxZoomFactor);

        if (Math.Abs(newZoom - oldZoom) < 0.001)
        {
            return;
        }

        // 统一公式：从 ScrollViewer 滚动位置推算鼠标下方的图片内容坐标
        var contentX = (ActualSizeScrollViewer.HorizontalOffset + pointerPosition.X - OverscrollPadding) / oldZoom;
        var contentY = (ActualSizeScrollViewer.VerticalOffset + pointerPosition.Y - OverscrollPadding) / oldZoom;
        bool switching = !viewModel.IsActualSize;

        ApplyZoomAroundCursor(viewModel, newZoom, contentX, contentY, pointerPosition, isModeSwitch: switching);
    }

    /// <summary>以鼠标为中心应用缩放并定位滚动偏移，保持鼠标下方图片像素不动。</summary>
    private void ApplyZoomAroundCursor(
        ScreenshotPreviewViewModel vm, double newZoom,
        double contentX, double contentY, Point pointer, bool isModeSwitch)
    {
        if (isModeSwitch) _zoomTriggeredModeSwitch = true;
        vm.SetManualZoomFactor(newZoom);
        if (isModeSwitch) _zoomTriggeredModeSwitch = false;

        ActualSizeScrollViewer.UpdateLayout();
        ActualSizeScrollViewer.ScrollToHorizontalOffset(Math.Max(0, OverscrollPadding + contentX * newZoom - pointer.X));
        ActualSizeScrollViewer.ScrollToVerticalOffset(Math.Max(0, OverscrollPadding + contentY * newZoom - pointer.Y));
    }

    private void ActualSizeScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        if (IsScrollBarInteraction(e.OriginalSource as DependencyObject)) return;

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

    private void ActualSizeScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        EndPreviewPan();
    }

    private void ActualSizeScrollViewer_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.MiddleButton != MouseButtonState.Pressed)
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

    // ══════════════════════════════════════════════════════════════════════════
    // 裁剪覆盖层（委托给 CropOverlayControl）
    // ══════════════════════════════════════════════════════════════════════════

    private void UpdateCropOverlayState()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        if (vm.IsCropMode)
        {
            CropOverlay.LockedAspectRatio = vm.CropPanel.LockedAspectRatio;
            Dispatcher.InvokeAsync(() =>
            {
                PreviewViewport.UpdateLayout();
                var imgRect = GetImageDisplayRect();
                var img = vm.ScreenshotImage!;
                CropOverlay.Initialize(imgRect, img.PixelWidth, img.PixelHeight);
                CropOverlay.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    private void OnCropPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingCropRect) return;
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null) return;

        if (e.PropertyName is nameof(CropViewModel.LockedAspectRatio))
        {
            CropOverlay.LockedAspectRatio = vm.CropPanel.LockedAspectRatio;
            return;
        }

        if (e.PropertyName is nameof(CropViewModel.CropX) or nameof(CropViewModel.CropY)
            or nameof(CropViewModel.CropWidth) or nameof(CropViewModel.CropHeight))
        {
            var imgRect = GetImageDisplayRect();
            var img = vm.ScreenshotImage;
            CropOverlay.SetPixelRect(
                vm.CropPanel.CropX, vm.CropPanel.CropY,
                vm.CropPanel.CropWidth, vm.CropPanel.CropHeight,
                imgRect, img.PixelWidth, img.PixelHeight);
        }
    }

    private void OnCropOverlayRectChanged(int x, int y, int w, int h)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        _syncingCropRect = true;
        try
        {
            vm.CropPanel.CropX = x;
            vm.CropPanel.CropY = y;
            vm.CropPanel.CropWidth = w;
            vm.CropPanel.CropHeight = h;
        }
        finally
        {
            _syncingCropRect = false;
        }
    }

    private void OnCropOverlayEnter()
    {
        if (DataContext is ScreenshotPreviewViewModel vm &&
            vm.CropPanel.ApplyCommand.CanExecute(vm.ScreenshotImage))
            vm.CropPanel.ApplyCommand.Execute(vm.ScreenshotImage);
    }

    private void OnCropOverlayEscape()
    {
        if (DataContext is ScreenshotPreviewViewModel vm)
            vm.CropPanel.CancelCommand.Execute(null);
    }

    /// <summary>获取图片在 PreviewViewport 中的显示区域。</summary>
    private Rect GetImageDisplayRect()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null)
            return new Rect(0, 0, PreviewViewport.ActualWidth, PreviewViewport.ActualHeight);

        try
        {
            // 利用 WPF 视觉树变换，精确计算图片经 LayoutTransform + ScrollViewer 偏移后的实际显示位置
            var transform = ActualSizeImage.TransformToAncestor(PreviewViewport);
            var topLeft = transform.Transform(new Point(0, 0));
            var bottomRight = transform.Transform(
                new Point(ActualSizeImage.ActualWidth, ActualSizeImage.ActualHeight));
            return new Rect(topLeft, bottomRight);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "图片显示区域变换失败，使用手动计算降级");
            // 降级：手动计算
            double zoom = vm.ZoomFactor;
            double displayW = vm.ScreenshotImage.Width * zoom;
            double displayH = vm.ScreenshotImage.Height * zoom;
            double left = OverscrollPadding - ActualSizeScrollViewer.HorizontalOffset;
            double top = OverscrollPadding - ActualSizeScrollViewer.VerticalOffset;
            return new Rect(left, top, displayW, displayH);
        }
    }

    /// <summary>DPI 缩放因子：Canvas DIP → Image Pixel。</summary>
    private double GetAnnotationDpiScale()
    {
        if (DataContext is ScreenshotPreviewViewModel vm && vm.ScreenshotImage is not null)
            return vm.ScreenshotImage.DpiX / 96.0;
        return 1.0;
    }

    /// <summary>将鼠标在擦除/标注 Canvas 的本地坐标转换为图片像素坐标。</summary>
    private bool TryGetImagePixelPoint(Point canvasPos, out Point imgPixelPoint)
    {
        imgPixelPoint = default;
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null)
            return false;

        double pw = vm.ScreenshotImage.PixelWidth;
        double ph = vm.ScreenshotImage.PixelHeight;
        double dpiScale = GetAnnotationDpiScale();

        // Canvas 坐标为 DIP 空间，乘以 DPI 缩放转为像素坐标
        double imgX = canvasPos.X * dpiScale;
        double imgY = canvasPos.Y * dpiScale;

        const double tolerance = 8;
        if (imgX < -tolerance || imgY < -tolerance ||
            imgX > pw + tolerance || imgY > ph + tolerance)
            return false;

        imgPixelPoint = new Point(Math.Clamp(imgX, 0, pw), Math.Clamp(imgY, 0, ph));
        return true;
    }

    private void OnCropApplied(BitmapSource newImage)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        vm.ApplyEditedImage(newImage);
        vm.ExitEditMode();
    }

    private void OnCropCancelled()
    {
        if (DataContext is ScreenshotPreviewViewModel vm)
            vm.ExitEditMode();
    }

    private void OnRoundCornerApplied(BitmapSource newImage)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        vm.ApplyEditedImage(newImage);
        vm.ExitEditMode();
    }

    private void OnRoundCornerCancelled()
    {
        if (DataContext is ScreenshotPreviewViewModel vm)
            vm.ExitEditMode();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 圆角预览蒙版
    // ══════════════════════════════════════════════════════════════════════════

    private void UpdateRoundCornerOverlayState()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        if (vm.IsRoundCornerMode)
        {
            Dispatcher.InvokeAsync(() =>
            {
                PreviewViewport.UpdateLayout();
                RefreshRoundCornerOverlay(vm);
            }, DispatcherPriority.Loaded);
        }
        else
        {
            RoundCornerOverlay.Hide();
        }
    }

    private void RefreshRoundCornerOverlayIfActive()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || !vm.IsRoundCornerMode || vm.ScreenshotImage is null)
            return;

        Dispatcher.InvokeAsync(() => RefreshRoundCornerOverlay(vm), DispatcherPriority.Render);
    }

    private void OnRoundCornerPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RoundCornerViewModel.CornerRadius))
            RefreshRoundCornerOverlayIfActive();
    }

    private void RefreshRoundCornerOverlay(ScreenshotPreviewViewModel vm)
    {
        if (!vm.IsRoundCornerMode || vm.ScreenshotImage is null)
        {
            RoundCornerOverlay.Hide();
            return;
        }

        var img = vm.ScreenshotImage;
        RoundCornerOverlay.Update(
            GetImageDisplayRect(),
            img.PixelWidth,
            img.PixelHeight,
            vm.RoundCornerPanel.CornerRadius);
    }

    private void PreviewViewport_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsOnOcrTextBox(source))
        {
            if (DataContext is ScreenshotPreviewViewModel vm)
                vm.IsAiPopupOpen = false;
            return;
        }

        if (DataContext is ScreenshotPreviewViewModel vm2)
        {
            if (vm2.IsAnnotateMode || vm2.IsCropMode || vm2.IsEraserMode || vm2.IsRoundCornerMode)
                return;
        }

        var pos = e.GetPosition(PreviewViewport);

        // 控制弹出位置在预览区域可见范围内
        double x = Math.Clamp(pos.X, 0, Math.Max(0, PreviewViewport.ActualWidth - PopupWidthEstimate));
        double y = Math.Clamp(pos.Y, 0, Math.Max(0, PreviewViewport.ActualHeight - PopupHeightEstimate));

        // 将弹出位置切换为相对于预览区域的点击坐标
        AiModulePopup.PlacementTarget = PreviewViewport;
        AiModulePopup.Placement = PlacementMode.Relative;
        AiModulePopup.HorizontalOffset = x;
        AiModulePopup.VerticalOffset = y;

        // 直接更新 ViewModel，TwoWay 绑定将自动同步至 AiModulePopup.IsOpen
        if (DataContext is ScreenshotPreviewViewModel vm3)
            vm3.IsAiPopupOpen = true;
        e.Handled = true;
    }

    private static bool IsOnOcrTextBox(DependencyObject source)
    {
        while (source is not null)
        {
            if (source is TextBox)
                return true;
            if (source is OcrOverlayControl)
                return false;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }
}