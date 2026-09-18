using System;
using Toolbox.Tools.QrCode;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

// ============================================================================
// 终极对照：让 ZXing 用**同一个版本、同一个纠错等级、同一个掩码**生成矩阵，
//           与我的矩阵逐模块比对。
//
// 做法：ZXing 的 QRCode 对象内部有 Matrix + maskPattern。用反射设掩码再渲染。
//       若两者矩阵一致 ⇒ 我的编码完全正确，问题在别处（渲染/解码器偏好）
//       若不一致       ⇒ 精确定位到哪些模块不同
// ============================================================================

Console.WriteLine("=== 与 ZXing 同版本同掩码逐模块比对 ===");
Console.WriteLine();

const string text = "桌面工具箱 · 二维码功能测试";

// 我这边的结果
var mine = QrEncoder.Encode(text, QrEcc.M);
Console.WriteLine($"我的：版本 {mine!.Version}，尺寸 {mine.Size}，掩码 {mine.Mask}");

// ZXing：手工编码，强制同版本 + 逐掩码
var payload = System.Text.Encoding.UTF8.GetBytes(text);
var bits = new BitArrayLite();
bits.Append(0b0100, 4);
bits.Append(payload.Length, 8);
foreach (var b in payload) { bits.Append(b, 8); }

Console.WriteLine();
Console.WriteLine("尝试用 ZXing 的 Encoder 直接编码，读出它的 maskPattern：");

try
{
    var content = new ZXing.QrCode.Internal.Encoder();
    var qr = content.encode(text, ErrorCorrectionLevel.M, null);

    Console.WriteLine($"  ZXing：版本 {qr.Version.VersionNumber}，掩码 ?，矩阵 {qr.Matrix.Width}×{qr.Matrix.Width}");

    if (qr.Matrix.Width != mine.Size)
    {
        Console.WriteLine("  版本不同，无法比对。");
    }
    else
    {
        var diff = 0;
        var samples = new System.Collections.Generic.List<string>();

        for (var y = 0; y < mine.Size; y++)
        {
            for (var x = 0; x < mine.Size; x++)
            {
                if (qr.Matrix[x, y] != mine[x, y])
                {
                    diff++;
                    if (samples.Count < 20) { samples.Add($"({x},{y})"); }
                }
            }
        }

        var total = mine.Size * mine.Size;
        Console.WriteLine($"  不同模块：{diff} / {total}  ({diff * 100.0 / total:F1}%)");

        if (diff > 0 && samples.Count > 0)
        {
            Console.WriteLine($"  前几个不同位置：{string.Join(" ", samples)}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  抛异常：{ex.GetType().Name}: {ex.Message}");
}

return 0;

// 极简位追加器（仅为上面演示，不参与产品）
file sealed class BitArrayLite
{
    public void Append(int value, int count) { }
}
