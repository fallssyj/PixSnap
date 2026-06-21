using PixSnap.ViewModels;
using System.Windows;

namespace PixSnap.Views;

public partial class LogViewerWindow : Window
{
    public LogViewerWindow()
    {
        InitializeComponent();
        DataContext = new LogViewerViewModel();
    }
}
