using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using Toolbox.Tools.QrCode;

// ============================================================================
// 把扫描器内部的每一步都打出来，定位到底卡在哪。
// ============================================================================

Console.WriteLine("=== 扫描器逐步诊断 ===");
Console.WriteLine();

foreach (var text in new[] { "hello", "https://example.com" })
{
    Console.WriteLine($"内容「{text}」");

    var m = QrEncoder.Encode(text, QrEcc.M)!;
    var bmp = QrRenderer.Render(m, 377);

    Console.WriteLine($"  编码：版本 {m.Version}  尺寸 {m.Size}  掩码 {m.Mask}");
    Console.WriteLine($"  位图：{bmp.PixelWidth}×{bmp.PixelHeight}  每模块 {bmp.PixelWidth / (m.Size + 8)}px");

    // 走内部诊断钩子
    var diag = QrScanner.Diagnose(bmp);
    Console.WriteLine($"  ① 二值化：{diag.Width}×{diag.Height}  深色像素占比 {diag.DarkRatio:P1}");
    Console.WriteLine($"  ② 找到定位图案候选：{diag.FinderCandidates} 个，纵向确认后 {diag.FinderConfirmed} 个，聚类后 {diag.FinderClusters} 个");

    if (diag.Centers.Count > 0)
    {
        foreach (var c in diag.Centers)
        {
            Console.WriteLine($"       中心 ({c.X:F1},{c.Y:F1})");
        }
    }

    Console.WriteLine($"  ③ 三角选择：tl={diag.Tl?.ToString() ?? "无"} tr={diag.Tr?.ToString() ?? "无"} bl={diag.Bl?.ToString() ?? "无"}");
    Console.WriteLine($"  ④ 尝试过的版本与结果：");
    foreach (var attempt in diag.Attempts)
    {
        Console.WriteLine($"       {attempt}");
    }

    Console.WriteLine($"  ⑤ 最终：{(diag.Result.Success ? "成功 → " + diag.Result.Text : "失败 → " + diag.Result.Error.Split('\n')[0])}");
    Console.WriteLine();
}

return 0;
