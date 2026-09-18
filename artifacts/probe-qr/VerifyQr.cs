using System;
using System.IO;
using System.Windows.Media.Imaging;
using Toolbox.Tools.QrCode;
using ZXing;
using ZXing.Common;

// ============================================================================
// 用 **ZXing 当独立裁判**，验证自实现的 QR 编码器是不是真的能被扫出来。
//
// 为什么必须这样验：
//   自己写的编码器 + 自己写的扫描器 = 可能两边一起错还"自洽"。
//   必须拿一个**独立实现**来解码，才知道生成的图到底符合不符合标准。
//
// 注意：ZXing 只在**这个验证工具**里用，**不进产品** ——
//   产品保持零第三方依赖（见 DECISIONS）。
// ============================================================================

Console.WriteLine("==================================================");
Console.WriteLine("QR 编码器验证（裁判：ZXing.Net，独立实现）");
Console.WriteLine("==================================================");
Console.WriteLine();

var cases = new (string Label, string Text)[]
{
    ("纯英文短", "hello"),
    ("纯中文", "你好世界"),
    ("中文长句", "桌面工具箱 · 二维码功能测试"),
    ("URL", "https://example.com/path?query=1&x=2"),
    ("中英混排+符号", "订单号 A12345，金额 99.80 元"),
    ("较长中文", "这是一段比较长的中文内容，用来测试高版本二维码的编码是否正确无误。"),
};

var eccLevels = new[] { QrEcc.L, QrEcc.M, QrEcc.Q, QrEcc.H };

var zxReader = new BarcodeReaderGeneric
{
    Options = new DecodingOptions
    {
        PossibleFormats = new[] { BarcodeFormat.QR_CODE },
        TryHarder = true,
    },
};

var pass = 0;
var total = 0;
var failures = new System.Collections.Generic.List<string>();

foreach (var (label, text) in cases)
{
    foreach (var ecc in eccLevels)
    {
        total++;

        var matrix = QrEncoder.Encode(text, ecc);
        if (matrix is null)
        {
            // 内容超出该等级容量：这是预期行为，不算失败
            Console.WriteLine($"  [跳过] {label,-16} {ecc} —— 内容超出该等级容量（预期行为）");
            total--;
            continue;
        }

        var bmp = QrRenderer.Render(matrix, 400);

        // 转成 ZXing 能吃的像素数组
        var w = bmp.PixelWidth;
        var h = bmp.PixelHeight;
        var stride = w * 4;
        var pixels = new byte[stride * h];
        bmp.CopyPixels(pixels, stride, 0);

        // BGRA → 亮度
        var gray = new byte[w * h];
        for (var i = 0; i < w * h; i++)
        {
            var b = pixels[i * 4];
            var g = pixels[i * 4 + 1];
            var r = pixels[i * 4 + 2];
            gray[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
        }

        var source = new RGBLuminanceSource(gray, w, h, RGBLuminanceSource.BitmapFormat.Gray8);
        var result = zxReader.Decode(source);

        var ok = result is not null && result.Text == text;
        if (ok)
        {
            pass++;
            Console.WriteLine($"  [通过] {label,-16} {ecc}  版本{matrix.Version} 掩码{matrix.Mask}");
        }
        else
        {
            var got = result?.Text ?? "(ZXing 解不出来)";
            failures.Add($"{label}/{ecc}: 期望「{text}」实得「{got}」");
            Console.WriteLine($"  [失败] {label,-16} {ecc}  期望「{text}」");
            Console.WriteLine($"         实得「{got}」");
        }
    }
}

Console.WriteLine();
Console.WriteLine("--------------------------------------------------");
Console.WriteLine($"ZXing 独立验证：{pass}/{total} 通过");
if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("失败明细：");
    foreach (var f in failures) { Console.WriteLine("  · " + f); }
}
Console.WriteLine("--------------------------------------------------");

return failures.Count == 0 ? 0 : 1;
