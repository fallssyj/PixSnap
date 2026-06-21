<p align="center">
  <img src="src/PixSnap/PixSnap/Assets/icons/app.png" width="96" alt="PixSnap Logo" />
</p>

<h1 align="center">PixSnap</h1>

<p align="center">
  <strong>截图 · 录屏 · 标注 · AI 编辑</strong>
</p>

<p align="center">
  一款基于 Windows Graphics Capture 与 ONNX AI 推理的现代化截图 / 录屏工具，<br/>
  使用 WPF + iNKORE UI.WPF.Modern 构建，支持 Windows 10 19041 及以上版本。
</p>

---

## 功能一览

### 截图

| 模式     | 说明                   | 操作     |
| -------- | ---------------------- | -------- |
| 窗口截图 | 悬停高亮，点击即可捕获 | 左键单击 |
| 矩形截图 | 拖拽框选，像素级精准   | 左键拖动 |
| 全屏截图 | 一键截取整个显示器     | Space    |

### 录屏

- 窗口 / 矩形 / 全屏三种模式
- H.264 视频 + AAC 音频（系统声音 & 麦克风）
- 暂停 / 恢复、动态码率、排除录制窗口自身
- 录制画质切换（标准 / 高 / 原始）

### 编辑

- 裁剪、圆角、旋转
- 标注工具——箭头、矩形、椭圆、文字、画笔、马赛克
- 撤销 / 重做
- 复制到剪贴板、保存为 PNG / JPEG / BMP

### AI 能力

| 功能               | 模型                  | 说明                     |
| ------------------ | --------------------- | ------------------------ |
| AI 擦除（Inpaint） | LaMa FP32             | 涂抹不需要的区域并自动修复 |
| 背景去除            | RMBG-1.4              | 一键去除图片背景          |
| 超分辨率 4×         | Real-ESRGAN x4plus    | 将图片放大 4 倍并增强细节  |

> 所有 AI 模型通过 ONNX Runtime + DirectML 在 GPU 上本地推理，无需联网。

---

## 系统要求

- **操作系统**: Windows 10 Build 19041 (Version 2004) 及以上
- **运行时**: .NET 10 Desktop Runtime
- **GPU**: 支持 DirectX 11 的显卡（AI 功能需要 DirectML 兼容 GPU）

## 构建

### 前置条件

1. **Visual Studio 2022** (v17.x)
   - 工作负载：**.NET 桌面开发** + **使用 C++ 的桌面开发**（v145 平台工具集）
2. **.NET 10 SDK**（`net10.0-windows`）
3. **x64** 平台

### 编译

```powershell
# 克隆仓库
git clone https://github.com/fallssyj/PixSnap.git
cd PixSnap

# 使用 MSBuild 编译
msbuild src/PixSnap/PixSnap.sln /p:Configuration=Debug /p:Platform=x64 /m
```

输出位于 `src/PixSnap/PixSnap/bin/x64/Debug/`。

### ONNX 模型

AI 功能需要以下 ONNX 模型文件放在对应位置（首次运行时提示下载）：

| 模型文件              | 用途       |
| --------------------- | ---------- |
| `rmbg-1.4.onnx`      | 背景去除   |
| `realesrgan-x4plus.onnx` | 超分辨率 |
| `lama_fp32.onnx`     | AI 擦除    |

---

## 项目架构

```
src/PixSnap/
├── NativeScreenCapturer/     # C++/CLI 原生组件
│   ├── ScreenCapturer.cpp    #   WGC 截图 / MF 录制 / WASAPI 音频
│   └── DirectXHelper.h       #   D3D11 / DXGI 辅助
├── PixSnap/                  # WPF 主程序
│   ├── Views/                #   窗口 & 用户界面
│   ├── ViewModels/           #   MVVM ViewModel
│   ├── Models/               #   数据模型
│   ├── Services/             #   业务逻辑 & AI 推理
│   ├── Controls/             #   自定义控件
│   ├── Converters/           #   值转换器
│   └── Styles/               #   主题样式 (Mica / 亮暗色)
└── PixSnap.sln
```

### 关键依赖

| 库 | 版本 | 用途 |
| --- | --- | --- |
| iNKORE.UI.WPF.Modern | 0.10.2.1 | Fluent 2 主题 / Mica 窗口 / 现代控件 |
| iNKORE.UI.WPF | 1.2.8 | iNKORE WPF 基础库 |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM 框架 |
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | AI 模型推理 |
| SkiaSharp | 3.119.2 | 图像处理 |
| Serilog | 4.3.1 | 结构化日志 |
| Hardcodet.NotifyIcon.Wpf | 2.0.1 | 系统托盘 |

### 原生依赖

Direct3D 11 · DXGI · DWM · Media Foundation (MFPlat / MFReadWrite / MF) · WASAPI · Windows.Graphics.Capture

---

## 许可证

Copyright © 2026 PixSnap Contributors
