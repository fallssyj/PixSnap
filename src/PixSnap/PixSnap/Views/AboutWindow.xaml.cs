using PixSnap.ViewModels;
using System.Windows;

namespace PixSnap.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DataContext = new AboutViewModel();
    }
}
