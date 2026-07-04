using CommunityToolkit.Mvvm.ComponentModel;
using PixSnap.Services;

namespace PixSnap.ViewModels;

public partial class DisclaimerViewModel : ObservableObject
{
    public IReadOnlyList<DisclaimerContent.Section> Sections { get; } = DisclaimerContent.Sections;

    public string ThirdPartyNoticesUrl { get; } = DisclaimerContent.ThirdPartyNoticesUrl;
}
