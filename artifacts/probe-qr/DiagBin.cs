using System;
using System.Linq;
using Toolbox.Tools.QrCode;

// ============================================================================
// 直接验：真定位图案中心 (97,97) 处，横/纵向游程到底长什么样？
// 为什么横向能命中、纵向却把它筛掉了？
// ============================================================================

Console.WriteLine("=== 真中心 (97,97) 的横纵游程 ===");
Console.WriteLine();

var m = QrEncoder.Encode("hello", QrEcc.M)!;
var bmp = QrRenderer.Render(m, 377);
var w = bmp.PixelWidth;
var h = bmp.PixelHeight;
var scale = w / (m.Size + 8);

var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
    bmp, System.Windows.Media.PixelFormats.Gray8, null, 0);
var gray = new byte[w * h];
conv.CopyPixels(gray, w, 0);

static byte[] Binarize(byte[] gray, int w, int h)
{
    var binary = new byte[w * h];
    var integral = new long[(w + 1) * (h + 1)];
    for (var y = 0; y < h; y++)
    {
        long rowSum = 0;
        for (var x = 0; x < w; x++)
        {
            rowSum += gray[y * w + x];
            integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + rowSum;
        }
    }

    const int half = 7;
    for (var y = 0; y < h; y++)
    {
        for (var x = 0; x < w; x++)
        {
            var x0 = Math.Max(0, x - half); var y0 = Math.Max(0, y - half);
            var x1 = Math.Min(w - 1, x + half); var y1 = Math.Min(h - 1, y + half);
            var count = (x1 - x0 + 1) * (y1 - y0 + 1);
            var sum = integral[(y1 + 1) * (w + 1) + x1 + 1]
                      - integral[y0 * (w + 1) + x1 + 1]
                      - integral[(y1 + 1) * (w + 1) + x0]
                      + integral[y0 * (w + 1) + x0];
            var mean = sum / count;
            binary[y * w + x] = (byte)(gray[y * w + x] < mean - 8 ? 1 : 0);
        }
    }

    return binary;
}

var binary = Binarize(gray, w, h);

// 二值化后 (97,97) 是深色吗
Console.WriteLine($"二值化后 (97,97) = {(binary[97 * w + 97] == 1 ? "深" : "浅")}（应为深）");
Console.WriteLine();

// 横向游程（y=97）
Console.WriteLine("横向游程（y=97）:");
PrintRuns(binary, w, 97, true);

Console.WriteLine();
Console.WriteLine("纵向游程（x=97）:");
PrintRuns(binary, h, 97, false);

Console.WriteLine();
Console.WriteLine($"每模块 {scale}px，定位图案 7 模块 = {7 * scale}px");
Console.WriteLine("期望纵向也是：浅(静区) 深1 浅1 深3 浅1 深1 浅(...)");

return 0;

void PrintRuns(byte[] bin, int len, int fixedCoord, bool horizontal)
{
    var runs = new System.Collections.Generic.List<(bool D, int L, int S)>();
    Func<int, bool> at = i => horizontal
        ? bin[fixedCoord * len + i] == 1
        : bin[i * len + fixedCoord] == 1;

    var cd = at(0); var cl = 1; var cs = 0;
    for (var i = 1; i < len; i++)
    {
        var d = at(i);
        if (d == cd) cl++;
        else { runs.Add((cd, cl, cs)); cd = d; cl = 1; cs = i; }
    }
    runs.Add((cd, cl, cs));

    var scale = len / 29.0;
    foreach (var (d, l, s) in runs)
    {
        Console.WriteLine($"  {(d ? "深" : "浅")} len={l,3} ({l / scale:F1} 模块) start={s,3}");
    }
}
