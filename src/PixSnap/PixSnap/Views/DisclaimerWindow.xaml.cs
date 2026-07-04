using PixSnap.ViewModels;
using System.ComponentModel;
using System.Windows;

namespace PixSnap.Views;

public partial class DisclaimerWindow : Window
{
    private readonly bool _requireAcceptance;

    /// <summary>仅在 <see cref="RequireAcceptance"/> 为 true 时有意义：true=同意，false=拒绝。</summary>
    public bool? AcceptanceResult { get; private set; }

    public bool RequireAcceptance => _requireAcceptance;

    public DisclaimerWindow(bool requireAcceptance = false)
    {
        _requireAcceptance = requireAcceptance;
        InitializeComponent();
        DataContext = new DisclaimerViewModel(requireAcceptance);

        if (requireAcceptance)
        {
            Title = "许可与免责声明";
            ViewFooter.Visibility = Visibility.Collapsed;
            AcceptanceFooter.Visibility = Visibility.Visible;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        AcceptanceResult = true;
        Close();
    }

    private void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        AcceptanceResult = false;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_requireAcceptance && AcceptanceResult is null)
            AcceptanceResult = false;

        base.OnClosing(e);
    }
}
