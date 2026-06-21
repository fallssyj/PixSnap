using iNKORE.UI.WPF.Modern.Controls;
using System.Windows;
using ModernMessageBox = iNKORE.UI.WPF.Modern.Controls.MessageBox;

namespace PixSnap.Services;

internal static class AppMessageBox
{
    public static MessageBoxResult Show(
        string message,
        string title = "PixSnap",
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        Window? owner = null)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            return Application.Current.Dispatcher.Invoke(
                () => Show(message, title, button, icon, owner));
        }

        owner ??= Application.Current.MainWindow?.IsVisible == true
            ? Application.Current.MainWindow
            : null;

        return ModernMessageBox.Show(owner, message, title, button, icon);
    }
}
