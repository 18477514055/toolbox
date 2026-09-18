using System;
using System.Collections.Generic;
using System.Linq;
using Toolbox.Tools.QrCode;
using QRCoder;

// ============================================================================
// 用**第二个独立实现（QRCoder）**当裁判，比对矩阵。
//
// QRCoder 是另一套完全独立的 QR 实现（不是 ZXing 的派生）。
// 让两个独立实现 + 我的实现三方比对：
//   · 若 QRCoder 与 ZXing 一致、与我不同 ⇒ 我错
//   · 若我与之相符 ⇒ 我对
//
// QRCoder 支持强制指定 mask（通过 QRCodeGenerator 的 mask 参数）。
// ============================================================================

Console.WriteLine("=== 三方可信度交叉验证（QRCoder / ZXing / 我的实现）===");
Console.WriteLine();

const string text = "桌面工具箱 · 二维码功能测试";

// 我这边
var mine = QrEncoder.Encode(text, QrEcc.M);
Console.WriteLine($"我的实现：版本 {mine!.Version}  掩码 {mine.Mask}  尺寸 {mine.Size}");

// QRCoder：强制同版本、M 级、遍历掩码
Console.WriteLine();
Console.WriteLine("QRCoder 对同一内容、同版本（3）、M 级，各掩码生成的图能否被 ZXing 解出：");
Console.WriteLine();

var zx = new ZXing.BarcodeReaderGeneric
{
    Options = new ZXing.Common.DecodingOptions
    {
        PossibleFormats = new[] { ZXing.BarcodeFormat.QR_CODE },
        TryHarder = true,
    },
};

for (var qrMask = 0; qrMask < 8; qrMask++)
{
    try
    {
        // QRCoder 的 mask 参数：-1 = 自动；0~7 = 强制
        var gen = new QRCodeGenerator();
        var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M,
            forceUtf8: true, utf8BOM: false, eciMode: QRCodeGenerator.EciMode.Utf8, requestedVersion: 3);
        var code = new QRCode(data);

        // 取模块矩阵
        var matrix = code.GetGraphic(10, System.Drawing.Color.Black, System.Drawing.Color.White, drawQuietZones: true);

        // 转成灰度
        var w = matrix.Width;
        var h = matrix.Height;
        var gray = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = matrix.GetPixel(x, y);
                gray[y * w + x] = (byte)((c.R * 299 + c.G * 587 + c.B * 114) / 1000);
            }
        }

        var src = new ZXing.RGBLuminanceSource(gray, w, h, ZXing.RGBLuminanceSource.BitmapFormat.Gray8);
        var res = zx.Decode(src);

        Console.WriteLine($"  QRCoder 自动选掩码：{(res is not null && res.Text == text ? "✅ 可解" : "❌ 解不出")}"
                          + $"  （尺寸 {w}）");

        break;   // QRCoder 不直接暴露 mask 选择，自动一次即可
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  抛异常：{ex.Message}");
        break;
    }
}

Console.WriteLine();
Console.WriteLine("--------------------------------------------------");
Console.WriteLine("说明：QRCoder 的 API 不直接暴露所选掩码，三方逐位比对成本较高。");
Console.WriteLine("      本诊断的目的是确认 ZXing 不是唯一的裁判来源。");

return 0;
