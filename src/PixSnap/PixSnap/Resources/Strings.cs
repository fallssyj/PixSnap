namespace PixSnap.Resources;

/// <summary>
/// 集中管理所有面向用户的中文字符串（C# 侧）。
/// 后续做 i18n 时，可替换为 .resx 或其他本地化方案。
/// </summary>
internal static class S
{
    // ── 通用 ──────────────────────────────────────────────────────────────
    public const string AppName = "PixSnap";
    public const string Error_PixSnap = "PixSnap 错误";
    public const string Done = "完成";

    // ── App 级别 ─────────────────────────────────────────────────────────
    public const string App_AlreadyRunning = "PixSnap 已在运行中，请查看系统托盘。";
    public const string App_StartupFailed = "PixSnap 启动失败";
    public const string App_PreviewOpenFailed = "预览窗口打开失败";
    public const string App_Exception = "应用异常";
    public const string App_BackgroundTaskException = "后台任务异常";
    public const string App_StartupFailedDetail = "应用启动失败";

    // ── 截图模式 ─────────────────────────────────────────────────────────
    public const string Capture_UnsupportedMode = "不支持的截图模式。";
    public const string Capture_Failed = "截图失败";

    // ── 区域选择器 ───────────────────────────────────────────────────────
    public const string Region_MoveToSelect = "移动鼠标以选择截图对象";
    public const string Region_Instruction = "悬停窗口后左键单击截窗口，按住左键拖动截矩形，Space 截当前显示器，Esc 或右键退出";
    public const string Region_DragToSelect = "拖动以选择截图区域";
    public const string Region_ClickWindow = "单击截取窗口: {0}";
    public const string Region_SpaceScreen = "按 Space 截取显示器 {0}";
    public const string Region_AreaInfo = "区域 X:{0:0} Y:{1:0} W:{2:0} H:{3:0}";

    // ── 屏幕信息 ─────────────────────────────────────────────────────────
    public const string Screen_DisplayName = "显示器 {0}  ({1} x {2})";

    // ── 设置 ─────────────────────────────────────────────────────────────
    public const string Settings_PressHotkey = "请按下快捷键...";
    public const string Settings_None = "无";

    // ── 裁剪 ─────────────────────────────────────────────────────────────
    public const string Crop_Free = "自由";
    public const string Crop_CurrentRatio = "当前比例: {0}";

    // ── 预览窗口 ─────────────────────────────────────────────────────────
    public const string Preview_FitToWindow = "缩放以适应";
    public const string Preview_ActualSize = "缩放以原始";
    public const string Preview_ZoomFormat = "缩放 {0:P0}";
    public const string Preview_Cropping = "正在裁剪...";
    public const string Preview_RoundingCorners = "正在处理圆角...";
    public const string Preview_Rotating = "正在旋转图片...";

    // ── 文件操作 ─────────────────────────────────────────────────────────
    public const string File_OpenTitle = "打开图片";
    public const string File_OpenFilter = "图片文件|*.png;*.jpg;*.jpeg|PNG 文件|*.png|JPEG 文件|*.jpg;*.jpeg";
    public const string File_Loading = "正在加载图片...";
    public const string File_LoadDone = "图片加载完成";
    public const string File_LoadFailed = "加载失败：{0}";
    public const string File_SaveTitle = "保存截图";
    public const string File_SaveFilter = "PNG 文件|*.png";
    public const string File_Saving = "正在保存图片...";
    public const string File_SaveDone = "图片保存完成";
    public const string File_SaveFailed = "保存失败：{0}";

    // ── AI · 背景移除 ───────────────────────────────────────────────────
    public const string Bg_PreparingRemoval = "正在准备去除背景...";
    public const string Bg_RemovalDone = "去除背景完成";
    public const string Bg_RemovalFailed = "去除背景失败：{0}";
    public const string Bg_LoadingModel = "正在加载去背景模型...";
    public const string Bg_BuildingTensor = "正在构建输入张量...";
    public const string Bg_RunningInference = "正在执行去背景推理...";
    public const string Bg_RestoringMask = "正在还原掩码分辨率...";
    public const string Bg_Compositing = "正在合成透明背景...";

    // ── AI · 超分辨率 ───────────────────────────────────────────────────
    public const string Sr_PreparingSuperRes = "正在准备超分辨率...";
    public const string Sr_SuperResDone = "超分辨率完成";
    public const string Sr_SuperResFailed = "超分辨率失败：{0}";
    public const string Sr_LoadingModel = "正在加载超分模型...";
    public const string Sr_RunningInference = "正在执行超分推理...";
    public const string Sr_GeneratingResult = "正在生成超分结果...";
    public const string Sr_PreScaling = "源图过大，正在预缩放至 {0}×{1}...";
    public const string Sr_TileProgress = "正在分块超分 ({0}/{1})...";

    // ── AI · 擦除 / 修复 ────────────────────────────────────────────────
    public const string Eraser_Preparing = "正在准备...";
    public const string Eraser_Cancelled = "已取消";
    public const string Eraser_Failed = "AI 处理失败：{0}";
    public const string Inpaint_LoadingModel = "正在加载模型...";
    public const string Inpaint_RoiFailed = "无法提取 AI 修复 ROI 区域";
    public const string Inpaint_GeneratingMask = "正在生成修复遮罩...";
    public const string Inpaint_Processing = "AI 处理中，请稍候...";
    public const string Inpaint_GeneratingResult = "正在生成结果图像...";
    public const string Inpaint_DmlFallback = "DirectML 推理失败，已回退至 CPU...";

    // ── AI · 通用推理 ───────────────────────────────────────────────────
    public const string Onnx_ModelNotFound = "未找到 ONNX 模型：{0}";
    public const string Onnx_InitEngine = "正在初始化推理引擎...";
    public const string Onnx_DeviceInfo = "当前推理设备：{0}";
    public const string Onnx_CpuFallback = "CPU(DML失败: {0})";
    public const string Onnx_Unknown = "未知";

    // ── 图片 IO ─────────────────────────────────────────────────────────
    public const string IO_ReadingFile = "正在读取文件...";
    public const string IO_Decoding = "正在解码图片...";
    public const string IO_PreparingDisplay = "正在准备显示...";
    public const string IO_Encoding = "正在编码图片...";
    public const string IO_WritingDisk = "正在写入磁盘...";
    public const string IO_FinishingSave = "正在完成保存...";
}
