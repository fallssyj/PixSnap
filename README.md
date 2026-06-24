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
| 运行时 | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) |
| 平台 | x64 |
| GPU | 支持 DirectX 11 的显卡（AI 功能推荐 DirectML 兼容 GPU） |

---

## 构建

### 前置条件

1. **Visual Studio 2022**（v17.x 或更新）
   - 工作负载：**使用 .NET 的桌面开发** + **使用 C++ 的桌面开发**（v145 平台工具集）
2. **.NET 10 SDK**（`net10.0-windows`）
3. 构建平台选 **x64**

### 编译

```powershell
git clone https://github.com/fallssyj/PixSnap.git
cd PixSnap

msbuild src/PixSnap/PixSnap.sln /p:Configuration=Release /p:Platform=x64 /m
```

输出目录：`src/PixSnap/PixSnap/bin/x64/Release/net10.0-windows/`

### AI 模型

首次使用 AI 功能时，应用会提示下载对应 ONNX 模型。也可在「设置 → AI → 模型管理」中手动下载。

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
└── PixSnap.sln
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

## 开源与反馈

- 仓库：[github.com/fallssyj/PixSnap](https://github.com/fallssyj/PixSnap)
- 问题反馈：[GitHub Issues](https://github.com/fallssyj/PixSnap/issues)

---

<p align="center">
  Made by <a href="https://github.com/fallssyj">fallssyj</a><br/>
  Copyright © 2026 fallssyj
</p>
