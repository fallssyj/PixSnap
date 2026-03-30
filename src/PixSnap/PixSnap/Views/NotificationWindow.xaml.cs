using PixSnap.ViewModels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace PixSnap.Views;

public partial class NotificationWindow : Window
{
    public NotificationWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionToBottomRight();

        if (FindResource("FadeIn") is Storyboard fadeIn)
            fadeIn.Begin(this);

        if (DataContext is NotificationViewModel vm)
        {
            vm.CloseRequested += OnCloseRequested;
            vm.Start();
        }
    }

    private void PositionToBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width;
        Top = workArea.Bottom - Height;
    }

    private void OnCloseRequested()
    {
        if (FindResource("FadeOut") is Storyboard fadeOut)
        {
            fadeOut.Completed += (_, _) => CloseInternal();
            fadeOut.Begin(this);
        }
        else
        {
            CloseInternal();
        }
    }

    private void CloseInternal()
    {
        if (DataContext is NotificationViewModel vm)
        {
            vm.CloseRequested -= OnCloseRequested;
            vm.Cleanup();
        }
        Close();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        // 点击关闭按钮区域时不触发 Open（由按钮自己的 Command 处理）
        if (e.Handled) return;

        if (DataContext is NotificationViewModel vm)
            vm.OpenCommand.Execute(null);
    }
}
