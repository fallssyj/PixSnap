# 第三方许可声明（Third-Party Notices）

PixSnap 本体以 [GPLv3](LICENSE) 发布。下列组件与模型由第三方提供，适用各自许可；**不**因包含于本软件而改变其原有条款。

---

## AI 模型（按需下载）

| 模型 | 用途 | 许可 | 说明 |
| --- | --- | --- | --- |
| PP-OCRv5 Mobile / Server | OCR 检测与识别 | Apache-2.0 | [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) |
| OCR 方向分类 CLS | 可选 | 源于 Paddle 生态 | [Kreuzberg/paddleocr-onnx-models](https://huggingface.co/Kreuzberg/paddleocr-onnx-models) |
| BiRefNet FP16 | 背景去除 | MIT | [ZhengPeng7/BiRefNet](https://github.com/ZhengPeng7/BiRefNet) |
| Real-ESRGAN x4plus | 超分辨率 | BSD-3-Clause | [xinntao/Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN) |
| LaMa FP32 | AI 擦除 | Apache-2.0 | [advimman/lama](https://github.com/advimman/lama) / [Carve/LaMa-ONNX](https://huggingface.co/Carve/LaMa-ONNX) |
| **RMBG-1.4** | 背景去除（默认） | **BRIA 自定义（非商用）** | [briaai/RMBG-1.4](https://huggingface.co/briaai/RMBG-1.4) — **商用须取得 BRIA 授权** |

下载或使用任一模型前，请阅读其 Hugging Face / GitHub 页面上的最新许可文本。

---

## 主要开源库（随程序分发）

| 组件 | 许可 | 说明 |
| --- | --- | --- |
| RapidOCRLib | Apache-2.0 | [RapidAI/RapidOCRCSharp](https://github.com/RapidAI/RapidOCRCSharp) |
| Emgu.CV | **GPL-3.0**（开源版）/ 商业许可 | [Emgu CV Licensing](https://www.emgu.com/wiki/index.php/Licensing:) — 闭源分发需评估合规 |
| Microsoft.ML.OnnxRuntime | MIT | ONNX Runtime |
| SkiaSharp | MIT | 图像处理 |
| CommunityToolkit.Mvvm | MIT | MVVM |
| Serilog | Apache-2.0 | 日志 |
| iNKORE.UI.WPF / Modern | MIT | UI 框架 |
| Hardcodet.NotifyIcon.Wpf | CPL-1.0 | 系统托盘 |
| Clipper2 | BSL-1.1 | RapidOCR 依赖 |

---

## 原生与系统组件

- **NativeScreenCapturer**：本项目 C++/CLI 组件，随 PixSnap 以 GPLv3 发布
- **Windows API**：Windows Graphics Capture、Media Foundation、DirectML 等由操作系统 / 运行时提供