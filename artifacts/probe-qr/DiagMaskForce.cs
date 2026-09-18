using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using Toolbox.Tools.QrCode;
using ZXing;
using ZXing.Common;

// ============================================================================
// 关键实验：对同一个内容，**强制使用每一种掩码**各生成一张图，逐张送 ZXing 解码。
//
// 三种结果对应三种结论：
//   A. 部分掩码可解、部分不可解 ⇒ 数据编码正确，问题在**掩码选择**
//   B. 全都不可解            ⇒ 数据编码 / 矩阵布局有错
//   C. 全都能解              ⇒ 失败来自渲染尺寸等外部因素
// ============================================================================

Console.WriteLine("=== 强制掩码逐张验证 ===");
Console.WriteLine();

var zx = new BarcodeReaderGeneric
{
    Options = new DecodingOptions
    {
        PossibleFormats = new[] { BarcodeFormat.QR_CODE },
        TryHarder = true,
    },
};

static byte[] ToGray(BitmapSource bmp)
{
    var w = bmp.PixelWidth;
    var h = bmp.PixelHeight;
    var stride = w * 4;
    var px = new byte[stride * h];
    bmp.CopyPixels(px, stride, 0);

    var gray = new byte[w * h];
    for (var i = 0; i < w * h; i++)
    {
        var b = px[i * 4]; var g = px[i * 4 + 1]; var r = px[i * 4 + 2];
        gray[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
    }

    return gray;
}

var cases = new (string Text, QrEcc Ecc)[]
{
    ("桌面工具箱 · 二维码功能测试", QrEcc.M),
    ("这是一段比较长的中文内容，用来测试高版本二维码的编码是否正确无误。", QrEcc.Q),
    ("这是一段比较长的中文内容，用来测试高版本二维码的编码是否正确无误。", QrEcc.H),
};

foreach (var (text, ecc) in cases)
{
    var auto = QrEncoder.Encode(text, ecc);
    if (auto is null) { Console.WriteLine($"(编码失败) {ecc}"); continue; }

    Console.WriteLine($"内容「{(text.Length > 12 ? text[..12] + "…" : text)}」 {ecc}  版本{auto.Version}  自动选掩码={auto.Mask}");

    var decodable = new List<int>();

    for (var mask = 0; mask < 8; mask++)
    {
        var forced = QrEncoder.EncodeWithMask(text, ecc, mask);
        var bmp = QrRenderer.Render(forced, 500);
        var gray = ToGray(bmp);

        var src = new RGBLuminanceSource(gray, bmp.PixelWidth, bmp.PixelHeight,
            RGBLuminanceSource.BitmapFormat.Gray8);
        var res = zx.Decode(src);
        var ok = res is not null && res.Text == text;

        if (ok) { decodable.Add(mask); }

        Console.WriteLine($"    掩码 {mask}: {(ok ? "✅ 可解" : "❌ 解不出")}"
                          + (mask == auto.Mask ? "   ← 自动选的" : ""));
    }

    Console.WriteLine($"    ⇒ 可用掩码: [{string.Join(",", decodable)}]"
                      + (decodable.Contains(auto.Mask) ? "  （自动选的在其中）" : "  ★ 自动选的**不可用**"));
    Console.WriteLine();
}

Console.WriteLine("--------------------------------------------------");
Console.WriteLine("解读：自动选的掩码若不在可用列表里，说明**掩码选择逻辑**有错。");

return 0;
