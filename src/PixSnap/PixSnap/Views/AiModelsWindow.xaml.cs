using PixSnap.ViewModels;
using System.Windows;

namespace PixSnap.Views;

public partial class AiModelsWindow : Window
{
    public AiModelsWindow(AiModelsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
