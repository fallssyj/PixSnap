using Serilog;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

internal static class ClipboardHelper
{
    public static bool TrySetImage(BitmapSource image)
    {
        try
        {
            Clipboard.SetImage(image);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "剪贴板写入图片失败");
            return false;
        }
    }

    public static bool TrySetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "剪贴板写入文本失败");
            return false;
        }
    }

    public static BitmapSource? TryGetImage()
    {
        try
        {
            if (!Clipboard.ContainsImage())
                return null;
            return Clipboard.GetImage();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "剪贴板读取图片失败");
            return null;
        }
    }
}
