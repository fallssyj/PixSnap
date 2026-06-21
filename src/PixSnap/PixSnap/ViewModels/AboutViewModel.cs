using CommunityToolkit.Mvvm.ComponentModel;
using System.Reflection;

namespace PixSnap.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    public string Version { get; }

    public AboutViewModel()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Version = v is not null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0";
    }
}
