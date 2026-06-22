using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PixSnap.Models;
using PixSnap.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace PixSnap.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    // 当前已确认的快捷键（初始化时从注册表读取，Save 时写入）
    private int _pendingModifiers;
    private int _pendingKey;
    private int _confirmedThemeIndex;
    private int _confirmedGpuDeviceId;
    private int _confirmedOcrModelIndex;
    private int _confirmedMattingModelIndex;
    private int _confirmedSuperResolutionModelIndex;
    private int _confirmedSegmentationModelIndex;
    private int _confirmedVisionModelIndex;

    [ObservableProperty]
    private ObservableCollection<GpuDeviceOption> _gpuDevices = [];

    [ObservableProperty]
    private GpuDeviceOption? _selectedGpuDevice;

    [ObservableProperty]
    private bool _isGpuListReady = true;

    /// <summary>0 = Mobile, 1 = Server。</summary>
    [ObservableProperty]
    private int _selectedOcrModelIndex;

    [ObservableProperty]
    private int _selectedMattingModelIndex;

    [ObservableProperty]
    private int _selectedSuperResolutionModelIndex;

    [ObservableProperty]
    private int _selectedSegmentationModelIndex;

    [ObservableProperty]
    private int _selectedVisionModelIndex;

    [ObservableProperty]
    private bool _isStartupEnabled;

    /// <summary>主题索引：0 = 自动, 1 = 深色, 2 = 浅色。</summary>
    [ObservableProperty]
    private int _selectedThemeIndex;

    /// <summary>自定义保存目录。</summary>
    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    /// <summary>是否启用自动保存到指定目录。</summary>
    [ObservableProperty]
    private bool _isAutoSaveEnabled;

    /// <summary>录屏临时文件存放目录。</summary>
    [ObservableProperty]
    private string _recordingTempDirectory = string.Empty;

    /// <summary>是否处于快捷键录制状态（用户点击输入框后激活）。</summary>
    [ObservableProperty]
    private bool _isRecordingHotkey;

    /// <summary>快捷键显示文本，录制中时显示提示语。</summary>
    [ObservableProperty]
    private string _hotkeyDisplayText = string.Empty;

    /// <summary>请求关闭设置窗口。</summary>
    public event Action? RequestClose;

    public SettingsViewModel()
    {
        _isStartupEnabled = SettingsService.ReadStartupEnabled();
        _selectedThemeIndex = SettingsService.ReadTheme();
        _confirmedThemeIndex = _selectedThemeIndex;
        _saveDirectory = SettingsService.ReadSaveDirectory();
        _isAutoSaveEnabled = SettingsService.ReadAutoSave();
        _recordingTempDirectory = SettingsService.ReadRecordingTempDirectory();
        var (modifiers, key) = SettingsService.ReadHotkey();
        _pendingModifiers = (int)modifiers;
        _pendingKey = (int)key;
        UpdateHotkeyDisplay();
        _confirmedGpuDeviceId = SettingsService.ReadAiGpuDeviceId();
        _selectedOcrModelIndex = (int)SettingsService.ReadOcrModelTier();
        _confirmedOcrModelIndex = _selectedOcrModelIndex;
        _selectedMattingModelIndex = (int)SettingsService.ReadMattingModel();
        _confirmedMattingModelIndex = _selectedMattingModelIndex;
        _selectedSuperResolutionModelIndex = (int)SettingsService.ReadSuperResolutionModel();
        _confirmedSuperResolutionModelIndex = _selectedSuperResolutionModelIndex;
        _selectedSegmentationModelIndex = (int)SettingsService.ReadSegmentationModel();
        _confirmedSegmentationModelIndex = _selectedSegmentationModelIndex;
        _selectedVisionModelIndex = (int)SettingsService.ReadVisionModel();
        _confirmedVisionModelIndex = _selectedVisionModelIndex;
        GpuDevices = new ObservableCollection<GpuDeviceOption>(DirectMlDeviceEnumerator.GetCachedOrDefault());
        SelectedGpuDevice = GpuDevices.FirstOrDefault(g => g.DeviceId == _confirmedGpuDeviceId) ?? GpuDevices.FirstOrDefault();
        _ = LoadGpuDevicesAsync();
    }

    private async Task LoadGpuDevicesAsync()
    {
        IsGpuListReady = false;
        try
        {
            var devices = await DirectMlDeviceEnumerator.EnsureEnumeratedAsync().ConfigureAwait(false);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                GpuDevices = new ObservableCollection<GpuDeviceOption>(devices);
                SelectedGpuDevice = GpuDevices.FirstOrDefault(g => g.DeviceId == _confirmedGpuDeviceId)
                    ?? GpuDevices.FirstOrDefault();
            });
        }
        finally
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => IsGpuListReady = true);
        }
    }

    // 进入/退出录制状态时同步显示文本
    partial void OnIsRecordingHotkeyChanged(bool value) =>
        HotkeyDisplayText = value ? "请按下快捷键..." : HotkeyDisplayFormatter.FormatForDisplay((ModifierKeys)_pendingModifiers, (Key)_pendingKey);

    partial void OnSelectedThemeIndexChanged(int value) => ThemeHelper.ApplyTheme(value);

    /// <summary>撤销未保存的主题与 GPU 预览（Cancel / 关闭窗口时调用）。</summary>
    public void RevertUnsavedChanges()
    {
        if (SelectedThemeIndex != _confirmedThemeIndex)
            ThemeHelper.ApplyTheme(_confirmedThemeIndex);

        SelectedGpuDevice = GpuDevices.FirstOrDefault(g => g.DeviceId == _confirmedGpuDeviceId)
            ?? GpuDevices.FirstOrDefault();
        SelectedOcrModelIndex = _confirmedOcrModelIndex;
        SelectedMattingModelIndex = _confirmedMattingModelIndex;
        SelectedSuperResolutionModelIndex = _confirmedSuperResolutionModelIndex;
        SelectedSegmentationModelIndex = _confirmedSegmentationModelIndex;
        SelectedVisionModelIndex = _confirmedVisionModelIndex;
    }

    /// <summary>
    /// 由 View 在捕获到合法按键时调用，立即更新显示并存储新的快捷键组合。
    /// </summary>
    public void RecordKey(Key key, ModifierKeys modifiers)
    {
        _pendingModifiers = (int)modifiers;
        _pendingKey = (int)key;
        // 立即刷新显示文本，不等待失焦事件
        UpdateHotkeyDisplay();
    }

    [RelayCommand]
    private void ClearHotkey()
    {
        _pendingModifiers = 0;
        _pendingKey = (int)Key.None;
        UpdateHotkeyDisplay();
    }

    [RelayCommand]
    private void Save()
    {
        SettingsService.WriteStartupEnabled(IsStartupEnabled);
        SettingsService.WriteHotkey((ModifierKeys)_pendingModifiers, (Key)_pendingKey);
        SettingsService.WriteTheme(SelectedThemeIndex);
        SettingsService.WriteSaveDirectory(SaveDirectory);
        SettingsService.WriteAutoSave(IsAutoSaveEnabled);
        SettingsService.WriteRecordingTempDirectory(RecordingTempDirectory);
        var gpuDeviceId = SelectedGpuDevice?.DeviceId ?? AiGpuSettings.AutoDeviceId;
        SettingsService.WriteAiGpuDeviceId(gpuDeviceId);
        AiGpuSettings.Apply(gpuDeviceId);
        _confirmedGpuDeviceId = gpuDeviceId;
        SettingsService.WriteOcrModelTier((OcrModelTier)SelectedOcrModelIndex);
        OcrSettings.Apply((OcrModelTier)SelectedOcrModelIndex);
        _confirmedOcrModelIndex = SelectedOcrModelIndex;
        SettingsService.WriteMattingModel((MattingModel)SelectedMattingModelIndex);
        SettingsService.WriteSuperResolutionModel((SuperResolutionModel)SelectedSuperResolutionModelIndex);
        SettingsService.WriteSegmentationModel((SegmentationModel)SelectedSegmentationModelIndex);
        SettingsService.WriteVisionModel((VisionModel)SelectedVisionModelIndex);
        AiFeatureSettings.Apply(
            (MattingModel)SelectedMattingModelIndex,
            (SuperResolutionModel)SelectedSuperResolutionModelIndex,
            (SegmentationModel)SelectedSegmentationModelIndex,
            (VisionModel)SelectedVisionModelIndex);
        _confirmedMattingModelIndex = SelectedMattingModelIndex;
        _confirmedSuperResolutionModelIndex = SelectedSuperResolutionModelIndex;
        _confirmedSegmentationModelIndex = SelectedSegmentationModelIndex;
        _confirmedVisionModelIndex = SelectedVisionModelIndex;
        Log.Information("设置已保存: 开机启动={Startup}, 快捷键={Modifiers}+{Key}, 主题={Theme}, AI GPU={Gpu}, OCR={OcrTier}, 抠图={Matting}, 超分={Sr}, 分割={Seg}, 读图={Vision}, 保存目录={SaveDir}, 自动保存={AutoSave}, 录屏目录={RecDir}",
            IsStartupEnabled, (ModifierKeys)_pendingModifiers, (Key)_pendingKey, SelectedThemeIndex, gpuDeviceId, (OcrModelTier)SelectedOcrModelIndex, (MattingModel)SelectedMattingModelIndex, (SuperResolutionModel)SelectedSuperResolutionModelIndex, (SegmentationModel)SelectedSegmentationModelIndex, (VisionModel)SelectedVisionModelIndex, SaveDirectory, IsAutoSaveEnabled, RecordingTempDirectory);
        WeakReferenceMessenger.Default.Send(new HotkeyChangedMessage((ModifierKeys)_pendingModifiers, (Key)_pendingKey));
        _confirmedThemeIndex = SelectedThemeIndex;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void OpenAiModels() => AiModelMissingPrompt.OpenModelManager();

    [RelayCommand]
    private void BrowseSaveDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择保存目录"
        };
        if (dialog.ShowDialog() == true)
        {
            SaveDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void BrowseRecordingTempDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择录屏临时文件目录"
        };
        if (dialog.ShowDialog() == true)
        {
            RecordingTempDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void OpenSaveDirectory() => OpenDirectory(SaveDirectory, "保存目录");

    [RelayCommand]
    private void OpenRecordingTempDirectory() => OpenDirectory(RecordingTempDirectory, "录屏临时目录");

    [RelayCommand]
    private void Cancel()
    {
        RevertUnsavedChanges();
        RequestClose?.Invoke();
    }

    private void UpdateHotkeyDisplay() =>
        HotkeyDisplayText = HotkeyDisplayFormatter.FormatForDisplay((ModifierKeys)_pendingModifiers, (Key)_pendingKey);

    private static void OpenDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            AppMessageBox.Show($"尚未设置{label}。", "PixSnap", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Directory.Exists(path))
        {
            AppMessageBox.Show($"{label}不存在：\n{path}", "PixSnap", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开{Label}失败: {Path}", label, path);
            AppMessageBox.Show($"无法打开{label}：\n{ex.Message}", "PixSnap", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
