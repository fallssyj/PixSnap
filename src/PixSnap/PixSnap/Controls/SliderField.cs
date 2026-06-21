using System.Windows;
using System.Windows.Controls;

namespace PixSnap.Controls;

/// <summary>复合控件：标签 + 数字输入框 + 滑块，用于浮动面板中的数值参数编辑。</summary>
public partial class SliderField : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SliderField), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(SliderField), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(SliderField),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(SliderField), new PropertyMetadata(0.0));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SliderField), new PropertyMetadata(100.0));

    public static readonly DependencyProperty TickFrequencyProperty =
        DependencyProperty.Register(nameof(TickFrequency), typeof(double), typeof(SliderField), new PropertyMetadata(1.0));

    public static readonly DependencyProperty SmallChangeProperty =
        DependencyProperty.Register(nameof(SmallChange), typeof(double), typeof(SliderField), new PropertyMetadata(1.0));

    public static readonly DependencyProperty LargeChangeProperty =
        DependencyProperty.Register(nameof(LargeChange), typeof(double), typeof(SliderField), new PropertyMetadata(10.0));

    public SliderField()
    {
        InitializeComponent();
    }

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double TickFrequency { get => (double)GetValue(TickFrequencyProperty); set => SetValue(TickFrequencyProperty, value); }
    public double SmallChange { get => (double)GetValue(SmallChangeProperty); set => SetValue(SmallChangeProperty, value); }
    public double LargeChange { get => (double)GetValue(LargeChangeProperty); set => SetValue(LargeChangeProperty, value); }
}
