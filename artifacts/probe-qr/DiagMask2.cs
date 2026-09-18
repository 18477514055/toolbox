using System;
using System.Windows.Media.Imaging;
using Toolbox.Tools.QrCode;
using ZXing;
using ZXing.Common;

// ============================================================================
// 结论验证：掩码 2（惩罚分最低、符合标准）为什么 ZXing 解不出？
//
// 要排除两种可能：
//   A. 我们生成的那张图确实有缺陷（比如渲染时模块边界有 1px 误差）
//   B. ZXing 对这个特定的合规掩码就是识别不了（它对某些花纹敏感）
//
// 判别方法：**用 ZXing 自己生成同一个内容的二维码**，看它能不能自解。
//   若 ZXing 自己生成的能自解，而我这个不能 ⇒ 更可能是我们的图有问题；
//   再进一步：把我的矩阵**按 2 倍像素、不同 quiet zone** 渲染多张，看是否与渲染有关。
// ============================================================================

Console.WriteLine("=== 掩码 2 失败原因分析 ===");
Console.WriteLine();

const string text = "桌面工具箱 · 二维码功能测试";
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

static bool TryDecode(BarcodeReaderGeneric reader, BitmapSource bmp, string expect)
{
    var gray = ToGray(bmp);
    var src = new RGBLuminanceSource(gray, bmp.PixelWidth, bmp.PixelHeight,
        RGBLuminanceSource.BitmapFormat.Gray8);
    var r = reader.Decode(src);
    return r is not null && r.Text == expect;
}

// ① 不同尺寸下测掩码 2
Console.WriteLine("① 掩码 2 在不同渲染尺寸下：");
var target = QrEncoder.EncodeWithMask(text, QrEcc.M, 2);
foreach (var size in new[] { 200, 300, 400, 500, 600, 800, 1000 })
{
    var bmp = QrRenderer.Render(target, size);
    var ok = TryDecode(zx, bmp, text);
    Console.WriteLine($"     {size,5}px → 实际 {bmp.PixelWidth}px  {(ok ? "✅ 可解" : "❌ 解不出")}");
}

Console.WriteLine();

// ② ZXing 自己生成的二维码能否自解（证明 ZXing 解码器本身是好的）
Console.WriteLine("② ZXing 自己生成同一内容，能否自解（对照组）：");
try
{
    var writer = new ZXing.QrCode.QRCodeWriter();
    var hints = new System.Collections.Generic.Dictionary<EncodeHintType, object>
    {
        [EncodeHintType.CHARACTER_SET] = "UTF-8",
        [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
    };

    var zm = writer.encode(text, BarcodeFormat.QR_CODE, 500, 500, hints);
    var w = zm.Width; var h = zm.Height;
    var gray = new byte[w * h];
    for (var y = 0; y < h; y++)
    {
        for (var x = 0; x < w; x++)
        {
            gray[y * w + x] = zm[x, y] ? (byte)0 : (byte)255;
        }
    }
    var src = new RGBLuminanceSource(gray, w, h, RGBLuminanceSource.BitmapFormat.Gray8);
    var res = zx.Decode(src);
    Console.WriteLine($"     ZXing 自产自解：{(res is not null && res.Text == text ? "✅ 成功" : "❌ 失败")}"
                      + $"  尺寸 {w}");
}
catch (Exception ex)
{
    Console.WriteLine($"     抛异常：{ex.Message}");
}

Console.WriteLine();
Console.WriteLine("③ 其余 7 个掩码在 500px 下的表现：");
for (var mask = 0; mask < 8; mask++)
{
    var m = QrEncoder.EncodeWithMask(text, QrEcc.M, mask);
    var bmp = QrRenderer.Render(m, 500);
    Console.WriteLine($"     掩码 {mask}: {(TryDecode(zx, bmp, text) ? "✅" : "❌")}");
}

Console.WriteLine();
Console.WriteLine("--------------------------------------------------");
Console.WriteLine("判读：");
Console.WriteLine("  · 若掩码 2 只是在小尺寸失败、大尺寸成功 ⇒ 是渲染/采样太粗的问题，可修");
Console.WriteLine("  · 若所有尺寸都失败，而其它 7 个掩码都成功 ⇒ 该掩码花纹对 ZXing 不友好");
Console.WriteLine("    （标准允许任选合规掩码；真实扫码器通常比 ZXing 宽容，但仍应规避）");

return 0;
