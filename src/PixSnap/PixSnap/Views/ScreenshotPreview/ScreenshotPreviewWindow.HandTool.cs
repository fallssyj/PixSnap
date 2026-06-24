using PixSnap.Services;
using PixSnap.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace PixSnap.Views;

public partial class ScreenshotPreviewWindow
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseWheel = 0x020A;
    private const int VkSpace = 0x20;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;

    private bool _isHandToolActive;
    private bool _isPanningPreview;
    private bool _nativeSpaceDown;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private HwndSource? _handToolHwndSource;
    private DispatcherTimer? _handToolSyncTimer;

    private void InitializeHandTool()
    {
        SourceInitialized += (_, _) => EnsureHandToolHwndHook();
        PreviewKeyDown += OnHandToolPreviewKeyDown;
        PreviewKeyUp += OnHandToolPreviewKeyUp;
        PreviewViewport.PreviewKeyDown += OnHandToolPreviewKeyDown;
        PreviewViewport.PreviewKeyUp += OnHandToolPreviewKeyUp;
        PreviewViewport.PreviewMouseMove += (_, _) => SyncHandToolFromKeyboard();
        Deactivated += (_, _) => DeactivateHandTool(force: true);
    }

    private void EnsureHandToolHwndHook()
    {
        if (_handToolHwndSource is not null)
            return;

        if (PresentationSource.FromVisual(this) is not HwndSource source)
            return;

        _handToolHwndSource = source;
        source.AddHook(HandToolWndProc);
    }

    private void RemoveHandToolHwndHook()
    {
        if (_handToolHwndSource is null)
            return;

        _handToolHwndSource.RemoveHook(HandToolWndProc);
        _handToolHwndSource = null;
    }

    private void CleanupHandTool()
    {
        StopHandToolSyncTimer();
        RemoveHandToolHwndHook();
        DeactivateHandTool(force: true);
    }

    private IntPtr HandToolWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmKeyDown:
            case WmSysKeyDown:
                if ((int)wParam == VkSpace && !IsRepeatedKeyDown(lParam) && ShouldCaptureSpaceForHandTool())
                {
                    OnNativeSpaceKeyDown();
                    handled = true;
                }
                break;

            case WmKeyUp:
            case WmSysKeyUp:
                if ((int)wParam == VkSpace)
                {
                    OnNativeSpaceKeyUp();
                    if (_nativeSpaceDown || _isHandToolActive || ShouldCaptureSpaceForHandTool())
                        handled = true;
                }
                break;

            case WmMouseWheel:
                if (_isHandToolActive || _nativeSpaceDown)
                    Dispatcher.BeginInvoke(() => SyncHandToolFromKeyboard(), DispatcherPriority.Input);
                break;
        }

        return IntPtr.Zero;
    }

    private static bool IsRepeatedKeyDown(IntPtr lParam)
        => (lParam.ToInt64() & (1 << 30)) != 0;

    private bool ShouldCaptureSpaceForHandTool()
    {
        if (!CanUseHandTool())
            return false;

        return !AreChordModifierKeysDown();
    }

    private static bool AreChordModifierKeysDown()
        => PhysicalKeyboardHelper.IsKeyPhysicallyDown(VkControl)
           || PhysicalKeyboardHelper.IsKeyPhysicallyDown(VkShift)
           || PhysicalKeyboardHelper.IsKeyPhysicallyDown(VkMenu);

    private void OnHandToolPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.IsRepeat || Keyboard.Modifiers != ModifierKeys.None)
            return;

        if (!ShouldCaptureSpaceForHandTool())
            return;

        OnNativeSpaceKeyDown();
        e.Handled = true;
    }

    private void OnHandToolPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space)
            return;

        if (!_nativeSpaceDown && !_isHandToolActive)
            return;

        OnNativeSpaceKeyUp();
        e.Handled = true;
    }

    private void OnNativeSpaceKeyDown()
    {
        _nativeSpaceDown = true;
        if (!ShouldCaptureSpaceForHandTool())
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (_nativeSpaceDown && ShouldCaptureSpaceForHandTool())
                ActivateHandTool();
        }, DispatcherPriority.Input);
    }

    private void OnNativeSpaceKeyUp()
    {
        _nativeSpaceDown = false;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isHandToolActive)
                DeactivateHandTool(force: true);
        }, DispatcherPriority.Input);
    }

    private void SyncHandToolFromKeyboard()
        => SyncHandToolFromKeyboard(forceSpaceUp: false);

    private void SyncHandToolFromKeyboard(bool forceSpaceUp)
    {
        if (!CanUseHandTool())
        {
            if (_isHandToolActive)
                DeactivateHandTool(force: true);
            return;
        }

        bool spaceDown = !forceSpaceUp && IsSpaceEngaged();
        if (spaceDown)
        {
            if (!_isHandToolActive)
                ActivateHandTool();
        }
        else if (_isHandToolActive)
        {
            DeactivateHandTool(force: true);
        }
    }

    private bool IsSpaceEngaged()
        => _nativeSpaceDown || PhysicalKeyboardHelper.IsSpacePhysicallyDown;

    private void StartHandToolSyncTimer()
    {
        _handToolSyncTimer ??= new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        _handToolSyncTimer.Tick -= OnHandToolSyncTimerTick;
        _handToolSyncTimer.Tick += OnHandToolSyncTimerTick;

        if (!_handToolSyncTimer.IsEnabled)
            _handToolSyncTimer.Start();
    }

    private void StopHandToolSyncTimer()
    {
        _handToolSyncTimer?.Stop();
    }

    private void OnHandToolSyncTimerTick(object? sender, EventArgs e) => SyncHandToolFromKeyboard();

    private void ActivateHandTool()
    {
        if (_isHandToolActive)
            return;

        _isHandToolActive = true;
        FocusPreviewViewportIfNeeded();
        UpdateHandToolOverlayState();
        UpdateHandToolCursor();
        StartHandToolSyncTimer();
    }

    private void DeactivateHandTool(bool force = false)
    {
        if (!_isHandToolActive)
        {
            if (force)
            {
                _nativeSpaceDown = false;
                UpdateHandToolCursor();
            }
            return;
        }

        if (!force && IsSpaceEngaged())
            return;

        _isHandToolActive = false;
        _nativeSpaceDown = false;
        StopHandToolSyncTimer();
        EndPreviewPan();
        UpdateHandToolOverlayState();
        UpdateHandToolCursor();
        AnnotationOverlay.RestoreInteractionCursor();
    }

    private void FocusPreviewViewportIfNeeded()
    {
        if (!CanUseHandTool())
            return;

        if (!PreviewViewport.IsKeyboardFocused)
            PreviewViewport.Focus();
    }

    private void UpdateHandToolCursor()
    {
        if (_isHandToolActive)
        {
            Mouse.OverrideCursor = _isPanningPreview ? Cursors.SizeAll : Cursors.Hand;
            return;
        }

        Mouse.OverrideCursor = null;
        Mouse.UpdateCursor();
    }

    private bool CanUseHandTool()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null)
            return false;

        return !IsAnnotationTextInputFocused();
    }

    private static bool IsAnnotationTextInputFocused()
        => Keyboard.FocusedElement is TextBox or ComboBox or PasswordBox;

    private void UpdateHandToolOverlayState()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm)
            return;

        if (_isHandToolActive)
        {
            AnnotationOverlay.IsHitTestVisible = false;
            OcrOverlay.IsHitTestVisible = false;
            EraserCanvas.IsHitTestVisible = false;
            EraserCursorIndicator.Visibility = Visibility.Collapsed;
            EraserCursorIndicatorOuter.Visibility = Visibility.Collapsed;
            return;
        }

        AnnotationOverlay.IsHitTestVisible = vm.IsAnnotateMode;
        OcrOverlay.IsHitTestVisible = vm.IsOcrOverlayVisible;
        UpdateEraserCanvasState();
    }

    private void PreviewViewport_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        FocusPreviewViewportIfNeeded();
        SyncHandToolFromKeyboard();
        if (!_isHandToolActive || e.ChangedButton != MouseButton.Left)
            return;

        if (IsScrollBarInteraction(e.OriginalSource as DependencyObject))
            return;

        BeginPreviewPan(e.GetPosition(ActualSizeScrollViewer));
        e.Handled = true;
    }

    private void BeginPreviewPan(Point startPoint)
    {
        _isPanningPreview = true;
        _panStartPoint = startPoint;
        _panStartHorizontalOffset = ActualSizeScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = ActualSizeScrollViewer.VerticalOffset;
        UpdateHandToolCursor();
        ActualSizeScrollViewer.CaptureMouse();
    }

    private void PreviewViewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        SyncHandToolFromKeyboard();

        if (!_isPanningPreview)
            return;

        var currentPoint = e.GetPosition(ActualSizeScrollViewer);
        var delta = currentPoint - _panStartPoint;

        ActualSizeScrollViewer.ScrollToHorizontalOffset(Math.Max(0, _panStartHorizontalOffset - delta.X));
        ActualSizeScrollViewer.ScrollToVerticalOffset(Math.Max(0, _panStartVerticalOffset - delta.Y));
        e.Handled = true;
    }

    private void PreviewViewport_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanningPreview && e.ChangedButton == MouseButton.Left)
            EndPreviewPan();

        SyncHandToolFromKeyboard();
    }

    private void PreviewViewport_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isPanningPreview && e.LeftButton != MouseButtonState.Pressed)
            EndPreviewPan();

        SyncHandToolFromKeyboard();
    }

    private void EndPreviewPan()
    {
        if (!_isPanningPreview)
            return;

        _isPanningPreview = false;
        ActualSizeScrollViewer.ReleaseMouseCapture();
        UpdateHandToolCursor();
    }

    private static bool IsScrollBarInteraction(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ScrollBar or Thumb)
                return true;

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
