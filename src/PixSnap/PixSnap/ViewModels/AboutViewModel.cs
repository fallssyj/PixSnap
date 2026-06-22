using CommunityToolkit.Mvvm.ComponentModel;
using System.Reflection;

namespace PixSnap.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    public string Version { get; }

    public string RepositoryUrl { get; } = "https://github.com/fallssyj/PixSnap";

    public string IssuesUrl { get; } = "https://github.com/fallssyj/PixSnap/issues";

    public AboutViewModel()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Version = v is not null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0";
    }
}
