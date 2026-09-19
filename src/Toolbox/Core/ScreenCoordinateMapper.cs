using System.Windows;

namespace Toolbox.Core;

/// <summary>
/// 「框选用的逻辑坐标」↔「位图像素坐标」的换算。
///
/// 单独抽成一个**纯函数**，是为了能被自检直接断言 —— 这块逻辑靠手点验不全
/// （要真有 125%/150% 的显示器、还要多屏混合 DPI），只能靠算术断言钉住。
///
/// ⚠️ 这里修掉的是一个真实缺陷（原实现见 git 历史里的 ScreenshotWindow.Finish）：
///   原来是 <c>scaleX = 位图像素宽 ÷ 背景图实际显示宽</c> **一个全局比值**。
///   单显示器下它成立；**多显示器且各屏 DPI 不同**时必然算错，因为一个比值
///   表达不了两个不同的比例。
///
///   实测反例（副屏在主屏右侧；主屏 150%、副屏 100%）：
///     物理虚拟屏幕宽 = 1920 + 1920 = 3840
///     WPF 逻辑虚拟屏幕宽 = 1280 + 1920 = 3200   （每块屏各自按自己的 DPI 换算）
///     全局系数 = 3840 / 3200 = 1.2
///     用户在主屏上框选逻辑 x=100 —— 真实物理位置是 100 × 1.5 = 150，
///     而全局系数给出 100 × 1.2 = 120 ⇒ **偏 30 px**，截出来的图整体错位。
///
/// 现在的做法：系数来自**目标显示器自己的 DPI**（dpi ÷ 96），
/// 与 app.manifest 的 PerMonitorV2 保持一致。单屏下结果与原来完全相同（回归安全）。
/// </summary>
public static class ScreenCoordinateMapper
{
    /// <summary>
    /// 把逻辑矩形换算成像素矩形（相对整张全屏位图的左上角）。
    /// </summary>
    /// <param name="logical">框选的逻辑矩形（相对截图窗口左上角）。</param>
    /// <param name="scaleX">逻辑 → 物理的横向系数（= dpi / 96）。</param>
    /// <param name="scaleY">逻辑 → 物理的纵向系数（= dpi / 96）。</param>
    /// <param name="bitmapWidth">全屏位图的像素宽（用于夹取越界）。</param>
    /// <param name="bitmapHeight">全屏位图的像素高。</param>
    public static Int32Rect ToPixelRect(
        double logicalX, double logicalY, double logicalW, double logicalH,
        double scaleX, double scaleY,
        int bitmapWidth, int bitmapHeight)
    {
        // 系数非法（0 / NaN / 负数）时退回 1.0：宁可原样输出，也不要算出全 0 的空图
        if (!IsUsable(scaleX)) { scaleX = 1.0; }
        if (!IsUsable(scaleY)) { scaleY = 1.0; }

        var x = (int)Math.Round(logicalX * scaleX);
        var y = (int)Math.Round(logicalY * scaleY);
        var w = (int)Math.Round(logicalW * scaleX);
        var h = (int)Math.Round(logicalH * scaleY);

        // 起点夹进图内：否则 CroppedBitmap 会直接抛 ArgumentException
        if (x < 0) { x = 0; }
        if (y < 0) { y = 0; }
        if (x > bitmapWidth - 1) { x = Math.Max(0, bitmapWidth - 1); }
        if (y > bitmapHeight - 1) { y = Math.Max(0, bitmapHeight - 1); }

        // 宽高至少 1，且不得越出右/下边界（裁掉越界的尾巴）
        w = Math.Max(1, Math.Min(w, bitmapWidth - x));
        h = Math.Max(1, Math.Min(h, bitmapHeight - y));

        return new Int32Rect(x, y, w, h);
    }

    private static bool IsUsable(double scale)
        => !double.IsNaN(scale) && !double.IsInfinity(scale) && scale > 0;

    /// <summary>
    /// 取窗口所在显示器的 DPI 缩放系数（96 dpi ⇒ 1.0）。
    ///
    /// 三级兜底，因为这几个 API 的可用性随系统版本不同：
    ///   ① <c>GetDpiForWindow</c>（Win10 1607+，最准，且是 PerMonitorV2 的正解）；
    ///   ② <c>GetDpiForMonitor(MDT_EFFECTIVE_DPI)</c>（Win8.1+，shcore.dll）；
    ///   ③ 失败就返回 1.0（等价于旧行为，绝不比原来更差）。
    /// </summary>
    public static double GetScaleForWindow(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                var dpi = NativeMethods.GetDpiForWindow(hwnd);
                if (dpi > 0)
                {
                    return dpi / 96.0;
                }
            }
            catch
            {
                // 老系统没有这个导出，走下一级
            }

            try
            {
                var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero
                    && NativeMethods.GetDpiForMonitor(
                        monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
                    && dpiX > 0)
                {
                    return dpiX / 96.0;
                }
            }
            catch
            {
                // shcore.dll 不可用
            }
        }

        return 1.0;
    }
}
