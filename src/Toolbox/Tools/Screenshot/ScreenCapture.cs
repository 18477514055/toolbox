using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core;

namespace Toolbox.Tools.Screenshot;

/// <summary>
/// 全屏截图：用 GDI 的 <c>BitBlt</c> 把整块虚拟屏幕（含多显示器）拷下来，转成 WPF 的 BitmapSource。
///
/// 为什么不用 <c>Graphics.CopyFromScreen</c>：它内部也是 BitBlt，但要引入 System.Drawing，
/// 而本项目刻意把 System.Drawing 的隐式 using 摘掉了（见 Toolbox.csproj）。用 GDI P/Invoke
/// 只多几个签名，换来零额外依赖。
///
/// 为什么用「先冻结整屏、再让用户框选」而不是「实时截选区」：
///   冻结后用户看到的是一张静态图，框选时不会闪、不会漏掉正在动画的内容；
///   这也是 Win+Shift+S / 微信截图 这些主流工具的做法。代价只是少截到「正在播放的视频帧」，
///   对截图工具完全可接受。
/// </summary>
internal static class ScreenCapture
{
    /// <summary>捕获整块虚拟屏幕。失败（拿不到 DC / BitBlt 失败）返回 null。</summary>
    public static BitmapSource? CaptureFullScreen()
    {
        try
        {
            var vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            var vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            var vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

            if (vw <= 0 || vh <= 0)
            {
                return null;
            }

            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var memDc = NativeMethods.CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, vw, vh);
                    if (hBitmap == IntPtr.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        var prev = NativeMethods.SelectObject(memDc, hBitmap);
                        // 虚拟屏幕原点可能是负数（副屏在左/上），BitBlt 的源坐标支持负偏移
                        var ok = NativeMethods.BitBlt(memDc, 0, 0, vw, vh, screenDc, vx, vy, NativeMethods.SRCCOPY);
                        NativeMethods.SelectObject(memDc, prev);

                        if (!ok)
                        {
                            return null;
                        }

                        // CreateBitmapSourceFromHBitmap 会立刻把位图数据拷进托管内存，
                        // 所以下面 DeleteObject 是安全的。dpi 显式给 96，避免 DPI 缩放把像素数带偏。
                        var source = Imaging.CreateBitmapSourceFromHBitmap(
                            hBitmap,
                            IntPtr.Zero,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromWidthAndHeight(vw, vh));

                        if (source.CanFreeze)
                        {
                            source.Freeze();
                        }

                        return source;
                    }
                    finally
                    {
                        NativeMethods.DeleteObject(hBitmap);
                    }
                }
                finally
                {
                    NativeMethods.DeleteDC(memDc);
                }
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("全屏截图失败", ex);
            return null;
        }
    }
}
