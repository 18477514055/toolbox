using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Toolbox.Tools.ImageCrop;

/// <summary>
/// 图片裁剪 / 缩放的纯函数实现。
///
/// 刻意从窗口里抽出来：这样 `--selftest` 能直接断言"输出像素数真的等于输入像素数"，
/// 而不是只能靠人眼看。"编译通过 ≠ 功能验证过"（交接文档 §六·5）。
/// </summary>
internal static class ImageOps
{
    /// <summary>
    /// 按**像素**矩形裁剪。矩形会自动夹到图片范围内。
    /// 注意：这里一律用像素坐标，不用显示尺寸——96 DPI 和 144 DPI 下
    /// PixelWidth 与显示宽度不一样，按显示尺寸裁会差一截（交接文档 §五·工具2 风险）。
    /// </summary>
    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        var x = Math.Max(0, Math.Min(rect.X, source.PixelWidth - 1));
        var y = Math.Max(0, Math.Min(rect.Y, source.PixelHeight - 1));
        var w = Math.Max(1, Math.Min(rect.Width, source.PixelWidth - x));
        var h = Math.Max(1, Math.Min(rect.Height, source.PixelHeight - y));

        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// 改成指定的像素尺寸。
    ///
    /// 用 DrawingVisual + RenderTargetBitmap 而不是 TransformedBitmap，原因有两个：
    ///   1. 能精确控制输出像素数（验收标准要求"实际像素与输入一致，不是看起来差不多"）；
    ///   2. 能选插值方式——TransformedBitmap 做不到最近邻。
    /// </summary>
    public static BitmapSource Resize(BitmapSource source, int width, int height, bool highQuality)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "目标尺寸必须是正数。");
        }

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual,
            highQuality ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);

        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, width, height));
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>按长边限制生成缩略图（长边已经够小就原样返回）。</summary>
    public static BitmapSource Thumbnail(BitmapSource source, int maxSide = 320)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= maxSide)
        {
            return source;
        }

        var scale = (double)maxSide / longest;
        var w = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        var h = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
        return Resize(source, w, h, highQuality: true);
    }
}
