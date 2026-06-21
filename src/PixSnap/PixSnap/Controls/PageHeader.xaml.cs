using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace PixSnap.Controls;

[ContentProperty(nameof(IconContent))]
public partial class PageHeader : UserControl
{
    public static readonly DependencyProperty IconContentProperty =
        DependencyProperty.Register(nameof(IconContent), typeof(object), typeof(PageHeader));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));

    public PageHeader()
    {
        InitializeComponent();
    }

    public object? IconContent { get => GetValue(IconContentProperty); set => SetValue(IconContentProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
}
