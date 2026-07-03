using CommunityToolkit.Mvvm.ComponentModel;
using System.Reflection;

namespace PixSnap.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    public string Version { get; }

    public string RepositoryUrl { get; } = "https://github.com/fallssyj/PixSnap";

    public string IssuesUrl { get; } = "https://github.com/fallssyj/PixSnap/issues";

    /// <summary>关于页免责说明（免费、非商用发布）。</summary>
    public string DisclaimerText { get; } =
        "本软件按「原样」免费提供，仅供个人学习与非商业使用，不提供任何明示或暗示的保证。\r\n" +
        "作者仅通过 GitHub 官方仓库免费发布，未授权任何第三方以收费、捆绑、代装或其他获利方式分发本软件；" +
        "未经授权，不得将本软件、其安装包或衍生品进行售卖、倒卖或用于其他营利活动。" +
        "若您通过付费途径获得本软件，该来源与作者无关，作者不对其安全性、完整性及后续支持承担任何责任。\r\n" +
        "您使用本软件及 AI 功能的风险由您自行承担；识别、抠图、修复等结果仅供参考，作者不对其准确性或由此造成的任何损失承担责任。\r\n" +
        "AI 推理在本地完成，默认不上传您的图片与数据。\r\n" +
        "本软件包含第三方开源组件与模型，相关权利归各自权利人所有，详见 GitHub 仓库说明。";

    public AboutViewModel()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Version = v is not null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0";
    }
}
