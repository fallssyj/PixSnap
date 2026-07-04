using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Services;
using PixSnap.Views;
using System.Windows;

namespace PixSnap.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    public string Version { get; } = UpdateCheckService.CurrentVersionDisplay;

    public string RepositoryUrl { get; } = "https://github.com/fallssyj/PixSnap";

    public string IssuesUrl { get; } = "https://github.com/fallssyj/PixSnap/issues";

    public string DisclaimerSummary { get; } = DisclaimerContent.Summary;

    [RelayCommand]
    private void OpenDisclaimer()
    {
        var owner = Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsVisible && w.IsActive)
            ?? Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible);

        var window = new DisclaimerWindow
        {
            Owner = WindowOwnerHelper.GetActiveOwner(owner)
        };
        window.ShowDialog();
    }
}
