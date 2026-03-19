#pragma once

#include <d3d11.h>
#include <dxgi1_2.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/base.h>

namespace NativeScreenCapturer::Interop
{
    inline winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice CreateDirect3DDevice(ID3D11Device* device)
    {
        winrt::com_ptr<IDXGIDevice> dxgiDevice;
        winrt::check_hresult(device->QueryInterface(__uuidof(IDXGIDevice), dxgiDevice.put_void()));

        winrt::com_ptr<::IInspectable> inspectable;
        winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.get(), inspectable.put()));

        return inspectable.as<winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>();
    }

    template <typename T>
    inline winrt::com_ptr<T> GetDXGIInterfaceFromObject(winrt::Windows::Foundation::IInspectable const& object)
    {
        auto access = object.as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        winrt::com_ptr<T> result;
        winrt::check_hresult(access->GetInterface(__uuidof(T), result.put_void()));
        return result;
    }
}