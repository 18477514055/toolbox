using System;
using System.Linq;
using Toolbox.Tools.QrCode;

// ============================================================================
// 直接调试扫描器的定位图案检测。
//
// 编码器已被 ZXing 证明是对的（24/24），所以问题一定在扫描器这一侧。
// 这里把二值化与扫描线的中间状态打出来，定位到底是哪一步漏了。
// ============================================================================

Console.WriteLine("=== 扫描器定位图案检测调试 ===");
Console.WriteLine();

var m = QrEncoder.Encode("hello", QrEcc.M)!;
var bmp = QrRenderer.Render(m, 377);

Console.WriteLine($"矩阵 {m.Size}×{m.Size}，位图 {bmp.PixelWidth}×{bmp.PixelHeight}");

// 取出灰度
var w = bmp.PixelWidth;
var h = bmp.PixelHeight;
var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
    bmp, System.Windows.Media.PixelFormats.Gray8, null, 0);
var gray = new byte[w * h];
conv.CopyPixels(gray, w, 0);

Console.WriteLine($"灰度范围: min={gray.Min()} max={gray.Max()}");
Console.WriteLine();

// 手算一条穿过定位图案中心的扫描线
// 定位图案在矩阵 (3,3) 附近（第一个 7×7 的中心），加静区 4 ⇒ 模块坐标 (3+0..6+4)=7
// 每模块像素 = 377 / (21+8) = 13
var scale = w / (m.Size + 8);
Console.WriteLine($"每模块像素 = {scale}");

// 左上定位图案中心：模块坐标 (3+4, 3+4) = (7,7) ⇒ 像素 (7*13+6, 7*13+6)
var cy = 7 * scale + scale / 2;
Console.WriteLine($"左上定位图案中心行 y = {cy}");
Console.WriteLine();

Console.WriteLine("该行的游程（从 x=0 开始）：");
var runs = new System.Collections.Generic.List<(bool Dark, int Len)>();
var cur = gray[cy * w] < 128;
var len = 0;
for (var x = 0; x < w; x++)
{
    var dark = gray[cy * w + x] < 128;
    if (dark == cur) { len++; }
    else { runs.Add((cur, len)); cur = dark; len = 1; }
}
runs.Add((cur, len));

foreach (var (dark, l) in runs)
{
    Console.WriteLine($"  {(dark ? "深" : "浅")} {l,3}px  ({l / (double)scale:F2} 模块)");
}

Console.WriteLine();
Console.WriteLine("期望看到：浅(静区4模块) 深(1) 浅(1) 深(3) 浅(1) 深(1) 浅 ...");

return 0;
