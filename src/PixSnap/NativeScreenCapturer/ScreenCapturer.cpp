#include "ScreenCapturer.h"

#pragma managed(push, off)

#include "DirectXHelper.h"

#include <d3d11.h>
#include <d3d11_4.h>
#include <dwmapi.h>
#include <dxgi1_2.h>
#include <objbase.h>
#include <roapi.h>
#include <windows.graphics.capture.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/Windows.Security.Authorization.AppCapabilityAccess.h>
#include <string>
#include <stdexcept>
#include <utility>
#include <vector>
#include <atomic>
#include <mutex>

// Media Foundation
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <Mferror.h>

// WASAPI
#include <mmdeviceapi.h>
#include <Audioclient.h>
#include <functiondiscoverykeys_devpkey.h>

#include <fstream>
#include <cstdio>
#include <csignal>
#include <thread>

namespace Wgc = winrt::Windows::Graphics::Capture;
namespace Wg = winrt::Windows::Graphics;
namespace Wd = winrt::Windows::Graphics::DirectX;
namespace Wd11 = winrt::Windows::Graphics::DirectX::Direct3D11;

// RtlGetVersion 不受 manifest 限制，始终返回真实 OS 版本
static bool IsOsBuildOrGreater(DWORD buildNumber)
{
    using RtlGetVersionFn = LONG(WINAPI*)(POSVERSIONINFOW);
    static const auto pRtlGetVersion = reinterpret_cast<RtlGetVersionFn>(
        GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"));
    if (!pRtlGetVersion) return false;

    OSVERSIONINFOW osvi{};
    osvi.dwOSVersionInfoSize = sizeof(osvi);
    pRtlGetVersion(&osvi);
    return osvi.dwBuildNumber >= buildNumber;
}

class ScreenCapturerImpl
{
public:
    winrt::com_ptr<ID3D11Device> Device;
    winrt::com_ptr<ID3D11DeviceContext> Context;
    Wd11::IDirect3DDevice WinRtDevice{ nullptr };
};

// ─── 录屏状态 ─────────────────────────────────────────────────────────────────

class ScreenRecorderState
{
public:
    ScreenCapturerImpl* impl{};

    // Media Foundation
    winrt::com_ptr<IMFSinkWriter> sinkWriter;
    DWORD videoStreamIndex{};

    // WGC 持续捕获
    Wgc::Direct3D11CaptureFramePool framePool{ nullptr };
    Wgc::GraphicsCaptureSession session{ nullptr };
    Wgc::Direct3D11CaptureFramePool::FrameArrived_revoker frameArrivedRevoker;

    // 状态
    std::atomic<bool> recording{ false };
    std::atomic<bool> paused{ false };
    LARGE_INTEGER startTime{};
    LARGE_INTEGER frequency{};
    LONGLONG totalPausedDuration{}; // 累计暂停时长 (100ns)
    LARGE_INTEGER pauseBeginTime{}; // 暂停开始时刻

    // 输出尺寸（偶数对齐后）
    int outputWidth{};
    int outputHeight{};

    // 区域裁剪参数（仅 region 录制需要）
    bool needsCrop{};
    int cropX{}, cropY{}, cropW{}, cropH{};
    int sourceW{}, sourceH{};

    std::mutex writerMutex;

    // 视频码率（由上层传入）
    UINT32 videoBitrate{ 8000000 };

    // 音频时间戳跟踪（用于 duration 计算）
    LONGLONG totalAudioSamplesWritten{};

    // 音频采集失败标志（用于通知上层）
    bool audioInitFailed{};

    // 复用 staging 纹理，避免每帧创建/销毁
    winrt::com_ptr<ID3D11Texture2D> stagingTexture;
    int stagingW{}, stagingH{};

    // 音频捕获
    bool enableMicrophone{};
    bool enableSystemAudio{};
    DWORD audioStreamIndex{};
    bool hasAudioStream{};

    // WASAPI 设备
    winrt::com_ptr<IAudioClient> loopbackClient;
    winrt::com_ptr<IAudioCaptureClient> loopbackCapture;
    HANDLE loopbackEvent{};
    WAVEFORMATEX* loopbackFormat{};

    winrt::com_ptr<IAudioClient> micClient;
    winrt::com_ptr<IAudioCaptureClient> micCapture;
    HANDLE micEvent{};
    WAVEFORMATEX* micFormat{};

    std::thread audioThread;
    std::atomic<bool> audioRunning{ false };

    // 音频混合参数（统一为 48kHz 16-bit stereo PCM 送 MF）
    static constexpr UINT32 kAudioSampleRate = 48000;
    static constexpr UINT32 kAudioChannels = 2;
    static constexpr UINT32 kAudioBitsPerSample = 16;

    void AudioCaptureLoop();

    void OnFrameArrived(
        Wgc::Direct3D11CaptureFramePool const& sender,
        winrt::Windows::Foundation::IInspectable const&);
};

struct CapturedFrameData
{
    int Width{};
    int Height{};
    std::vector<BYTE> Pixels;
};

struct RegionCaptureRequest
{
    int X{};
    int Y{};
    int Width{};
    int Height{};
};

struct TextureMapGuard
{
    ID3D11DeviceContext* Context{};
    ID3D11Texture2D* Texture{};

    ~TextureMapGuard()
    {
        if (Context != nullptr && Texture != nullptr)
        {
            Context->Unmap(Texture, 0);
        }
    }
};

namespace
{
    constexpr UINT kBytesPerPixel = 4;
    constexpr DWORD kFrameTimeoutMs = 2500;
    constexpr int DWMWA_CLOAKED = 14;

    // 崩溃诊断：将 terminate 原因写入日志文件
    void WriteCrashLog(const char* reason)
    {
        wchar_t modulePath[MAX_PATH]{};
        GetModuleFileNameW(nullptr, modulePath, MAX_PATH);
        std::wstring dir(modulePath);
        auto pos = dir.find_last_of(L'\\');
        if (pos != std::wstring::npos) dir.resize(pos);
        auto logPath = dir + L"\\logs\\native_crash.log";

        std::ofstream ofs(logPath, std::ios::app);
        if (ofs.is_open())
        {
            SYSTEMTIME st{};
            GetLocalTime(&st);
            char buf[64]{};
            sprintf_s(buf, "%04d-%02d-%02d %02d:%02d:%02d.%03d",
                st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
            ofs << buf << " [FATAL] " << reason << std::endl;
        }
    }

    void CustomTerminateHandler()
    {
        WriteCrashLog("std::terminate() called — possible uncaught exception in native thread");
        std::abort();
    }

    // 向量异常处理器：捕获 Access Violation 等 SEH 级崩溃
    static LONG WINAPI RecordingVEHandler(PEXCEPTION_POINTERS pExInfo)
    {
        if (!pExInfo || !pExInfo->ExceptionRecord) return EXCEPTION_CONTINUE_SEARCH;

        DWORD code = pExInfo->ExceptionRecord->ExceptionCode;

        // 只记录致命异常，忽略 C++ throw / CLR 等常规异常代码
        if (code == EXCEPTION_ACCESS_VIOLATION ||
            code == EXCEPTION_STACK_OVERFLOW ||
            code == EXCEPTION_INT_DIVIDE_BY_ZERO ||
            code == EXCEPTION_ILLEGAL_INSTRUCTION ||
            code == EXCEPTION_NONCONTINUABLE_EXCEPTION ||
            code == STATUS_HEAP_CORRUPTION ||
            code == 0xC0000374 /* heap corruption */ ||
            code == 0xC0000409 /* stack buffer overrun (/GS) */ ||
            code == 0xC0000602 /* unknown software exception */)
        {
            // 获取崩溃地址所在的模块名
            char moduleName[MAX_PATH]{};
            HMODULE hMod = nullptr;
            auto faultAddr = pExInfo->ExceptionRecord->ExceptionAddress;
            if (GetModuleHandleExA(
                    GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                    static_cast<LPCSTR>(faultAddr), &hMod))
            {
                GetModuleFileNameA(hMod, moduleName, MAX_PATH);
            }

            char buf[768]{};
            if (code == EXCEPTION_ACCESS_VIOLATION && pExInfo->ExceptionRecord->NumberParameters >= 2)
            {
                sprintf_s(buf, "VEH: AccessViolation %s address 0x%p at IP=%p module=%s",
                    pExInfo->ExceptionRecord->ExceptionInformation[0] == 0 ? "READ" : "WRITE",
                    (void*)pExInfo->ExceptionRecord->ExceptionInformation[1],
                    faultAddr, moduleName);
            }
            else
            {
                sprintf_s(buf, "VEH: ExceptionCode=0x%08lX Address=%p module=%s",
                    (unsigned long)code, faultAddr, moduleName);
            }

            WriteCrashLog(buf);
        }

        return EXCEPTION_CONTINUE_SEARCH;
    }

    static PVOID g_vehHandle = nullptr;

    struct MonitorEntry
    {
        HMONITOR Handle;
        RECT Bounds;
    };

    BOOL CALLBACK EnumMonitorCallback(HMONITOR monitor, HDC, LPRECT, LPARAM lParam)
    {
        auto monitors = reinterpret_cast<std::vector<MonitorEntry>*>(lParam);
        MONITORINFOEXW info{};
        info.cbSize = sizeof(info);

        if (GetMonitorInfoW(monitor, &info))
        {
            monitors->push_back(MonitorEntry{ monitor, info.rcMonitor });
        }

        return TRUE;
    }

    std::vector<MonitorEntry> EnumerateMonitors()
    {
        std::vector<MonitorEntry> monitors;
        EnumDisplayMonitors(nullptr, nullptr, &EnumMonitorCallback, reinterpret_cast<LPARAM>(&monitors));
        return monitors;
    }

    void EnsureWinRtInitialized()
    {
        static bool terminateHandlerSet = false;
        if (!terminateHandlerSet)
        {
            std::set_terminate(CustomTerminateHandler);
            terminateHandlerSet = true;
        }

        APTTYPE apartmentType = APTTYPE_CURRENT;
        APTTYPEQUALIFIER apartmentQualifier = APTTYPEQUALIFIER_NONE;
        const auto apartmentState = CoGetApartmentType(&apartmentType, &apartmentQualifier);

        RO_INIT_TYPE initType = RO_INIT_MULTITHREADED;
        if (SUCCEEDED(apartmentState))
        {
            if (apartmentType == APTTYPE_STA || apartmentType == APTTYPE_MAINSTA)
            {
                initType = RO_INIT_SINGLETHREADED;
            }
        }

        const auto initializeResult = RoInitialize(initType);
        if (FAILED(initializeResult)
            && initializeResult != S_FALSE
            && initializeResult != RPC_E_CHANGED_MODE)
        {
            winrt::check_hresult(initializeResult);
        }
    }

    void InitializeDevice(ScreenCapturerImpl* impl)
    {
        UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
#ifdef _DEBUG
        flags |= D3D11_CREATE_DEVICE_DEBUG;
#endif

        constexpr D3D_FEATURE_LEVEL levels[] =
        {
            D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL_10_1,
            D3D_FEATURE_LEVEL_10_0
        };

        D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
        winrt::check_hresult(D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            nullptr,
            flags,
            levels,
            ARRAYSIZE(levels),
            D3D11_SDK_VERSION,
            impl->Device.put(),
            &level,
            impl->Context.put()));

        impl->WinRtDevice = NativeScreenCapturer::Interop::CreateDirect3DDevice(impl->Device.get());
    }

    Wgc::GraphicsCaptureItem CreateItemForWindow(HWND hwnd)
    {
        auto interop = winrt::get_activation_factory<Wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        Wgc::GraphicsCaptureItem item{ nullptr };
        winrt::check_hresult(interop->CreateForWindow(hwnd, winrt::guid_of<Wgc::GraphicsCaptureItem>(), winrt::put_abi(item)));
        return item;
    }

    Wgc::GraphicsCaptureItem CreateItemForMonitor(HMONITOR monitor)
    {
        auto interop = winrt::get_activation_factory<Wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        Wgc::GraphicsCaptureItem item{ nullptr };
        winrt::check_hresult(interop->CreateForMonitor(monitor, winrt::guid_of<Wgc::GraphicsCaptureItem>(), winrt::put_abi(item)));
        return item;
    }

    CapturedFrameData CopyTextureToBuffer(ScreenCapturerImpl* impl, ID3D11Texture2D* texture, Wg::SizeInt32 contentSize)
    {
        D3D11_TEXTURE2D_DESC sourceDesc{};
        texture->GetDesc(&sourceDesc);

        D3D11_TEXTURE2D_DESC stagingDesc = sourceDesc;
        stagingDesc.BindFlags = 0;
        stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        stagingDesc.MiscFlags = 0;
        stagingDesc.Usage = D3D11_USAGE_STAGING;

        winrt::com_ptr<ID3D11Texture2D> staging;
        winrt::check_hresult(impl->Device->CreateTexture2D(&stagingDesc, nullptr, staging.put()));
        impl->Context->CopyResource(staging.get(), texture);

        D3D11_MAPPED_SUBRESOURCE mapped{};
        winrt::check_hresult(impl->Context->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped));

        TextureMapGuard mapGuard{ impl->Context.get(), staging.get() };

        CapturedFrameData frameData;
        frameData.Width = contentSize.Width;
        frameData.Height = contentSize.Height;

        const int stride = frameData.Width * static_cast<int>(kBytesPerPixel);
        frameData.Pixels.resize(static_cast<size_t>(stride) * static_cast<size_t>(frameData.Height));

        auto destination = frameData.Pixels.data();
        auto source = static_cast<BYTE*>(mapped.pData);

        for (int row = 0; row < frameData.Height; ++row)
        {
            memcpy(destination + (row * stride), source + (row * mapped.RowPitch), stride);
        }

        return frameData;
    }

    CapturedFrameData CaptureSingleFrameData(ScreenCapturerImpl* impl, Wgc::GraphicsCaptureItem const& item, bool includeBorder)
    {
        if (item == nullptr)
        {
            throw std::runtime_error("The capture item could not be created.");
        }

        auto size = item.Size();
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw std::runtime_error("The capture target has an invalid size.");
        }

        auto framePool = Wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(
            impl->WinRtDevice,
            Wd::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            1,
            size);

        auto session = framePool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled(false);
        if (IsOsBuildOrGreater(22000))
            session.IsBorderRequired(includeBorder);

        winrt::handle readyEvent(CreateEventW(nullptr, FALSE, FALSE, nullptr));
        Wgc::Direct3D11CaptureFrame frame{ nullptr };

        auto revoker = framePool.FrameArrived(winrt::auto_revoke, [&](auto const& sender, auto const&)
        {
            if (frame == nullptr)
            {
                frame = sender.TryGetNextFrame();
                SetEvent(readyEvent.get());
            }
        });

        session.StartCapture();

        const auto waitResult = WaitForSingleObject(readyEvent.get(), kFrameTimeoutMs);
        if (waitResult != WAIT_OBJECT_0 || frame == nullptr)
        {
            throw std::runtime_error("Timed out waiting for a captured frame.");
        }

        auto surface = frame.Surface();
        auto texture = NativeScreenCapturer::Interop::GetDXGIInterfaceFromObject<ID3D11Texture2D>(surface);
        return CopyTextureToBuffer(impl, texture.get(), frame.ContentSize());
    }

    CapturedFrameData CaptureWindowFrame(ScreenCapturerImpl* impl, HWND hwnd, bool includeBorder)
    {
        EnsureWinRtInitialized();
        auto item = CreateItemForWindow(hwnd);
        return CaptureSingleFrameData(impl, item, includeBorder);
    }

    CapturedFrameData CaptureMonitorFrame(ScreenCapturerImpl* impl, HMONITOR monitor)
    {
        EnsureWinRtInitialized();
        auto item = CreateItemForMonitor(monitor);
        return CaptureSingleFrameData(impl, item, false);
    }

    CapturedFrameData CaptureRegionFrame(ScreenCapturerImpl* impl, RegionCaptureRequest const& request)
    {
        if (request.Width <= 0 || request.Height <= 0)
        {
            throw std::runtime_error("The capture region must have a positive size.");
        }

        CapturedFrameData combined;
        combined.Width = request.Width;
        combined.Height = request.Height;
        combined.Pixels.resize(static_cast<size_t>(request.Width) * static_cast<size_t>(request.Height) * kBytesPerPixel, 0);

        RECT requestedRect
        {
            request.X,
            request.Y,
            request.X + request.Width,
            request.Y + request.Height
        };

        const auto monitors = EnumerateMonitors();
        bool copiedAnyPixels = false;
        const int combinedStride = combined.Width * static_cast<int>(kBytesPerPixel);

        for (auto const& monitor : monitors)
        {
            RECT intersection{};
            if (!IntersectRect(&intersection, &requestedRect, &monitor.Bounds))
            {
                continue;
            }

            auto monitorFrame = CaptureMonitorFrame(impl, monitor.Handle);
            const int monitorStride = monitorFrame.Width * static_cast<int>(kBytesPerPixel);

            const int sourceX = intersection.left - monitor.Bounds.left;
            const int sourceY = intersection.top - monitor.Bounds.top;
            const int destX = intersection.left - request.X;
            const int destY = intersection.top - request.Y;
            const int copyWidth = intersection.right - intersection.left;
            const int copyHeight = intersection.bottom - intersection.top;
            const int bytesPerRow = copyWidth * static_cast<int>(kBytesPerPixel);

            for (int row = 0; row < copyHeight; ++row)
            {
                auto source = monitorFrame.Pixels.data() + ((sourceY + row) * monitorStride) + (sourceX * static_cast<int>(kBytesPerPixel));
                auto destination = combined.Pixels.data() + ((destY + row) * combinedStride) + (destX * static_cast<int>(kBytesPerPixel));
                memcpy(destination, source, bytesPerRow);
            }

            copiedAnyPixels = true;
        }

        if (!copiedAnyPixels)
        {
            throw std::runtime_error("The requested region does not overlap any monitor.");
        }

        return combined;
    }

    // ─── 音频辅助函数 ─────────────────────────────────────────────────────────

    // 将 WASAPI 捕获的浮点/整型音频转换为 16-bit PCM 并重采样到目标格式
    // 简化实现：只处理 float32 和 int16 输入，单声道/立体声
    // 从源缓冲区读取一帧（float L/R），支持 float32 / int16 输入
    inline void ReadSourceFrame(const BYTE* src, UINT32 frameIdx, UINT32 srcChannels,
                                 bool isFloat, UINT32 srcBits, float& left, float& right)
    {
        if (isFloat && srcBits == 32)
        {
            auto samples = reinterpret_cast<const float*>(src + frameIdx * srcChannels * sizeof(float));
            left = samples[0];
            right = srcChannels >= 2 ? samples[1] : left;
        }
        else if (!isFloat && srcBits == 16)
        {
            auto samples = reinterpret_cast<const int16_t*>(src + frameIdx * srcChannels * sizeof(int16_t));
            left = samples[0] / 32768.0f;
            right = srcChannels >= 2 ? samples[1] / 32768.0f : left;
        }
        else
        {
            left = right = 0.0f;
        }
    }

    void ConvertAudioToInt16Stereo(
        const BYTE* src, UINT32 numFrames, const WAVEFORMATEX* srcFmt,
        std::vector<int16_t>& outBuf, UINT32 targetSampleRate, UINT32 targetChannels)
    {
        if (numFrames == 0 || src == nullptr) return;

        UINT32 srcChannels = srcFmt->nChannels;
        UINT32 srcBits = srcFmt->wBitsPerSample;
        UINT32 srcSampleRate = srcFmt->nSamplesPerSec;
        bool isFloat = false;

        if (srcFmt->wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
            isFloat = true;
        else if (srcFmt->wFormatTag == WAVE_FORMAT_EXTENSIBLE)
        {
            auto ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(srcFmt);
            if (ext->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                isFloat = true;
        }

        auto toI16 = [](float v) -> int16_t {
            v = (std::max)(-1.0f, (std::min)(1.0f, v));
            return static_cast<int16_t>(v * 32767.0f);
        };

        size_t prevSize = outBuf.size();

        if (srcSampleRate == targetSampleRate)
        {
            // 采样率匹配，直接转换格式
            outBuf.resize(prevSize + numFrames * targetChannels);
            for (UINT32 i = 0; i < numFrames; ++i)
            {
                float left, right;
                ReadSourceFrame(src, i, srcChannels, isFloat, srcBits, left, right);
                size_t baseIdx = prevSize + i * targetChannels;
                outBuf[baseIdx] = toI16(left);
                if (targetChannels >= 2) outBuf[baseIdx + 1] = toI16(right);
            }
        }
        else
        {
            // 采样率不匹配，线性插值重采样
            // 输出帧数 = 输入帧数 * (目标采样率 / 源采样率)
            UINT32 outFrames = static_cast<UINT32>(
                static_cast<uint64_t>(numFrames) * targetSampleRate / srcSampleRate);
            if (outFrames == 0) return;

            outBuf.resize(prevSize + outFrames * targetChannels);
            double ratio = static_cast<double>(srcSampleRate) / targetSampleRate;

            for (UINT32 o = 0; o < outFrames; ++o)
            {
                double srcPos = o * ratio;
                UINT32 idx0 = static_cast<UINT32>(srcPos);
                double frac = srcPos - idx0;
                UINT32 idx1 = (idx0 + 1 < numFrames) ? idx0 + 1 : idx0;

                float l0, r0, l1, r1;
                ReadSourceFrame(src, idx0, srcChannels, isFloat, srcBits, l0, r0);
                ReadSourceFrame(src, idx1, srcChannels, isFloat, srcBits, l1, r1);

                float left  = static_cast<float>(l0 + (l1 - l0) * frac);
                float right = static_cast<float>(r0 + (r1 - r0) * frac);

                size_t baseIdx = prevSize + o * targetChannels;
                outBuf[baseIdx] = toI16(left);
                if (targetChannels >= 2) outBuf[baseIdx + 1] = toI16(right);
            }
        }
    }

    // 初始化一个 WASAPI 音频捕获客户端
    HRESULT InitWasapiCapture(
        bool isLoopback,
        winrt::com_ptr<IAudioClient>& outClient,
        winrt::com_ptr<IAudioCaptureClient>& outCapture,
        HANDLE& outEvent,
        WAVEFORMATEX*& outFormat)
    {
        winrt::com_ptr<IMMDeviceEnumerator> enumerator;
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
            __uuidof(IMMDeviceEnumerator), enumerator.put_void());
        if (FAILED(hr)) return hr;

        winrt::com_ptr<IMMDevice> device;
        hr = enumerator->GetDefaultAudioEndpoint(
            isLoopback ? eRender : eCapture,
            isLoopback ? eConsole : eCommunications,
            device.put());
        if (FAILED(hr)) return hr;

        hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, outClient.put_void());
        if (FAILED(hr)) return hr;

        hr = outClient->GetMixFormat(&outFormat);
        if (FAILED(hr)) return hr;

        DWORD streamFlags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        if (isLoopback)
            streamFlags |= AUDCLNT_STREAMFLAGS_LOOPBACK;

        // 20ms buffer
        REFERENCE_TIME bufferDuration = 200000; // 20ms in 100ns units
        hr = outClient->Initialize(AUDCLNT_SHAREMODE_SHARED, streamFlags, bufferDuration, 0, outFormat, nullptr);
        if (FAILED(hr)) return hr;

        outEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!outEvent) return E_FAIL;

        hr = outClient->SetEventHandle(outEvent);
        if (FAILED(hr)) return hr;

        hr = outClient->GetService(__uuidof(IAudioCaptureClient), outCapture.put_void());
        if (FAILED(hr)) return hr;

        return S_OK;
    }

    // ─── 录屏辅助函数 ─────────────────────────────────────────────────────────

    int AlignToEven(int value)
    {
        return (value + 1) & ~1;
    }

    MonitorEntry FindBestMonitorForRegion(RECT const& region)
    {
        auto monitors = EnumerateMonitors();
        if (monitors.empty())
            throw std::runtime_error("No monitors found.");

        MonitorEntry best = monitors[0];
        LONG bestOverlap = 0;

        for (auto& m : monitors)
        {
            RECT intersect{};
            if (IntersectRect(&intersect, &region, &m.Bounds))
            {
                LONG overlap = (intersect.right - intersect.left) * (intersect.bottom - intersect.top);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = m;
                }
            }
        }
        return best;
    }

    HRESULT InitializeMFSinkWriter(
        ScreenRecorderState* state,
        const wchar_t* outputPath)
    {
        HRESULT hr = MFStartup(MF_VERSION);
        if (FAILED(hr)) return hr;

        winrt::com_ptr<IMFAttributes> attrs;
        hr = MFCreateAttributes(attrs.put(), 1);
        if (FAILED(hr)) return hr;

        attrs->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);

        hr = MFCreateSinkWriterFromURL(outputPath, nullptr, attrs.get(), state->sinkWriter.put());
        if (FAILED(hr)) return hr;

        // 输出类型: H.264
        winrt::com_ptr<IMFMediaType> outputType;
        MFCreateMediaType(outputType.put());
        outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
        outputType->SetUINT32(MF_MT_AVG_BITRATE, state->videoBitrate);
        outputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        MFSetAttributeSize(outputType.get(), MF_MT_FRAME_SIZE, state->outputWidth, state->outputHeight);
        MFSetAttributeRatio(outputType.get(), MF_MT_FRAME_RATE, 30, 1);
        MFSetAttributeRatio(outputType.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

        hr = state->sinkWriter->AddStream(outputType.get(), &state->videoStreamIndex);
        if (FAILED(hr)) return hr;

        // 输入类型: RGB32 (BGRA 内存布局，与 D3D11 B8G8R8A8_UNORM 纹理匹配)
        winrt::com_ptr<IMFMediaType> inputType;
        MFCreateMediaType(inputType.put());
        inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        inputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
        inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        MFSetAttributeSize(inputType.get(), MF_MT_FRAME_SIZE, state->outputWidth, state->outputHeight);
        MFSetAttributeRatio(inputType.get(), MF_MT_FRAME_RATE, 30, 1);
        MFSetAttributeRatio(inputType.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        inputType->SetUINT32(MF_MT_DEFAULT_STRIDE, state->outputWidth * kBytesPerPixel);
        inputType->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE);
        inputType->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
        inputType->SetUINT32(MF_MT_SAMPLE_SIZE, state->outputWidth * state->outputHeight * kBytesPerPixel);

        hr = state->sinkWriter->SetInputMediaType(state->videoStreamIndex, inputType.get(), nullptr);
        if (FAILED(hr)) return hr;

        // 音频流（AAC 输出，PCM 输入）
        if (state->enableMicrophone || state->enableSystemAudio)
        {
            winrt::com_ptr<IMFMediaType> audioOutputType;
            MFCreateMediaType(audioOutputType.put());
            audioOutputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            audioOutputType->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
            audioOutputType->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            audioOutputType->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, ScreenRecorderState::kAudioSampleRate);
            audioOutputType->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, ScreenRecorderState::kAudioChannels);
            audioOutputType->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 32000);

            hr = state->sinkWriter->AddStream(audioOutputType.get(), &state->audioStreamIndex);
            if (FAILED(hr)) return hr;

            winrt::com_ptr<IMFMediaType> audioInputType;
            MFCreateMediaType(audioInputType.put());
            audioInputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            audioInputType->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
            audioInputType->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, ScreenRecorderState::kAudioBitsPerSample);
            audioInputType->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, ScreenRecorderState::kAudioSampleRate);
            audioInputType->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, ScreenRecorderState::kAudioChannels);
            audioInputType->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, ScreenRecorderState::kAudioChannels * ScreenRecorderState::kAudioBitsPerSample / 8);
            audioInputType->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND,
                ScreenRecorderState::kAudioSampleRate * ScreenRecorderState::kAudioChannels * ScreenRecorderState::kAudioBitsPerSample / 8);

            hr = state->sinkWriter->SetInputMediaType(state->audioStreamIndex, audioInputType.get(), nullptr);
            if (FAILED(hr)) return hr;

            state->hasAudioStream = true;
        }

        hr = state->sinkWriter->BeginWriting();
        return hr;
    }

    void WriteFrameToSinkWriter(
        ScreenRecorderState* state,
        const BYTE* pixels,
        int srcWidth,
        int srcHeight,
        int srcStride,
        LONGLONG timestamp)
    {
        int outW = state->outputWidth;
        int outH = state->outputHeight;
        DWORD outStride = outW * kBytesPerPixel;
        DWORD bufferSize = outStride * outH;

        winrt::com_ptr<IMFMediaBuffer> buffer;
        if (FAILED(MFCreateMemoryBuffer(bufferSize, buffer.put()))) return;

        BYTE* dest = nullptr;
        if (FAILED(buffer->Lock(&dest, nullptr, nullptr))) return;

        if (state->needsCrop)
        {
            // 裁剪：从源帧中提取 ROI
            int sx = state->cropX;
            int sy = state->cropY;
            int cw = state->cropW;
            int ch = state->cropH;

            // 清空目标缓冲区（处理边缘和对齐填充）
            memset(dest, 0, bufferSize);

            int copyW = (std::min)(cw, outW);
            int copyH = (std::min)(ch, outH);
            int copyBytes = copyW * static_cast<int>(kBytesPerPixel);

            for (int row = 0; row < copyH; ++row)
            {
                int srcRow = sy + row;
                if (srcRow < 0 || srcRow >= srcHeight) continue;
                const BYTE* srcLine = pixels + srcRow * srcStride + sx * kBytesPerPixel;
                BYTE* dstLine = dest + row * outStride;
                memcpy(dstLine, srcLine, copyBytes);
            }
        }
        else
        {
            // 直接拷贝（处理对齐填充）
            int copyW = (std::min)(srcWidth, outW);
            int copyH = (std::min)(srcHeight, outH);
            int copyBytes = copyW * static_cast<int>(kBytesPerPixel);

            if (outW > srcWidth || outH > srcHeight)
                memset(dest, 0, bufferSize);

            for (int row = 0; row < copyH; ++row)
            {
                memcpy(dest + row * outStride, pixels + row * srcStride, copyBytes);
            }
        }

        buffer->Unlock();
        buffer->SetCurrentLength(bufferSize);

        winrt::com_ptr<IMFSample> sample;
        if (FAILED(MFCreateSample(sample.put()))) return;

        sample->AddBuffer(buffer.get());
        sample->SetSampleTime(timestamp);
        sample->SetSampleDuration(333333LL); // ~30fps = 10000000/30

        state->sinkWriter->WriteSample(state->videoStreamIndex, sample.get());
    }

    void StartRecordingWithItem(
        ScreenRecorderState* state,
        ScreenCapturerImpl* impl,
        Wgc::GraphicsCaptureItem const& item,
        const wchar_t* outputPath)
    {
        if (state->recording.load())
            throw std::runtime_error("Already recording.");

        EnsureWinRtInitialized();

        // 启用 D3D11 多线程保护（录屏帧回调在后台线程运行）
        ID3D11Multithread* mt = nullptr;
        if (SUCCEEDED(impl->Device->QueryInterface(__uuidof(ID3D11Multithread), reinterpret_cast<void**>(&mt))) && mt)
        {
            mt->SetMultithreadProtected(TRUE);
            mt->Release();
        }

        state->impl = impl;

        auto size = item.Size();
        if (size.Width <= 0 || size.Height <= 0)
            throw std::runtime_error("The capture target has an invalid size.");

        if (!state->needsCrop)
        {
            state->outputWidth = AlignToEven(size.Width);
            state->outputHeight = AlignToEven(size.Height);
            state->sourceW = size.Width;
            state->sourceH = size.Height;
        }

        // 初始化 MF Sink Writer
        auto hr = InitializeMFSinkWriter(state, outputPath);
        if (FAILED(hr))
            throw std::runtime_error("Failed to initialize Media Foundation sink writer.");

        // 安装向量异常处理器以捕获硬件级崩溃
        if (!g_vehHandle)
            g_vehHandle = AddVectoredExceptionHandler(1, RecordingVEHandler);

        try
        {
            QueryPerformanceFrequency(&state->frequency);
            QueryPerformanceCounter(&state->startTime);

            // 创建 FreeThreaded 帧池
            state->framePool = Wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(
                impl->WinRtDevice,
                Wd::DirectXPixelFormat::B8G8R8A8UIntNormalized,
                2,
                size);

            state->session = state->framePool.CreateCaptureSession(item);
            state->session.IsCursorCaptureEnabled(true);
            if (IsOsBuildOrGreater(22000))
                state->session.IsBorderRequired(false);

            state->frameArrivedRevoker = state->framePool.FrameArrived(
                winrt::auto_revoke,
                [state](auto const& sender, auto const& args)
                {
                    try
                    {
                        state->OnFrameArrived(sender, args);
                    }
                    catch (...)
                    {
                        state->recording.store(false);
                    }
                });

            state->recording.store(true);
            state->session.StartCapture();

            // 启动音频捕获线程
            if (state->enableMicrophone || state->enableSystemAudio)
            {
                bool audioOk = false;
                bool loopbackFailed = false;
                bool micFailed = false;

                if (state->enableSystemAudio)
                {
                    auto hr2 = InitWasapiCapture(true,
                        state->loopbackClient, state->loopbackCapture,
                        state->loopbackEvent, state->loopbackFormat);
                    if (SUCCEEDED(hr2))
                    {
                        state->loopbackClient->Start();
                        audioOk = true;
                    }
                    else
                    {
                        loopbackFailed = true;
                    }
                }

                if (state->enableMicrophone)
                {
                    auto hr2 = InitWasapiCapture(false,
                        state->micClient, state->micCapture,
                        state->micEvent, state->micFormat);
                    if (SUCCEEDED(hr2))
                    {
                        state->micClient->Start();
                        audioOk = true;
                    }
                    else
                    {
                        micFailed = true;
                    }
                }

                // 记录音频初始化失败状态
                if (loopbackFailed || micFailed)
                    state->audioInitFailed = true;

                if (audioOk)
                {
                    state->audioRunning.store(true);
                    state->audioThread = std::thread([state]() { state->AudioCaptureLoop(); });
                }
            }
        }
        catch (winrt::hresult_error const& e)
        {
            throw std::runtime_error(winrt::to_string(e.message()));
        }
    }

    void PauseRecordingState(ScreenRecorderState* state)
    {
        if (!state->recording.load() || state->paused.load()) return;
        QueryPerformanceCounter(&state->pauseBeginTime);
        state->paused.store(true);
    }

    void ResumeRecordingState(ScreenRecorderState* state)
    {
        if (!state->recording.load() || !state->paused.load()) return;
        LARGE_INTEGER now{};
        QueryPerformanceCounter(&now);
        state->totalPausedDuration += (now.QuadPart - state->pauseBeginTime.QuadPart) * 10000000LL / state->frequency.QuadPart;
        state->paused.store(false);
    }

    void StopRecordingState(ScreenRecorderState* state)
    {
        if (!state->recording.exchange(false))
            return;

        state->paused.store(false);
        state->totalPausedDuration = 0;

        // 停止音频捕获线程
        state->audioRunning.store(false);
        if (state->audioThread.joinable())
            state->audioThread.join();

        // 清理 WASAPI 资源
        if (state->loopbackClient) { try { state->loopbackClient->Stop(); } catch (...) {} }
        if (state->micClient) { try { state->micClient->Stop(); } catch (...) {} }
        state->loopbackCapture = nullptr;
        state->loopbackClient = nullptr;
        state->micCapture = nullptr;
        state->micClient = nullptr;
        if (state->loopbackEvent) { CloseHandle(state->loopbackEvent); state->loopbackEvent = nullptr; }
        if (state->micEvent) { CloseHandle(state->micEvent); state->micEvent = nullptr; }
        if (state->loopbackFormat) { CoTaskMemFree(state->loopbackFormat); state->loopbackFormat = nullptr; }
        if (state->micFormat) { CoTaskMemFree(state->micFormat); state->micFormat = nullptr; }
        state->hasAudioStream = false;

        // 断开帧回调
        state->frameArrivedRevoker.revoke();

        try
        {
            if (state->session != nullptr)
            {
                state->session.Close();
                state->session = nullptr;
            }
        }
        catch (...) {}

        try
        {
            if (state->framePool != nullptr)
            {
                state->framePool.Close();
                state->framePool = nullptr;
            }
        }
        catch (...) {}

        {
            std::lock_guard lock(state->writerMutex);
            if (state->sinkWriter)
            {
                try { state->sinkWriter->Finalize(); } catch (...) {}
                state->sinkWriter = nullptr;
            }
        }

        MFShutdown();

        // 重置裁剪状态
        state->needsCrop = false;
        state->cropX = state->cropY = state->cropW = state->cropH = 0;
        state->sourceW = state->sourceH = 0;
        state->impl = nullptr;

        // 释放复用的 staging 纹理
        state->stagingTexture = nullptr;
        state->stagingW = state->stagingH = 0;

        // 重置音频标志
        state->enableMicrophone = false;
        state->enableSystemAudio = false;
        state->totalAudioSamplesWritten = 0;
        state->audioInitFailed = false;
    }

    // ─── 纯 unmanaged 录屏入口（所有 WinRT 异常在此捕获转换） ────────────

    void DoStartRecordingMonitor(ScreenRecorderState* state, ScreenCapturerImpl* impl,
                                  HMONITOR monitor, const wchar_t* outputPath,
                                  bool enableMic, bool enableSys, UINT32 videoBitrate)
    {
        try
        {
            state->enableMicrophone = enableMic;
            state->enableSystemAudio = enableSys;
            state->videoBitrate = videoBitrate;
            auto item = CreateItemForMonitor(monitor);
            StartRecordingWithItem(state, impl, item, outputPath);
        }
        catch (winrt::hresult_error const& e)
        {
            throw std::runtime_error(winrt::to_string(e.message()));
        }
    }

    void DoStartRecordingWindow(ScreenRecorderState* state, ScreenCapturerImpl* impl,
                                 HWND hwnd, const wchar_t* outputPath,
                                 bool enableMic, bool enableSys, UINT32 videoBitrate)
    {
        try
        {
            state->enableMicrophone = enableMic;
            state->enableSystemAudio = enableSys;
            state->videoBitrate = videoBitrate;
            auto item = CreateItemForWindow(hwnd);
            StartRecordingWithItem(state, impl, item, outputPath);
        }
        catch (winrt::hresult_error const& e)
        {
            throw std::runtime_error(winrt::to_string(e.message()));
        }
    }

    void DoStartRecordingRegion(ScreenRecorderState* state, ScreenCapturerImpl* impl,
                                 HMONITOR monitor, const wchar_t* outputPath,
                                 bool enableMic, bool enableSys, UINT32 videoBitrate)
    {
        try
        {
            state->enableMicrophone = enableMic;
            state->enableSystemAudio = enableSys;
            state->videoBitrate = videoBitrate;
            auto item = CreateItemForMonitor(monitor);
            StartRecordingWithItem(state, impl, item, outputPath);
        }
        catch (winrt::hresult_error const& e)
        {
            throw std::runtime_error(winrt::to_string(e.message()));
        }
    }

    void DoStopRecording(ScreenRecorderState* state)
    {
        try
        {
            StopRecordingState(state);
        }
        catch (winrt::hresult_error const& e)
        {
            throw std::runtime_error(winrt::to_string(e.message()));
        }
    }

} // end anonymous namespace

// ─── 音频捕获线程 ─────────────────────────────────────────────────────────────

void ScreenRecorderState::AudioCaptureLoop()
{
    // 等待事件数组
    HANDLE events[2]{};
    int eventCount = 0;
    if (loopbackEvent) events[eventCount++] = loopbackEvent;
    if (micEvent) events[eventCount++] = micEvent;
    if (eventCount == 0) return;

    while (audioRunning.load() && recording.load())
    {
        WaitForMultipleObjects(eventCount, events, FALSE, 20);

        // 暂停时仍需读取 WASAPI 缓冲区以避免溢出，但跳过写入
        if (paused.load())
        {
            // 排空 loopback 缓冲区
            if (loopbackCapture)
            {
                BYTE* data = nullptr; UINT32 frames = 0; DWORD flags = 0;
                while (SUCCEEDED(loopbackCapture->GetBuffer(&data, &frames, &flags, nullptr, nullptr)) && frames > 0)
                    loopbackCapture->ReleaseBuffer(frames);
            }
            // 排空 mic 缓冲区
            if (micCapture)
            {
                BYTE* data = nullptr; UINT32 frames = 0; DWORD flags = 0;
                while (SUCCEEDED(micCapture->GetBuffer(&data, &frames, &flags, nullptr, nullptr)) && frames > 0)
                    micCapture->ReleaseBuffer(frames);
            }
            continue;
        }

        std::vector<int16_t> mixedPcm;

        // 读取系统声音（loopback）
        if (loopbackCapture)
        {
            BYTE* data = nullptr;
            UINT32 frames = 0;
            DWORD flags = 0;
            while (SUCCEEDED(loopbackCapture->GetBuffer(&data, &frames, &flags, nullptr, nullptr)) && frames > 0)
            {
                if (flags & AUDCLNT_BUFFERFLAGS_SILENT)
                {
                    // 静音帧也需要按重采样后的帧数填充，保持与非静音帧一致
                    UINT32 outFrames = (loopbackFormat->nSamplesPerSec != kAudioSampleRate)
                        ? static_cast<UINT32>(static_cast<uint64_t>(frames) * kAudioSampleRate / loopbackFormat->nSamplesPerSec)
                        : frames;
                    if (outFrames == 0) outFrames = 1;
                    size_t prevSize = mixedPcm.size();
                    mixedPcm.resize(prevSize + outFrames * kAudioChannels, 0);
                }
                else
                {
                    ConvertAudioToInt16Stereo(data, frames, loopbackFormat, mixedPcm, kAudioSampleRate, kAudioChannels);
                }
                loopbackCapture->ReleaseBuffer(frames);
            }
        }

        // 读取麦克风
        if (micCapture)
        {
            std::vector<int16_t> micPcm;
            BYTE* data = nullptr;
            UINT32 frames = 0;
            DWORD flags = 0;
            while (SUCCEEDED(micCapture->GetBuffer(&data, &frames, &flags, nullptr, nullptr)) && frames > 0)
            {
                if (flags & AUDCLNT_BUFFERFLAGS_SILENT)
                {
                    UINT32 outFrames = (micFormat->nSamplesPerSec != kAudioSampleRate)
                        ? static_cast<UINT32>(static_cast<uint64_t>(frames) * kAudioSampleRate / micFormat->nSamplesPerSec)
                        : frames;
                    if (outFrames == 0) outFrames = 1;
                    size_t prevSize = micPcm.size();
                    micPcm.resize(prevSize + outFrames * kAudioChannels, 0);
                }
                else
                {
                    ConvertAudioToInt16Stereo(data, frames, micFormat, micPcm, kAudioSampleRate, kAudioChannels);
                }
                micCapture->ReleaseBuffer(frames);
            }

            // 混合：将麦克风叠加到系统声音上
            if (!micPcm.empty())
            {
                if (mixedPcm.empty())
                {
                    mixedPcm = std::move(micPcm);
                }
                else
                {
                    size_t minLen = (std::min)(mixedPcm.size(), micPcm.size());
                    for (size_t i = 0; i < minLen; ++i)
                    {
                        int32_t mixed = static_cast<int32_t>(mixedPcm[i]) + static_cast<int32_t>(micPcm[i]);
                        mixedPcm[i] = static_cast<int16_t>((std::max)(-32768, (std::min)(32767, mixed)));
                    }
                    // 如果麦克风比系统声音长，追加剩余部分
                    if (micPcm.size() > mixedPcm.size())
                    {
                        mixedPcm.insert(mixedPcm.end(), micPcm.begin() + minLen, micPcm.end());
                    }
                }
            }
        }

        // 写入 MF SinkWriter（使用 QPC 墙钟时间戳，与视频流保持同一时钟基准）
        if (!mixedPcm.empty() && hasAudioStream)
        {
            DWORD dataBytes = static_cast<DWORD>(mixedPcm.size() * sizeof(int16_t));
            winrt::com_ptr<IMFMediaBuffer> mfBuf;
            if (SUCCEEDED(MFCreateMemoryBuffer(dataBytes, mfBuf.put())))
            {
                BYTE* dest = nullptr;
                if (SUCCEEDED(mfBuf->Lock(&dest, nullptr, nullptr)))
                {
                    memcpy(dest, mixedPcm.data(), dataBytes);
                    mfBuf->Unlock();
                    mfBuf->SetCurrentLength(dataBytes);

                    winrt::com_ptr<IMFSample> sample;
                    if (SUCCEEDED(MFCreateSample(sample.put())))
                    {
                        UINT32 totalFrames = static_cast<UINT32>(mixedPcm.size()) / kAudioChannels;
                        LONGLONG duration = static_cast<LONGLONG>(totalFrames) * 10000000LL / kAudioSampleRate;

                        // 使用 QPC 墙钟（减去暂停时长），与视频帧时间戳同源
                        LARGE_INTEGER now{};
                        QueryPerformanceCounter(&now);
                        LONGLONG sampleTime = (now.QuadPart - startTime.QuadPart) * 10000000LL / frequency.QuadPart - totalPausedDuration;
                        // 确保音频时间戳不回退
                        if (sampleTime < 0) sampleTime = 0;

                        sample->AddBuffer(mfBuf.get());
                        sample->SetSampleTime(sampleTime);
                        sample->SetSampleDuration(duration);

                        std::lock_guard lock(writerMutex);
                        if (sinkWriter && recording.load())
                        {
                            sinkWriter->WriteSample(audioStreamIndex, sample.get());
                            totalAudioSamplesWritten += totalFrames;
                        }
                    }
                }
            }
        }
    }
}

void ScreenRecorderState::OnFrameArrived(
    Wgc::Direct3D11CaptureFramePool const& sender,
    winrt::Windows::Foundation::IInspectable const&)
{
    if (!recording.load()) return;

    Wgc::Direct3D11CaptureFrame frame{ nullptr };
    try { frame = sender.TryGetNextFrame(); }
    catch (...) { return; }
    if (frame == nullptr) return;

    // 暂停时仍需取出帧以释放帧池资源，但跳过写入
    if (paused.load()) { frame.Close(); return; }

    // 获取 D3D11 纹理（使用 try_as 避免异常）
    winrt::com_ptr<ID3D11Texture2D> texture;
    {
        auto surface = frame.Surface();
        auto access = surface.try_as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        if (!access) { frame.Close(); return; }
        HRESULT hr = access->GetInterface(__uuidof(ID3D11Texture2D), texture.put_void());
        if (FAILED(hr)) { frame.Close(); return; }
    }

    Wg::SizeInt32 contentSize{};
    try { contentSize = frame.ContentSize(); }
    catch (...) { frame.Close(); return; }
    if (contentSize.Width <= 0 || contentSize.Height <= 0) { frame.Close(); return; }

    D3D11_TEXTURE2D_DESC sourceDesc{};
    texture->GetDesc(&sourceDesc);
    int texW = static_cast<int>(sourceDesc.Width);
    int texH = static_cast<int>(sourceDesc.Height);

    // 复用或重新创建 staging 纹理
    if (!stagingTexture || stagingW != texW || stagingH != texH)
    {
        stagingTexture = nullptr;

        D3D11_TEXTURE2D_DESC stagingDesc = sourceDesc;
        stagingDesc.BindFlags = 0;
        stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        stagingDesc.MiscFlags = 0;
        stagingDesc.Usage = D3D11_USAGE_STAGING;

        HRESULT hr = impl->Device->CreateTexture2D(&stagingDesc, nullptr, stagingTexture.put());
        if (FAILED(hr)) { frame.Close(); return; }
        stagingW = texW;
        stagingH = texH;
    }

    // GPU 拷贝 + 刷新确保完成
    impl->Context->CopyResource(stagingTexture.get(), texture.get());
    impl->Context->Flush();

    // 释放对 WGC 帧纹理的引用 — 越早释放，WGC 帧池回收越安全
    texture = nullptr;
    frame.Close();
    frame = nullptr;

    D3D11_MAPPED_SUBRESOURCE mapped{};
    HRESULT hr = impl->Context->Map(stagingTexture.get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr)) return;

    int frameW = contentSize.Width;
    int frameH = contentSize.Height;

    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    LONGLONG elapsed100ns = (now.QuadPart - startTime.QuadPart) * 10000000LL / frequency.QuadPart - totalPausedDuration;

    // 写入 MF SinkWriter
    {
        std::lock_guard lock(writerMutex);
        if (sinkWriter && recording.load())
        {
            int outW = outputWidth;
            int outH = outputHeight;
            DWORD outStrideMF = outW * kBytesPerPixel;
            DWORD bufferSize = outStrideMF * outH;

            winrt::com_ptr<IMFMediaBuffer> mfBuffer;
            if (SUCCEEDED(MFCreateMemoryBuffer(bufferSize, mfBuffer.put())))
            {
                BYTE* dest = nullptr;
                if (SUCCEEDED(mfBuffer->Lock(&dest, nullptr, nullptr)))
                {
                    auto srcBase = static_cast<const BYTE*>(mapped.pData);

                    if (needsCrop)
                    {
                        memset(dest, 0, bufferSize);
                        int copyW = (std::min)(cropW, outW);
                        int copyH = (std::min)(cropH, outH);
                        int copyBytes = copyW * static_cast<int>(kBytesPerPixel);

                        for (int row = 0; row < copyH; ++row)
                        {
                            int srcRow = cropY + row;
                            if (srcRow < 0 || srcRow >= frameH) continue;
                            const BYTE* srcLine = srcBase + srcRow * mapped.RowPitch + cropX * kBytesPerPixel;
                            BYTE* dstLine = dest + row * outStrideMF;
                            memcpy(dstLine, srcLine, copyBytes);
                        }
                    }
                    else
                    {
                        int copyW = (std::min)(frameW, outW);
                        int copyH = (std::min)(frameH, outH);
                        int copyBytes = copyW * static_cast<int>(kBytesPerPixel);

                        if (outW > frameW || outH > frameH)
                            memset(dest, 0, bufferSize);

                        for (int row = 0; row < copyH; ++row)
                        {
                            memcpy(dest + row * outStrideMF,
                                   srcBase + row * mapped.RowPitch,
                                   copyBytes);
                        }
                    }

                    mfBuffer->Unlock();
                    mfBuffer->SetCurrentLength(bufferSize);

                    winrt::com_ptr<IMFSample> sample;
                    if (SUCCEEDED(MFCreateSample(sample.put())))
                    {
                        sample->AddBuffer(mfBuffer.get());
                        sample->SetSampleTime(elapsed100ns);
                        sample->SetSampleDuration(333333LL); // ~30fps
                        sinkWriter->WriteSample(videoStreamIndex, sample.get());
                    }
                }
            }
        }
    }

    // 解除映射（stagingTexture 保留复用）
    impl->Context->Unmap(stagingTexture.get(), 0);
}

#pragma managed(pop)

namespace Sys = System;
namespace Collections = System::Collections::Generic;
namespace InteropServices = System::Runtime::InteropServices;
namespace Wpf = System::Windows;
namespace WpfInterop = System::Windows::Interop;
namespace WpfMedia = System::Windows::Media;
namespace WpfImaging = System::Windows::Media::Imaging;

namespace
{
    HMONITOR GetMonitorHandleForScreen(int screenIndex)
    {
        const auto monitors = EnumerateMonitors();
        if (screenIndex < 0 || screenIndex >= static_cast<int>(monitors.size()))
        {
            throw gcnew Sys::ArgumentOutOfRangeException("screenIndex");
        }

        return monitors[screenIndex].Handle;
    }

    WpfImaging::BitmapSource^ CreateBitmapFromFrameData(CapturedFrameData const& frameData)
    {
        const int stride = frameData.Width * static_cast<int>(kBytesPerPixel);
        auto pixels = gcnew array<Sys::Byte>(stride * frameData.Height);

        if (!frameData.Pixels.empty())
        {
            InteropServices::Marshal::Copy(
                Sys::IntPtr(const_cast<BYTE*>(frameData.Pixels.data())),
                pixels,
                0,
                pixels->Length);
        }

        auto bitmap = WpfImaging::BitmapSource::Create(
            frameData.Width,
            frameData.Height,
            96.0,
            96.0,
            WpfMedia::PixelFormats::Bgra32,
            nullptr,
            pixels,
            stride);

        bitmap->Freeze();
        return bitmap;
    }

    bool IsWindowCloaked(HWND hwnd)
    {
        DWORD cloaked = 0;
        return SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked != 0;
    }

    HICON GetWindowIcon(HWND hwnd)
    {
        auto icon = reinterpret_cast<HICON>(SendMessageW(hwnd, WM_GETICON, ICON_BIG, 0));
        if (icon == nullptr)
        {
            icon = reinterpret_cast<HICON>(SendMessageW(hwnd, WM_GETICON, ICON_SMALL2, 0));
        }
        if (icon == nullptr)
        {
            icon = reinterpret_cast<HICON>(GetClassLongPtrW(hwnd, GCLP_HICON));
        }
        if (icon == nullptr)
        {
            icon = reinterpret_cast<HICON>(GetClassLongPtrW(hwnd, GCLP_HICONSM));
        }

        return icon;
    }

    WpfImaging::BitmapSource^ CreateIconBitmap(HICON icon)
    {
        if (icon == nullptr)
        {
            return nullptr;
        }

        auto source = WpfInterop::Imaging::CreateBitmapSourceFromHIcon(
            Sys::IntPtr(icon),
            Wpf::Int32Rect::Empty,
            WpfImaging::BitmapSizeOptions::FromWidthAndHeight(16, 16));
        source->Freeze();
        return source;
    }

    BOOL CALLBACK EnumWindowsCallback(HWND hwnd, LPARAM lParam)
    {
        auto handle = InteropServices::GCHandle::FromIntPtr(Sys::IntPtr(lParam));
        auto results = safe_cast<Collections::List<NativeScreenCapturer::WindowInfo^>^>(handle.Target);
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsWindowCloaked(hwnd))
        {
            return TRUE;
        }

        const auto titleLength = GetWindowTextLengthW(hwnd);
        if (titleLength <= 0)
        {
            return TRUE;
        }

        std::wstring title(static_cast<size_t>(titleLength) + 1, L'\0');
        GetWindowTextW(hwnd, title.data(), static_cast<int>(title.size()));
        title.resize(wcslen(title.c_str()));
        if (title.empty())
        {
            return TRUE;
        }

        wchar_t className[256]{};
        GetClassNameW(hwnd, className, ARRAYSIZE(className));

        auto info = gcnew NativeScreenCapturer::WindowInfo();
        info->Title = gcnew Sys::String(title.c_str());
        info->Hwnd = Sys::IntPtr(hwnd);
        info->ClassName = gcnew Sys::String(className);
        info->Icon = CreateIconBitmap(GetWindowIcon(hwnd));

        results->Add(info);
        return TRUE;
    }
}

namespace NativeScreenCapturer
{
    ScreenCapturer::ScreenCapturer()
        : m_impl(new ScreenCapturerImpl())
        , m_recorder(new ScreenRecorderState())
    {
        EnsureWinRtInitialized();
        InitializeDevice(m_impl);
    }

    ScreenCapturer::~ScreenCapturer()
    {
        this->!ScreenCapturer();
    }

    ScreenCapturer::!ScreenCapturer()
    {
        if (m_recorder != nullptr)
        {
            StopRecordingState(m_recorder);
            delete m_recorder;
            m_recorder = nullptr;
        }
        if (m_impl != nullptr)
        {
            delete m_impl;
            m_impl = nullptr;
        }
    }

    WpfImaging::BitmapSource^ ScreenCapturer::CaptureFullScreen(int screenIndex)
    {
        auto monitor = GetMonitorHandleForScreen(screenIndex);

        try
        {
            return CreateBitmapFromFrameData(CaptureMonitorFrame(m_impl, monitor));
        }
        catch (std::exception const& exception)
        {
            throw gcnew Sys::InvalidOperationException(gcnew Sys::String(exception.what()));
        }
    }

    WpfImaging::BitmapSource^ ScreenCapturer::CaptureWindow(Sys::IntPtr hwnd, bool includeBorder)
    {
        if (hwnd == Sys::IntPtr::Zero)
        {
            throw gcnew Sys::ArgumentException("A valid window handle is required.", "hwnd");
        }

        auto target = static_cast<HWND>(hwnd.ToPointer());
        if (!IsWindow(target))
        {
            throw gcnew Sys::InvalidOperationException("The specified window no longer exists.");
        }

        try
        {
            return CreateBitmapFromFrameData(CaptureWindowFrame(m_impl, target, includeBorder));
        }
        catch (std::exception const& exception)
        {
            throw gcnew Sys::InvalidOperationException(gcnew Sys::String(exception.what()));
        }
    }

    WpfImaging::BitmapSource^ ScreenCapturer::CaptureRegion(int x, int y, int width, int height)
    {
        try
        {
            return CreateBitmapFromFrameData(CaptureRegionFrame(m_impl, RegionCaptureRequest{ x, y, width, height }));
        }
        catch (std::exception const& exception)
        {
            throw gcnew Sys::InvalidOperationException(gcnew Sys::String(exception.what()));
        }
    }

    array<WindowInfo^>^ ScreenCapturer::GetOpenWindows()
    {
        auto windows = gcnew Collections::List<WindowInfo^>();
        auto handle = InteropServices::GCHandle::Alloc(windows);

        try
        {
            const auto callbackState = InteropServices::GCHandle::ToIntPtr(handle).ToInt64();
            EnumWindows(&EnumWindowsCallback, static_cast<LPARAM>(callbackState));
            return windows->ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    int ScreenCapturer::GetScreenCount()
    {
        return static_cast<int>(EnumerateMonitors().size());
    }

    Sys::Drawing::Rectangle ScreenCapturer::GetScreenBounds(int screenIndex)
    {
        const auto monitors = EnumerateMonitors();
        if (screenIndex < 0 || screenIndex >= static_cast<int>(monitors.size()))
        {
            throw gcnew Sys::ArgumentOutOfRangeException("screenIndex");
        }

        const auto& bounds = monitors[screenIndex].Bounds;
        return Sys::Drawing::Rectangle(bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top);
    }

    Sys::Threading::Tasks::Task<WpfImaging::BitmapSource^>^ ScreenCapturer::CaptureFullScreenAsync(int screenIndex)
    {
        return Sys::Threading::Tasks::Task<WpfImaging::BitmapSource^>::Factory->StartNew(
            gcnew Sys::Func<Sys::Object^, WpfImaging::BitmapSource^>(this, &ScreenCapturer::CaptureFullScreenCore),
            screenIndex);
    }

    WpfImaging::BitmapSource^ ScreenCapturer::CaptureFullScreenCore(Sys::Object^ screenIndex)
    {
        return CaptureFullScreen(safe_cast<int>(screenIndex));
    }

    // ── 录屏 API ─────────────────────────────────────────────────────────────────────

    bool ScreenCapturer::IsRecording::get()
    {
        return m_recorder != nullptr && m_recorder->recording.load();
    }

    bool ScreenCapturer::AudioInitFailed::get()
    {
        return m_recorder != nullptr && m_recorder->audioInitFailed;
    }

    void ScreenCapturer::StartRecordingMonitor(int screenIndex, Sys::String^ outputPath, bool enableMicrophone, bool enableSystemAudio, int videoBitrate)
    {
        try
        {
            auto monitor = GetMonitorHandleForScreen(screenIndex);

            auto pathPin = InteropServices::Marshal::StringToHGlobalUni(outputPath);
            auto pathW = static_cast<const wchar_t*>(pathPin.ToPointer());

            try
            {
                DoStartRecordingMonitor(m_recorder, m_impl, monitor, pathW, enableMicrophone, enableSystemAudio, static_cast<UINT32>(videoBitrate));
            }
            finally
            {
                InteropServices::Marshal::FreeHGlobal(pathPin);
            }
        }
        catch (std::exception const& ex)
        {
            throw gcnew Sys::InvalidOperationException(gcnew Sys::String(ex.what()));
        }
        catch (...)
        {
            throw gcnew Sys::InvalidOperationException("Unknown native exception while starting recording.");
        }
    }

    void ScreenCapturer::StartRecordingWindow(Sys::IntPtr hwnd, Sys::String^ outputPath, bool enableMicrophone, bool enableSystemAudio, int videoBitrate)
    {
        if (hwnd == Sys::IntPtr::Zero)
            throw gcnew Sys::ArgumentException("A valid window handle is required.", "hwnd");

        auto target = static_cast<HWND>(hwnd.ToPointer());
        if (!IsWindow(target))
            throw gcnew Sys::InvalidOperationException("The specified window no longer exists.");

        try
        {
            auto pathPin = InteropServices::Marshal::StringToHGlobalUni(outputPath);
            auto pathW = static_cast<const wchar_t*>(pathPin.ToPointer());

            try
            {
                DoStartRecordingWindow(m_recorder, m_impl, target, pathW, enableMicrophone, enableSystemAudio, static_cast<UINT32>(videoBitrate));
            }
            finally
            {
                InteropServices::Marshal::FreeHGlobal(pathPin);
            }
        }
        catch (std::exception const& ex)
        {
            throw gcnew Sys::InvalidOperationException(gcnew Sys::String(ex.what()));
        }
        catch (...)
        {
            throw gcnew Sys::InvalidOperationException("Unknown native exception while starting recording.");
        }
    }

    void ScreenCapturer::StartRecordingRegion(int x, int y, int width, int height, Sys::String^ outputPath, bool enableMicrophone, bool enableSystemAudio, int videoBitrate)
    {
        if (width <= 0 || height <= 0)
            throw gcnew Sys::ArgumentException("The capture region must have a positive size.");

        try
        {
            RECT requestedRect{ x, y, x + width, y + height };
            auto bestMonitor = FindBestMonitorForRegion(requestedRect);

            // 设置裁剪参数：将屏幕坐标转为相对于监视器的偏移
            m_recorder->needsCrop = true;
            m_recorder->cropX = x - bestMonitor.Bounds.left;
            m_recorder->cropY = y - bestMonitor.Bounds.top;
            m_recorder->cropW = width;
            m_recorder->cropH = height;
            m_recorder->sourceW = bestMonitor.Bounds.right - bestMonitor.Bounds.left;
            m_recorder->sourceH = bestMonitor.Bounds.bottom - bestMonitor.Bounds.top;
            m_recorder->outputWidth = AlignToEven(width);
            m_recorder->outputHeight = AlignToEven(height);

            auto pathPin = InteropServices::Marshal::StringToHGlobalUni(outputPath);
            auto pathW = static_cast<const wchar_t*>(pathPin.ToPointer());

            try
            {
                DoStartRecordingRegion(m_recorder, m_impl, bestMonitor.Handle, pathW, enableMicrophone, enableSystemAudio, static_cast<UINT32>(videoBitrate));
            }
            finally
            {
                InteropServices::Marshal::FreeHGlobal(pathPin);
            }
        }
        catch (std::exception const& ex)
        {
            throw gcnew Sys::InvalidOperationException(gcnew Sys::String(ex.what()));
        }
        catch (...)
        {
            throw gcnew Sys::InvalidOperationException("Unknown native exception while starting recording.");
        }
    }

    void ScreenCapturer::PauseRecording()
    {
        PauseRecordingState(m_recorder);
    }

    void ScreenCapturer::ResumeRecording()
    {
        ResumeRecordingState(m_recorder);
    }

    void ScreenCapturer::StopRecording()
    {
        try
        {
            DoStopRecording(m_recorder);
        }
        catch (std::exception const& ex)
        {
            throw gcnew Sys::InvalidOperationException(gcnew Sys::String(ex.what()));
        }
        catch (...)
        {
            throw gcnew Sys::InvalidOperationException("Unknown native exception while stopping recording.");
        }
    }
}