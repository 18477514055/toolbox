using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core;

namespace Toolbox.Tools.QrCode;

/// <summary>
/// 把 <see cref="QrMatrix"/> 画成位图。
/// </summary>
internal static class QrRenderer
{
    /// <summary>静区宽度（模块数）。标准要求 4 个模块，少了扫码器会定位困难。</summary>
    private const int QuietZone = 4;

    /// <summary>
    /// 渲染成位图。
    /// </summary>
    /// <param name="matrix">矩阵。</param>
    /// <param name="pixelSize">目标边长（像素）。实际会取整到模块的整数倍，避免模块大小不一。</param>
    public static BitmapSource Render(QrMatrix matrix, int pixelSize)
    {
        var modules = matrix.Size + QuietZone * 2;

        // 每个模块占多少像素：**向下取整**，保证所有模块严格等宽等高
        var scale = Math.Max(1, pixelSize / modules);
        var actual = modules * scale;

        var bmp = new WriteableBitmap(actual, actual, 96, 96, PixelFormats.Bgra32, null);

        // 白底
        var white = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        var black = new byte[] { 0x00, 0x00, 0x00, 0xFF };

        var pixels = new byte[actual * actual * 4];

        // 先全部铺白
        for (var i = 0; i < pixels.Length; i += 4)
        {
            white.CopyTo(pixels, i);
        }

        // 画深色模块
        for (var my = 0; my < matrix.Size; my++)
        {
            for (var mx = 0; mx < matrix.Size; mx++)
            {
                if (!matrix[mx, my])
                {
                    continue;
                }

                var px0 = (mx + QuietZone) * scale;
                var py0 = (my + QuietZone) * scale;

                for (var y = 0; y < scale; y++)
                {
                    var rowStart = ((py0 + y) * actual + px0) * 4;
                    for (var x = 0; x < scale; x++)
                    {
                        black.CopyTo(pixels, rowStart + x * 4);
                    }
                }
            }
        }

        bmp.WritePixels(new Int32Rect(0, 0, actual, actual), pixels, actual * 4, 0);
        bmp.Freeze();

        return bmp;
    }

    /// <summary>
    /// 生成内容的简短描述，用于文件名与状态显示。
    /// </summary>
    public static string DescribeContent(string text)
    {
        var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();

        // 去掉不能做文件名的字符
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
        {
            oneLine = oneLine.Replace(c, '_');
        }

        if (oneLine.Length > 40)
        {
            oneLine = oneLine[..40] + "…";
        }

        return oneLine.Length == 0 ? "qrcode" : oneLine;
    }
}
