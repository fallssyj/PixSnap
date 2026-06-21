using PixSnap.ViewModels;
using Serilog;
using System.Windows;

namespace PixSnap.Views;

public partial class RecordingControlWindow : Window
{
    private readonly RecordingControlViewModel _viewModel;

    public RecordingControlWindow(RecordingControlViewModel viewModel)
    {
        Log.Information("[RecordingControlWindow] 构造函数开始");
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;

        var screen = SystemParameters.WorkArea;
        Left = (screen.Width - Width) / 2;
        Top = 16;

        _viewModel.RequestClose += Close;
        SourceInitialized += (_, _) => _viewModel.OnWindowSourceInitialized(this);
        Closed += OnClosed;

        _viewModel.StartTimers();
        Log.Information("[RecordingControlWindow] 构造函数完成");
    }

    public string? OutputFilePath
    {
        get => _viewModel.OutputFilePath;
        set => _viewModel.OutputFilePath = value;
    }

    public void SetAudioWarningVisible() => _viewModel.SetAudioWarningVisible();

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.RequestClose -= Close;
        _viewModel.StopTimers();
    }
}
