<p align="center">
  <img src="src/PixSnap/PixSnap/Assets/icons/app.png" width="96" alt="PixSnap Logo" />
</p>

<h1 align="center">PixSnap</h1>

<p align="center">
  <strong>截图 · 录屏 · 标注 · OCR · 本地 AI 编辑</strong>
</p>

<p align="center">
  基于 Windows Graphics Capture 与 ONNX 本地推理的 Windows 截图 / 录屏工具。<br/>
  WPF + iNKORE UI.WPF.Modern 构建，Fluent 2 风格界面，支持 Windows 10 19041 及以上。
</p>

<p align="center">
  <a href="https://github.com/fallssyj/PixSnap">GitHub</a> ·
  <a href="https://github.com/fallssyj/PixSnap/releases">下载</a> ·
  <a href="https://github.com/fallssyj/PixSnap/issues">反馈问题</a>
</p>

---

## 目录

- [快速开始](#快速开始)
- [截图](#截图)
- [录屏](#录屏)
- [Windows Graphics Capture 说明](#windows-graphics-capture-说明)
- [图片编辑](#图片编辑)
- [AI 本地推理](#ai-本地推理)
- [设置项说明](#设置项说明)
- [数据与路径](#数据与路径)
- [系统要求](#系统要求)
- [下载与安装](#下载与安装)
- [构建](#构建)
- [项目结构](#项目结构)
- [许可证与免责声明](#许可证)

---

## 快速开始

1. 安装后 PixSnap 常驻**系统托盘**，主窗口默认隐藏。
2. 按全局快捷键 **`Ctrl + Shift + Q`**（可在设置中修改）或右键托盘 → **截图/录屏**，进入全屏选区界面。
3. 截图完成后自动**复制到剪贴板**，并弹出 10 秒通知；点击通知或托盘 → **打开预览窗口** 进入编辑。
4. 在选区界面按 **`Tab`** 可切换 **截屏 / 录屏** 模式；录屏结束后在预览窗口保存 MP4。

| 入口 | 说明 |
| --- | --- |
| 全局快捷键 | 默认 `Ctrl + Shift + Q`，触发截图/录屏选区 |
| 托盘右键 | 截图/录屏、打开预览、设置、日志、关于、退出 |
| 托盘双击 | 默认打开预览窗口（可在设置中改为直接截图） |
| 开机启动 | 设置中开启，写入注册表 `Run` 键 |

程序为**单实例**运行；启动时会清理 7 天前的 `recording_*.mp4` 临时文件与过期日志。

---

## 截图

### 触发与流程

按下快捷键或托盘菜单后（`MainViewModel.StartCapture`）：

1. **并行预截图**：对各显示器发起 WGC 全屏捕获（不阻塞 UI）。
2. **窗口快照**：记录当前所有可见窗口的 Z 序与屏幕矩形。
3. 隐藏主窗口，等待 120 ms 后弹出**全屏选区界面**（覆盖虚拟桌面）。
4. 选区界面以预截图作为背景，展示截图瞬间的桌面状态。
5. 确认后：
   - 若所有显示器预截图在 **800 ms** 内就绪 → 从预截图中**裁剪**目标区域；
   - 否则 → 回退为**实时 WGC 捕获**。
6. 结果自动写入剪贴板，并发送通知。

若已有预览窗口正在编辑图片，新截图会**另开预览窗口**，不会覆盖当前编辑内容。

### 三种模式

| 模式 | 选区操作 | 捕获方式 |
| --- | --- | --- |
| 窗口 | 鼠标悬停高亮，**左键单击** | 优先从预截图按窗口矩形裁剪；失败时 `CaptureWindowAsync` |
| 矩形 | **左键拖动**框选 | 优先从预截图裁剪；失败时 `CaptureRegionAsync` |
| 全屏 | 鼠标悬停某显示器后按 **`Space`** | 优先使用该显示器的预截图；失败时 `CaptureFullScreenAsync` |

### 选区界面快捷键

| 按键 | 作用 |
| --- | --- |
| `Tab` | 切换截屏 / 录屏（录屏进行中时禁用录屏切换） |
| `Space` | 全屏截取当前悬停的显示器 |
| `Esc` / 右键 | 取消 |

**边缘吸附**：拖动矩形时，选区四边会在 **8 px** 范围内吸附到窗口或屏幕边缘。

**窗口命中**（截图模式）：使用进入选区前的窗口快照 Z 序，避免选区界面抢焦点导致目标变化。

**录屏进行中截图**：允许在录制时再次进入选区截图，录制不会中断；此时选区器仅允许截图，不能启动第二次录屏。

---

## 录屏

### 选区与默认选项

录屏与截图共用同一选区界面。切换到录屏模式（`Tab`）后，底部出现录屏选项：

| 选项 | 默认值 |
| --- | --- |
| 画质 | **原画** |
| 系统声音 | **开启** |
| 麦克风 | **关闭** |

窗口 / 矩形 / 全屏三种捕获模式与截图相同；**窗口录屏**使用实时窗口句柄检测（需要目标窗口仍然存在）。

### 画质与码率

码率以 **1080p（1920×1080 = 2,073,600 像素）** 为基准；录制区域像素数超过 1080p 时，按像素数**线性放大**（`CaptureSelection.VideoBitrate`）。

| 画质 | 1080p 基准码率 | 选区 UI 标签 |
| --- | --- | --- |
| 标准 | 4 Mbps | 标清 |
| 高 | 12 Mbps | 高清 |
| 原画（默认） | 24 Mbps | 原画 |

例如 4K（3840×2160，约为 1080p 的 4 倍）时，「原画」实际码率约为 **96 Mbps**。

### 编码与音频

原生层（`NativeScreenCapturer/ScreenCapturer.cpp`）通过 Media Foundation 输出 MP4：

| 项目 | 参数 |
| --- | --- |
| 视频 | H.264，**30 fps**，RGB32 输入 |
| 音频 | AAC；可选系统声音（WASAPI Loopback）与麦克风，混音后统一为 **48 kHz / 16-bit / 立体声** |
| 输出尺寸 | 宽、高对齐为偶数 |

优先尝试硬件编码，失败时回退软件编码。暂停 / 恢复期间跳过帧与音频写入。

### 录制控制

录制开始后弹出**浮动控制条**（`RecordingControlWindow`）：

- 显示已录制时长（1 秒刷新）；每 **5 秒**检查输出盘剩余空间。
- 剩余空间 **< 100 MB** 时自动暂停。
- 控制条通过 `SetWindowDisplayAffinity(WdaExcludeFromCapture)` **排除在录制画面之外**。
- 支持暂停 / 恢复、停止；停止后打开视频预览窗口。

若请求了音频但初始化失败，控制条会显示无声警告，视频仍继续录制。

### 临时文件与保存

| 阶段 | 行为 |
| --- | --- |
| 录制中 | 写入 `{录屏临时目录}/recording_{Guid}.mp4` |
| 预览关闭 | 自动删除当次临时文件（最多重试 3 次） |
| 用户保存 | 复制到自选路径，默认文件名 `PixSnap_yyyyMMdd_HHmmss.mp4` |
| 启动清理 | 删除临时目录中 **7 天前**的 `recording_*.mp4` |

默认录屏临时目录：`%UserProfile%\Documents\PixSnap`（可在设置中修改）。

---

## Windows Graphics Capture 说明

PixSnap 的截图与录屏均基于 **Windows Graphics Capture（WGC）** API（C++/CLI 组件 `NativeScreenCapturer`），由 Direct3D 11 提供 GPU 加速捕获，而非 GDI 桌面位图拷贝。

### 三种模式对比

| 模式 | WGC 捕获对象 | 输出内容 |
| --- | --- | --- |
| 全屏 | 显示器（Monitor） | 当前显示器上所有可见内容的合成画面 |
| 矩形 | 显示器（按区域裁剪 / 拼接） | 虚拟桌面上指定矩形范围内的像素 |
| **窗口** | **指定窗口（Window）** | **仅目标窗口自身的渲染内容** |

### 窗口模式的行为

选择**窗口录屏**时，程序通过 WGC 的 `CreateForWindow` 绑定到窗口句柄：

- **只针对目标窗口**：输出的是窗口应用自身绘制的内容。其他窗口叠在上方时，**不会**把遮挡物录进结果——这与「截取屏幕上窗口所在矩形」的传统做法不同。
- **跟随窗口**：目标窗口移动或改变大小时，录制画面随之调整；最小化、关闭或尺寸为 0 时可能无法继续捕获。
- **需要有效窗口**：录屏选区时实时检测句柄；截图选区则使用进入选区前的窗口位置快照。

**窗口截图**的预截取路径：从全屏预截图中按窗口矩形裁剪，保留您触发快捷键时的桌面状态；预截图失败时回退为 WGC 窗口直采（与录屏相同，仅输出窗口自身内容）。

### 截图 vs 录屏的 WGC 差异

| 项目 | 截图 | 录屏 |
| --- | --- | --- |
| 鼠标光标 | 不捕获 | 捕获 |
| 窗口黄色边框（Win11+） | 不显示 | 不显示 |
| 单帧等待超时 | 2500 ms | 持续帧回调（帧池缓冲 2 帧） |

### 兼容限制

- 部分受保护内容（DRM 视频、硬件加速全屏游戏等）或特殊系统窗口可能无法捕获。
- 窗口枚举过滤：不可见、最小化、被 DWM 隐藏（`DWMWA_CLOAKED`）、无标题的窗口不会出现在窗口列表中。
- 此类场景请改用**全屏**或**矩形**模式。

> 最低系统要求 Windows 10 Build **19041**（Version 2004），启动时会检测 WGC 可用性。

---

## 图片编辑

截图完成后在**预览窗口**中编辑。编辑模式互斥：裁剪、圆角、AI 擦除、标注。

### 基础编辑

| 功能 | 说明 |
| --- | --- |
| 缩放 | 滚轮上下滚动放大 / 缩小（以光标为中心，0.1×–8.0×）；工具栏可切换适应窗口 / 实际大小 |
| 平移 | 按住 `Space` 进入平移模式，再按住左键拖动画布 |
| 裁剪 | 自由 / 1:1 / 16:9 / 4:3 / 3:2 预设比例；**智能裁剪**自动检测前景外接矩形 |
| 圆角 | 可调圆角半径（默认 20 px），SkiaSharp 后台处理 |
| 旋转 | 顺时针 90° |
| 撤销 / 重做 | 全局历史 + 标注独立撤销栈 |

### 标注工具

| 工具 | 快捷键 | 说明 |
| --- | --- | --- |
| 选择 / 移动 | `V` | 选中、拖动、调整大小 |
| 箭头 | `A` | |
| 矩形 | `R` | 支持圆角、填充 |
| 椭圆 | `E` | 支持填充 |
| 文本 | `T` | 可调字号、背景 |
| 画笔 | `P` | 自由曲线 |
| 模糊 | `M` | 可调半径；可切换**马赛克**模式 |

通用操作：`Ctrl+D` 复制选中标注；`Enter` 应用标注；`Esc` 取消选中或退出标注模式。

默认描边颜色红色，线宽 10 px；模糊默认半径 10 px。

### OCR

- 引擎：**PP-OCRv5**（Mobile 或 Server，在设置中选择）。
- 方向分类（CLS）**未启用**，无需下载 CLS 模型即可使用。
- 检测最大边长：Mobile **1920** px，Server **2560** px；长边 < 2560 时自动 2× 放大后识别。
- 识别结果叠加在图片上，支持复制全部文字（多栏排版合并）；`Esc` 退出 OCR 叠加层。

### AI 功能（预览工具栏）

| 功能 | 模型 | 说明 |
| --- | --- | --- |
| 背景去除 | RMBG-1.4 / BiRefNet FP16 | 设置中切换；一键抠图 |
| 超分辨率 | Real-ESRGAN x4plus | 4× 直接放大；2× 先半分辨率推理再 x4 模型 |
| AI 擦除 | LaMa FP32 | 涂抹后智能修复；默认笔刷 30 px |

超分输出上限 **128,000,000** 像素；分块推理 tile 256 px，padding 16 px。

### 导出

| 操作 | 说明 |
| --- | --- |
| 复制 | 复制到剪贴板 |
| 保存 | PNG / JPEG / BMP；默认文件名 `PixSnap_yyyyMMdd_HHmmss.{ext}` |
| 自动保存 | 设置中开启后，截图完成即保存到指定目录 |
| 打开 / 拖放 | 支持 PNG、JPG、JPEG、WEBP、BMP |

预览窗口关闭时会释放大图引用并整理内存。

---

## AI 本地推理

所有 AI 能力通过 **ONNX Runtime + DirectML** 在本地运行；推理过程**不上传**图片数据。模型文件需首次使用时下载（或提前在模型管理中下载）。

### 模型清单

模型保存至 `%LocalAppData%\PixSnap\onnx\`（安装目录下的 `onnx\` 为备用查找路径）。

| 文件 | 用途 | 约计大小 |
| --- | --- | --- |
| `ocr/ch_PP-OCRv5_mobile_det.onnx` | OCR Mobile · 检测 | ~4.8 MB |
| `ocr/ch_PP-OCRv5_mobile_rec_infer.onnx` | OCR Mobile · 识别 | ~16 MB |
| `ocr/ch_PP-OCRv5_server_det.onnx` | OCR Server · 检测 | ~88 MB |
| `ocr/ch_PP-OCRv5_server_rec_infer.onnx` | OCR Server · 识别 | ~84 MB |
| `ocr/ch_ppocr_mobile_v2.0_cls_infer.onnx` | 方向分类（可选，当前未启用） | ~2.1 MB |
| `ocr/ppocrv5_dict.txt` | 字符字典（OCR 共用） | ~74 KB，需在模型管理中下载（GitHub 源，不受 HF 镜像设置影响） |
| `rmbg-1.4.onnx` | 背景去除（默认） | ~176 MB |
| `birefnet-fp16.onnx` | 背景去除（高精度） | ~490 MB |
| `realesrgan-x4plus.onnx` | 超分辨率 | ~67 MB |
| `lama_fp32.onnx` | AI 擦除 | ~210 MB |

### 下载

- 入口：**设置 → AI → 管理 ONNX 模型下载**。
- 镜像：默认 **hf-mirror.com**（国内镜像），可切换 **huggingface.co** 官方源。
- 下载超时 45 分钟；先写入 `*.download` 临时文件，完成后原子替换。
- Hugging Face URL 按镜像设置自动改写；GitHub raw 等非 HF 地址不变。

### GPU 设备

| 设置值 | 含义 |
| --- | --- |
| 自动（默认，deviceId = -2） | 优先独显，DirectML 自动选择 |
| 仅 CPU（-1） | 不使用 DirectML |
| 指定 GPU（≥ 0） | 固定 DirectML 设备索引 |

切换 GPU 后会重建 OCR 会话。自动模式下用 CLS 模型探测设备可用性（超时 45 秒）。

---

## 设置项说明

所有 JSON 设置存储于 `%LocalAppData%\PixSnap\settings.json`（schema v8），原子写入（`.tmp` → 重命名）。损坏时会备份为 `settings.json.corrupted`。

| 设置 | 默认值 | 可选值 |
| --- | --- | --- |
| 全局快捷键 | `Ctrl + Shift + Q` | 任意组合；清除为 `None` 即禁用 |
| 主题 | 跟随系统 | 跟随系统 / 深色 / 浅色 |
| 窗口背景 | Mica | Mica / Acrylic / Tabbed / Acrylic（Win10）/ Acrylic（Win11）/ 无 |
| 更新源 | **Gitee** | GitHub / Gitee |
| 启动时检查更新 | 关闭 | |
| 模型下载镜像 | 国内镜像 | hf-mirror.com / huggingface.co |
| 开机启动 | 关闭 | 写入 `HKCU\...\Run\PixSnap` |
| 截图保存目录 | `%UserProfile%\Pictures` | |
| 自动保存截图 | 关闭 | |
| 录屏临时目录 | `%UserProfile%\Documents\PixSnap` | |
| AI GPU 设备 | 自动（优先独显） | 自动 / 仅 CPU / 指定 GPU |
| OCR 规格 | Mobile | Mobile / Server |
| 抠图模型 | RMBG-1.4 | RMBG-1.4 / BiRefNet FP16 |
| 超分倍率 | 4× | 2× / 4× |
| 托盘双击 | 打开预览窗口 | 截图/录屏 / 打开预览窗口 |
| 日志保留天数 | 7 天 | 1–365 天 |

主题与窗口背景在设置中可实时预览；点**取消**或关闭窗口会撤销未保存的更改。保存后会重新注册全局快捷键。

### 检查更新

- GitHub：`api.github.com/repos/fallssyj/PixSnap/releases/latest`
- Gitee（默认）：`gitee.com/api/v5/repos/falls_syj/PixSnap/releases/latest`
- 发现新版本时可应用内下载（保存至 `%TEMP%\PixSnap\`）或浏览器打开发行页。

---

## 数据与路径

| 路径 | 用途 |
| --- | --- |
| `%LocalAppData%\Programs\PixSnap` | 程序安装目录（安装包默认，无需管理员） |
| `%LocalAppData%\PixSnap\` | 用户数据根目录 |
| `%LocalAppData%\PixSnap\settings.json` | 应用设置 |
| `%LocalAppData%\PixSnap\logs\{日期}\pixsnap.log` | 运行日志（Information 及以上） |
| `%LocalAppData%\PixSnap\onnx\` | 下载的 AI 模型 |
| `%UserProfile%\Pictures\` | 默认截图保存目录 |
| `%UserProfile%\Documents\PixSnap\` | 默认录屏临时目录 |
| `%TEMP%\PixSnap\` | 更新安装包下载目录 |
| `{安装目录}\onnx\` | 内置 / 旧版模型备用路径 |

首次启动会将旧版 `{安装目录}\settings.json` 迁移到 LocalAppData。卸载安装包时会删除 `%LocalAppData%\PixSnap\` 下的用户数据；若程序正在运行会提示先退出。

---

## 系统要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 Build 19041（Version 2004）及以上 |
| 运行时 | 安装包自带 .NET 10；从源码运行需 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) |
| 平台 | x64 |
| GPU | 支持 DirectX 11 的显卡（AI 功能推荐 DirectML 兼容 GPU） |

---

## 下载与安装

从 [Releases](https://github.com/fallssyj/PixSnap/releases) 下载 `PixSnap-Setup-{版本}-x64.exe` 并运行安装程序。

安装包特性：

- 自包含 .NET 10 运行时，含 `NativeScreenCapturer.dll` 等原生依赖
- 默认安装到 `%LocalAppData%\Programs\PixSnap`（无需管理员权限）
- 已安装时可选继续安装、卸载或取消；**禁止**安装低于已装版本的旧包
- 开始菜单快捷方式，可选桌面图标
- 安装许可页展示免责声明（与「关于 → 查看全文」内容同步）

---

## 构建

### 前置条件

1. **Visual Studio 2022** 或更新（v17.x / v18.x）
   - 工作负载：**使用 .NET 的桌面开发** + **使用 C++ 的桌面开发**
2. **.NET 10 SDK**（`net10.0-windows`）
3. 构建平台选 **x64**

### 编译

```powershell
git clone https://github.com/fallssyj/PixSnap.git
cd PixSnap

msbuild src/PixSnap/PixSnap.sln /p:Configuration=Release /p:Platform=x64 /m
```

输出目录：`src/PixSnap/PixSnap/bin/x64/Release/net10.0-windows/`

### 制作安装包

需要额外安装 [Inno Setup 6](https://jrsoftware.org/isdl.php)（提供 `ISCC.exe`）与 [7-Zip](https://www.7-zip.org/)（提供 `7z.exe`）。

```powershell
# 一键发布 + 打包（自包含运行时）
powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1

# 或双击运行
scripts/build-installer.bat
```

输出（`installer/output/`，版本号取自 `PixSnap.csproj`）：

| 文件 | 说明 |
| --- | --- |
| `PixSnap-Setup-{版本}-x64.exe` | Inno Setup 安装包 |
| `PixSnap-{版本}-x64-portable.7z` | 绿色版压缩包，解压到任意文件夹即可运行，无需安装 |

若已安装 Inno Setup 但脚本找不到，可设置环境变量：

```powershell
$env:PIXSNAP_ISCC = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
$env:PIXSNAP_7Z = "C:\Program Files\7-Zip\7z.exe"
```

---

## 项目结构

```
src/PixSnap/
├── NativeScreenCapturer/       # C++/CLI 原生捕获组件
│   ├── ScreenCapturer.cpp      #   WGC 截图 / MF 录制 / WASAPI 音频
│   └── DirectXHelper.h         #   D3D11 / DXGI 辅助
├── RapidOCRLib/                # PaddleOCR ONNX 推理封装
├── PixSnap/                    # WPF 主程序
│   ├── Views/                  #   窗口（预览、设置、录屏控制等）
│   ├── ViewModels/             #   MVVM ViewModel
│   ├── Models/                 #   数据模型与消息
│   ├── Services/               #   捕获、AI 推理、设置、导航
│   ├── Controls/               #   自定义控件与工具面板
│   ├── Converters/             #   值转换器
│   ├── Behaviors/              #   交互行为
│   └── Styles/                 #   Fluent 主题与排版
├── installer/                  # Inno Setup 脚本与语言包
└── scripts/                    # 构建与打包脚本
```

### 技术栈

| 组件 | 版本 | 用途 |
| --- | --- | --- |
| iNKORE.UI.WPF.Modern | 0.10.2.1 | Fluent 2 主题、Mica 窗口、现代控件 |
| iNKORE.UI.WPF | 1.2.8 | iNKORE WPF 基础库 |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM 框架 |
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | AI 模型推理 |
| SkiaSharp | 3.119.2 | 图像处理 |
| Serilog | 4.3.1 | 结构化日志 |
| Hardcodet.NotifyIcon.Wpf | 2.0.1 | 系统托盘 |

### 原生依赖

Direct3D 11 · DXGI · DWM · Media Foundation · WASAPI · Windows.Graphics.Capture

---

## 许可证

PixSnap 以 [GNU General Public License v3.0（GPLv3）](LICENSE) 发布。你可以自由使用、修改和分发本软件，但衍生作品须同样以 GPLv3 开源，并保留版权声明与许可全文。

本软件 bundled 的第三方库与 AI 模型各自适用其原始许可，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 与「关于 → 免责声明」。

### 免责声明

PixSnap 以 **GPLv3** 开源发布，按「原样」提供。完整条款见应用内「免责声明」及安装许可页。

- **开源再分发**：在遵守 GPLv3（含提供源代码等义务）的前提下，允许修改与再分发；作者官方免费渠道为 [GitHub Releases](https://github.com/fallssyj/PixSnap/releases) 及 Gitee 镜像。
- **AI 模型**：模型版权与许可独立于本软件；使用前须自行阅读各模型许可（RMBG-1.4 等存在非商用限制）。
- **合法使用**：禁止将本软件用于违法或侵权用途；相关法律责任由使用者自行承担。
- **非官方渠道**：通过付费或不明来源获得的安装包与作者无关，作者不对其安全性与完整性负责。
- **使用风险与隐私**：AI 结果仅供参考；核心功能本地运行，默认不上传您的图片与数据。

详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

---

## 反馈

- 仓库：[github.com/fallssyj/PixSnap](https://github.com/fallssyj/PixSnap)
- 问题反馈：[GitHub Issues](https://github.com/fallssyj/PixSnap/issues)

---

<p align="center">
  Made by <a href="https://github.com/fallssyj">fallssyj</a><br/>
  Copyright © 2026 fallssyj
</p>
