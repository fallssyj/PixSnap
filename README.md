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

## 功能概览

### 截图

| 模式 | 操作 | 说明 |
| --- | --- | --- |
| 窗口截图 | 左键单击 | 悬停高亮目标窗口，一键捕获 |
| 矩形截图 | 左键拖动 | 像素级框选，支持多显示器 |
| 全屏截图 | `Space` | 截取当前显示器 |
| 退出选区 | `Esc` / 右键 | 取消本次截图 |

默认全局快捷键为 `Ctrl + Shift + Q`，可在设置中自定义。应用常驻系统托盘，支持开机启动。

### 录屏

- **捕获模式**：窗口 / 矩形 / 全屏
- **编码**：H.264 视频 + AAC 音频（系统声音 & 麦克风可选）
- **画质**：标准 / 高 / 原画，码率随分辨率动态调整
- **控制**：暂停 / 恢复、排除录制窗口自身、磁盘空间预警
- **临时文件**：录制过程写入设置的「录屏临时目录」；预览窗口关闭后自动删除当次临时文件，保存则复制到您选择的位置
- 录屏进行中仍可进入截图选区，互不干扰

### 图片编辑

预览窗口提供完整的后期编辑流程：

| 类别 | 能力 |
| --- | --- |
| 基础 | 裁剪（自由 / 预设比例 / 智能裁剪）、圆角、顺时针旋转 90° |
| 标注 | 选择、箭头、矩形、椭圆、文字、画笔、马赛克、序号 |
| AI 擦除 | 涂抹不需要的区域，LaMa 智能修复 |
| OCR | PaddleOCR 离线识别，支持复制文字、多栏排版 |
| 导出 | 撤销 / 重做、复制到剪贴板、保存 PNG / JPEG / BMP |

编辑时按住 `Space` 可临时切换抓手平移；标注工具支持单键快捷切换（`V` `A` `R` `E` `T` `P` `M`）。

### AI 本地推理

所有 AI 能力通过 **ONNX Runtime + DirectML** 在本地 GPU 上运行，数据不上传、无需联网。

| 功能 | 模型 | 说明 |
| --- | --- | --- |
| 扫描文本 | PP-OCRv5 Mobile / Server | 检测 + 识别，Mobile 轻量、Server 高精度 |
| 背景去除 | RMBG-1.4 / BiRefNet FP16 | 一键抠图，可在设置中切换 |
| 超分辨率 | Real-ESRGAN x4plus | 2× / 4× 放大增强 |
| AI 擦除 | LaMa FP32 | 涂抹后智能修复 |

可在 **设置 → AI** 中选择 GPU 设备、OCR 规格与抠图 / 超分模型；**模型管理** 中按需下载 ONNX 文件。

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

从 [Releases](https://github.com/fallssyj/PixSnap/releases) 下载 `PixSnap-Setup-x64.exe` 并运行安装程序。

| 路径 | 说明 |
| --- | --- |
| `%LocalAppData%\Programs\PixSnap` | 程序安装目录（无需管理员权限） |
| `%LocalAppData%\PixSnap` | 日志、设置、下载的 AI 模型 |
| 文档\PixSnap（默认） | 录屏临时文件目录，可在设置中修改 |

安装包为自包含发布，目标机无需单独安装 .NET。卸载时会清理 `%LocalAppData%\PixSnap` 下的用户数据；若程序正在运行会提示先退出。

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

需要额外安装 [Inno Setup 6](https://jrsoftware.org/isdl.php)（提供 `ISCC.exe`）。

```powershell
# 一键发布 + 打包（自包含运行时）
powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1

# 或双击运行
scripts/build-installer.bat
```

输出：`installer/output/PixSnap-Setup-{版本}-x64.exe`（版本号取自 `PixSnap.csproj`）

安装包特性：

- 默认安装到 `%LocalAppData%\Programs\PixSnap`（无需管理员权限）
- 已安装时可选继续安装、卸载或取消；禁止安装低于已装版本的旧包
- 日志、设置、下载的 AI 模型写入 `%LocalAppData%\PixSnap\`
- 卸载时会删除 `%LocalAppData%\PixSnap\`；若程序正在运行会提示先退出
- 开始菜单快捷方式，可选桌面图标
- 自带 .NET 10 运行时，含 `NativeScreenCapturer.dll` 等原生依赖

若已安装 Inno Setup 但脚本找不到，可设置环境变量：

```powershell
$env:PIXSNAP_ISCC = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
```

### AI 模型

首次使用 AI 功能时，应用会提示下载对应 ONNX 模型。也可在「设置 → AI → 模型管理」中手动下载。模型默认保存至 `%LocalAppData%\PixSnap\onnx\`。

| 文件 | 用途 | 约计大小 |
| --- | --- | --- |
| `onnx/rmbg-1.4.onnx` | 背景去除（默认） | ~176 MB |
| `onnx/birefnet-fp16.onnx` | 背景去除（高精度） | ~490 MB |
| `onnx/realesrgan-x4plus.onnx` | 超分辨率 | ~67 MB |
| `onnx/lama_fp32.onnx` | AI 擦除 | ~210 MB |
| `onnx/ocr/ch_PP-OCRv5_mobile_*.onnx` | OCR Mobile 档 | ~21 MB |
| `onnx/ocr/ch_PP-OCRv5_server_*.onnx` | OCR Server 档 | ~172 MB |
| `onnx/ocr/ppocrv5_dict.txt` | 字符字典 | 随程序内置 |

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

## 免责声明

PixSnap 由作者**免费**发布，**仅供个人学习与非商业使用**，按「原样」提供，不提供任何明示或暗示的保证。

- **官方渠道**：作者仅通过本 GitHub 仓库及 [Releases](https://github.com/fallssyj/PixSnap/releases) 免费发布，**未授权**任何第三方以收费、捆绑、代装或其他获利方式分发本软件。
- **禁止倒卖**：未经授权，不得将本软件、其安装包或衍生品进行售卖、倒卖或用于其他营利活动。
- **付费来源**：若您通过付费途径获得本软件，该来源与作者无关，作者不对其安全性、完整性及后续支持承担任何责任。
- **使用风险**：您使用本软件及 AI 功能的风险由您自行承担；识别、抠图、修复等结果仅供参考，作者不对其准确性或由此造成的任何损失承担责任。
- **隐私**：AI 推理在本地完成，默认不上传您的图片与数据。
- **第三方组件**：本软件包含第三方开源组件与模型，相关权利归各自权利人所有。

---

## 反馈

- 仓库：[github.com/fallssyj/PixSnap](https://github.com/fallssyj/PixSnap)
- 问题反馈：[GitHub Issues](https://github.com/fallssyj/PixSnap/issues)

---

<p align="center">
  Made by <a href="https://github.com/fallssyj">fallssyj</a><br/>
  Copyright © 2026 fallssyj
</p>
