#include "ScreenCapturer.h"

#pragma managed(push, off)

#include "DirectXHelper.h"

#include <d3d11.h>
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
#include <string>
#include <stdexcept>
#include <utility>
#include <vector>

namespace Wgc = winrt::Windows::Graphics::Capture;
namespace Wg = winrt::Windows::Graphics;
namespace Wd = winrt::Windows::Graphics::DirectX;
namespace Wd11 = winrt::Windows::Graphics::DirectX::Direct3D11;

class ScreenCapturerImpl
{
public:
    winrt::com_ptr<ID3D11Device> Device;
    winrt::com_ptr<ID3D11DeviceContext> Context;
    Wd11::IDirect3DDevice WinRtDevice{ nullptr };
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
}