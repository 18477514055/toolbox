using System;
using System.Linq;
using Toolbox.Tools.QrCode;

// ============================================================================
// 直接检查：真正的定位图案中心，在二值化后长什么样？
//
// 版本 1、377px、13px/模块、静区 4 模块：
//   左上定位图案中心 = 模块坐标 (3,3) + 静区 4 = (7,7) 模块 → 像素 (7*13+6, 7*13+6) = (97,97)
//   右上 = 模块 (17,3)+4 = (21,7) → 像素 (21*13+6, 97) = (279,97)
//   左下 = (7,21) → (97,279)
// 若扫描器找不到这些点，说明候选搜索/纵向确认把它筛掉了。
// ============================================================================

Console.WriteLine("=== 真定位图案中心处的实际像素 ===");
Console.WriteLine();

var m = QrEncoder.Encode("hello", QrEcc.M)!;
var bmp = QrRenderer.Render(m, 377);
var w = bmp.PixelWidth;
var h = bmp.PixelHeight;

var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
    bmp, System.Windows.Media.PixelFormats.Gray8, null, 0);
var gray = new byte[w * h];
conv.CopyPixels(gray, w, 0);

var scale = w / (m.Size + 8);
Console.WriteLine($"每模块 {scale}px，图像 {w}×{h}");
Console.WriteLine();

// 计算三个定位图案中心（矩阵坐标 (3,3) / (size-4,3) / (3,size-4)，加静区 4）
var positions = new[]
{
    ("左上", 3 + 4, 3 + 4),
    ("右上", m.Size - 4 + 4, 3 + 4),
    ("左下", 3 + 4, m.Size - 4 + 4),
};

foreach (var (name, mx, my) in positions)
{
    var px = mx * scale + scale / 2;
    var py = my * scale + scale / 2;

    Console.WriteLine($"{name}：模块({mx},{my}) → 像素({px},{py})  灰度={gray[py * w + px]}");
    Console.WriteLine($"    矩阵值应为深色: {m[mx - 4, my - 4]}");
}

Console.WriteLine();
Console.WriteLine("=== 该行的游程（y = 左上中心）===");
var cy = 3 + 4;
var yy = cy * scale + scale / 2;
var runs = new System.Collections.Generic.List<(bool D, int L, int S)>();
var cd = gray[yy * w] < 128; var cl = 1; var cs = 0;
for (var x = 1; x < w; x++)
{
    var d = gray[yy * w + x] < 128;
    if (d == cd) cl++;
    else { runs.Add((cd, cl, cs)); cd = d; cl = 1; cs = x; }
}
runs.Add((cd, cl, cs));

foreach (var (d, l, s) in runs)
{
    Console.WriteLine($"  {(d ? "深" : "浅")} len={l,3} ({l / (double)scale:F1} 模块) start={s,3}");
}

return 0;
