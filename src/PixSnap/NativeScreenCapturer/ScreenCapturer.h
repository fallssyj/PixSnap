#pragma once

#include <windows.h>

namespace System
{
    namespace Drawing
    {
        value class Rectangle;
    }

    namespace Windows::Media::Imaging
    {
        ref class BitmapSource;
    }
}

class ScreenCapturerImpl;
class ScreenRecorderState;

namespace NativeScreenCapturer
{
    public ref class WindowInfo
    {
    public:
        property System::String^ Title;
        property System::IntPtr Hwnd;
        property System::String^ ClassName;
        property System::Windows::Media::Imaging::BitmapSource^ Icon;
    };

    public ref class ScreenCapturer
    {
    public:
        ScreenCapturer();
        ~ScreenCapturer();
        !ScreenCapturer();

        System::Windows::Media::Imaging::BitmapSource^ CaptureFullScreen(int screenIndex);
        System::Windows::Media::Imaging::BitmapSource^ CaptureWindow(System::IntPtr hwnd, bool includeBorder);
        System::Windows::Media::Imaging::BitmapSource^ CaptureRegion(int x, int y, int width, int height);
        array<WindowInfo^>^ GetOpenWindows();
        int GetScreenCount();
        System::Drawing::Rectangle GetScreenBounds(int screenIndex);
        System::Threading::Tasks::Task<System::Windows::Media::Imaging::BitmapSource^>^ CaptureFullScreenAsync(int screenIndex);

        // 录屏 API
        void StartRecordingMonitor(int screenIndex, System::String^ outputPath, bool enableMicrophone, bool enableSystemAudio, int videoBitrate);
        void StartRecordingWindow(System::IntPtr hwnd, System::String^ outputPath, bool enableMicrophone, bool enableSystemAudio, int videoBitrate);
        void StartRecordingRegion(int x, int y, int width, int height, System::String^ outputPath, bool enableMicrophone, bool enableSystemAudio, int videoBitrate);
        void PauseRecording();
        void ResumeRecording();
        void StopRecording();
        property bool IsRecording { bool get(); }
        property bool AudioInitFailed { bool get(); }

    private:
        System::Windows::Media::Imaging::BitmapSource^ CaptureFullScreenCore(System::Object^ screenIndex);

    private:
        ScreenCapturerImpl* m_impl;
        ScreenRecorderState* m_recorder;
    };
}