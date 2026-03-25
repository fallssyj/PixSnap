using PixSnap.ViewModels;
using MicaWPF.Controls;
using System.Windows;
using System.Windows.Input;
using WpfApp = System.Windows.Application;

namespace PixSnap.Views;

public partial class MessageBoxWindow : MicaWindow
{
    private MessageBoxWindow(MessageBoxViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += _ => Close();
    }

    /// <summary>显示自定义样式的消息框。</summary>
    public static MessageBoxResult Show(
        string message,
        string title = "PixSnap",
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        Window? owner = null)
    {
        if (!WpfApp.Current.Dispatcher.CheckAccess())
        {
            return WpfApp.Current.Dispatcher.Invoke(
                () => Show(message, title, button, icon, owner));
        }

        var viewModel = MessageBoxViewModel.Create(message, title, button, icon);
        var dialog = new MessageBoxWindow(viewModel)
        {
            Owner = owner
                ?? (WpfApp.Current.MainWindow?.IsVisible == true ? WpfApp.Current.MainWindow : null)
        };

        dialog.ShowDialog();
        return viewModel.Result;
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
