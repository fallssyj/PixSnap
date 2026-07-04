using PixSnap.ViewModels;
using System.Windows;

namespace PixSnap.Views;

public partial class DisclaimerWindow : Window
{
    public DisclaimerWindow()
    {
        InitializeComponent();
        DataContext = new DisclaimerViewModel();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
