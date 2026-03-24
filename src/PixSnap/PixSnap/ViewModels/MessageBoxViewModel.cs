using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace PixSnap.ViewModels;

/// <summary>
/// 自定义消息对话框 ViewModel：支持多种按钮组合和图标样式，替代原生 MessageBox。
/// </summary>
public partial class MessageBoxViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "PixSnap";
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private Geometry? _iconData;
    [ObservableProperty] private MediaBrush? _iconBrush;
    [ObservableProperty] private bool _iconVisible;
    [ObservableProperty] private bool _showOk;
    [ObservableProperty] private bool _showCancel;
    [ObservableProperty] private bool _showYes;
    [ObservableProperty] private bool _showNo;

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    /// <summary>ViewModel 请求关闭窗口时触发，携带最终结果。</summary>
    public event Action<MessageBoxResult>? RequestClose;

    [RelayCommand]
    private void Ok() => CloseWith(MessageBoxResult.OK);

    [RelayCommand]
    private void Cancel() => CloseWith(MessageBoxResult.Cancel);

    [RelayCommand]
    private void Yes() => CloseWith(MessageBoxResult.Yes);

    [RelayCommand]
    private void No() => CloseWith(MessageBoxResult.No);

    [RelayCommand]
    private void CloseDialog() => CloseWith(MessageBoxResult.None);

    private void CloseWith(MessageBoxResult result)
    {
        Result = result;
        RequestClose?.Invoke(result);
    }

    public static MessageBoxViewModel Create(
        string message,
        string title,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        var vm = new MessageBoxViewModel
        {
            Title = title,
            Message = message
        };
        vm.SetupIcon(icon);
        vm.SetupButtons(button);
        PlaySystemSound(icon);
        return vm;
    }

    private static void PlaySystemSound(MessageBoxImage icon)
    {
        var sound = icon switch
        {
            MessageBoxImage.Error or MessageBoxImage.Stop or MessageBoxImage.Hand
                => System.Media.SystemSounds.Hand,
            MessageBoxImage.Warning or MessageBoxImage.Exclamation
                => System.Media.SystemSounds.Exclamation,
            MessageBoxImage.Information or MessageBoxImage.Asterisk
                => System.Media.SystemSounds.Asterisk,
            MessageBoxImage.Question
                => System.Media.SystemSounds.Question,
            _ => null
        };
        sound?.Play();
    }

    private void SetupIcon(MessageBoxImage icon)
    {
        string? pathData = icon switch
        {
            MessageBoxImage.Error or MessageBoxImage.Stop or MessageBoxImage.Hand =>
                "M12,2C6.47,2 2,6.47 2,12S6.47,22 12,22 22,17.53 22,12 17.53,2 12,2Z" +
                "M17,15.59L15.59,17 12,13.41 8.41,17 7,15.59 10.59,12 7,8.41 8.41,7 12,10.59 15.59,7 17,8.41 13.41,12Z",

            MessageBoxImage.Warning or MessageBoxImage.Exclamation =>
                "M1,21H23L12,2Z M13,18H11V16H13V18Z M13,15H11V9H13V15Z",

            MessageBoxImage.Information or MessageBoxImage.Asterisk =>
                "M12,2C6.47,2 2,6.47 2,12S6.47,22 12,22 22,17.53 22,12 17.53,2 12,2Z" +
                "M13,17H11V11H13V17Z M13,9H11V7H13V9Z",

            MessageBoxImage.Question =>
                "M10,19H14V21H10V19Z" +
                "M12,2C6.47,2 2,6.48 2,12C2,17.52 6.47,22 12,22C17.53,22 22,17.52 22,12C22,6.48 17.53,2 12,2Z" +
                "M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20Z" +
                "M12,6A4,4 0,0,0 8,10H10A2,2 0,0,1 12,8A2,2 0,0,1 14,10C14,12 11,11.75 11,15H13C13,12.75 16,12.5 16,10A4,4 0,0,0 12,6Z",

            _ => null
        };

        if (pathData is null)
        {
            IconVisible = false;
            return;
        }

        IconData = Geometry.Parse(pathData);
        IconData.Freeze();
        // 冻结画刷以节省内存并防止意外修改（创建后不再变更）
        SolidColorBrush MakeBrush(MediaColor color)
        {
            var b = new SolidColorBrush(color);
            b.Freeze();
            return b;
        }

        IconBrush = icon switch
        {
            MessageBoxImage.Error or MessageBoxImage.Stop or MessageBoxImage.Hand =>
                MakeBrush(MediaColor.FromRgb(0xCF, 0x6E, 0x6E)),
            MessageBoxImage.Warning or MessageBoxImage.Exclamation =>
                MakeBrush(MediaColor.FromRgb(0xC8, 0xA5, 0x60)),
            _ => MakeBrush(MediaColor.FromRgb(0xA0, 0xA0, 0xA0))
        };
        IconVisible = true;
    }

    private void SetupButtons(MessageBoxButton button)
    {
        ShowOk = button is MessageBoxButton.OK or MessageBoxButton.OKCancel;
        ShowCancel = button is MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel;
        ShowYes = button is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel;
        ShowNo = button is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel;
    }
}
