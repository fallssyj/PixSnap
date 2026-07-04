using CommunityToolkit.Mvvm.ComponentModel;
using PixSnap.Services;

namespace PixSnap.ViewModels;

public partial class DisclaimerViewModel : ObservableObject
{
    public DisclaimerViewModel(bool requireAcceptance = false)
    {
        Title = requireAcceptance ? "许可与免责声明" : "免责声明";
        Subtitle = requireAcceptance
            ? "请阅读并选择是否同意以下条款"
            : "安装与使用本软件前请仔细阅读";
    }

    public string Title { get; }

    public string Subtitle { get; }

    public IReadOnlyList<DisclaimerContent.Section> Sections { get; } = DisclaimerContent.Sections;

    public string ThirdPartyNoticesUrl { get; } = DisclaimerContent.ThirdPartyNoticesUrl;
}
