using System.Runtime.InteropServices;
using Toolbox.Core;

namespace Toolbox.Shell;

/// <summary>
/// 把「工作区百分比」翻译成屏幕上的实际位置，以及反向翻译。
///
/// 为什么不用绝对像素（交接文档明确要求）：
/// 用户把悬浮窗放在副屏右侧，拔掉副屏后，绝对坐标会落在不存在的区域里，
/// 窗口就"消失"了，而且用户根本不知道为什么。按百分比存就永远落在可见区域内。
/// </summary>
internal static class ScreenPlacement
{
    /// <summary>拿到鼠标所在显示器的工作区（物理像素，已排除任务栏）。</summary>
    public static (double Left, double Top, double Width, double Height) GetWorkAreaOfCursor()
    {
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (NativeMethods.GetCursorPos(out var pt))
        {
            var monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(monitor, ref info))
            {
                var w = info.rcWork.Right - info.rcWork.Left;
                var h = info.rcWork.Bottom - info.rcWork.Top;
                if (w > 0 && h > 0)
                {
                    return (info.rcWork.Left, info.rcWork.Top, w, h);
                }
            }
        }

        // 兜底：主屏
        return (0, 0,
            System.Windows.SystemParameters.PrimaryScreenWidth,
            System.Windows.SystemParameters.PrimaryScreenHeight);
    }

    /// <summary>
    /// 按百分比算出窗口应该出现在哪个物理像素位置。
    /// 百分比会被夹到 [0,1]，保证窗口始终落在工作区内。
    /// </summary>
    public static (double X, double Y) Resolve(double pctX, double pctY, double windowWidthDip, double windowHeightDip, double dpiScaleX, double dpiScaleY)
    {
        var (left, top, width, height) = GetWorkAreaOfCursor();

        pctX = Math.Clamp(pctX, 0, 1);
        pctY = Math.Clamp(pctY, 0, 1);

        var winW = windowWidthDip * dpiScaleX;
        var winH = windowHeightDip * dpiScaleY;

        // 百分比指的是窗口**左上角**能落在的范围（预留窗口自身的宽高）
        var maxX = Math.Max(0, width - winW);
        var maxY = Math.Max(0, height - winH);

        return (left + maxX * pctX, top + maxY * pctY);
    }

    /// <summary>
    /// 把窗口摆到「鼠标所在屏幕的右下角附近」。
    /// 交接文档明确选了这个位置而不是居中——居中更容易挡住用户正在看的东西。
    /// </summary>
    public static void MoveWindowNearBottomRight(IntPtr hwnd, int marginX = 28, int marginY = 28)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;

        var (left, top, width, height) = GetWorkAreaOfCursor();

        var x = left + width - w - marginX;
        var y = top + height - h - marginY;

        // 窗口比工作区还大时，至少别跑到左上角外面去
        x = Math.Max(left, x);
        y = Math.Max(top, y);

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, (int)x, (int)y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>反向：窗口现在的物理位置 → 工作区百分比。</summary>
    public static (double PctX, double PctY) ToPercent(double physicalX, double physicalY, double windowWidthDip, double windowHeightDip, double dpiScaleX, double dpiScaleY)
    {
        var (left, top, width, height) = GetWorkAreaOfCursor();

        var winW = windowWidthDip * dpiScaleX;
        var winH = windowHeightDip * dpiScaleY;

        var maxX = Math.Max(1, width - winW);
        var maxY = Math.Max(1, height - winH);

        var pctX = Math.Clamp((physicalX - left) / maxX, 0, 1);
        var pctY = Math.Clamp((physicalY - top) / maxY, 0, 1);
        return (pctX, pctY);
    }
}
