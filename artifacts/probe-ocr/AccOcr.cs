using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Toolbox.Tools.Ocr;

// ============================================================================
// 定位「快捷截图能够识别屏幕上的文字」这句为什么被认成「…识另刂…」。
//
// 要回答的问题：这是**引擎的能力上限**，还是**我造图参数不对**？
//   · 若能通过调字号/字体/DPI 修好 ⇒ 是测试造图的问题，该调测试；
//   · 若怎么调都错 ⇒ 是 Windows OCR 对"别"这个字的固有弱点，该写进 README 的已知限制。
// ============================================================================

Console.WriteLine("=== 「别」字识别率诊断 ===");
Console.WriteLine();

var tempDir = Path.Combine(Path.GetTempPath(), "ocr-acc-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);

try
{
    const string text = "快捷截图能够识别屏幕上的文字";

    // 变量：字号 × 字体
    var configs = new (string Label, string Font, int Size)[]
    {
        ("雅黑 48px", "Microsoft YaHei", 48),
        ("雅黑 64px", "Microsoft YaHei", 64),
        ("雅黑 80px", "Microsoft YaHei", 80),
        ("雅黑 96px", "Microsoft YaHei", 96),
        ("宋体 64px", "SimSun", 64),
        ("微软雅黑UI 64px", "Microsoft YaHei UI", 64),
    };

    foreach (var (label, fontName, size) in configs)
    {
        var png = Path.Combine(tempDir, $"a-{Guid.NewGuid():N}.png");

        using (var bmp = new System.Drawing.Bitmap(1600, 260))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new System.Drawing.Font(fontName, size,
                System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
            g.DrawString(text, font, brush, 20, 80);
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }

        BitmapSource src;
        using (var fs = File.OpenRead(png))
        {
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            src = dec.Frames[0];
        }

        var r = await OcrEngineService.RecognizeAsync(src);
        var got = r.Text.Replace(" ", "").Trim();
        var ok = got == text;
        var hasBie = got.Contains("别");
        var hasWrong = got.Contains("另");

        Console.WriteLine($"{label,-20} {(ok ? "✅ 完全正确" : "❌")}");
        Console.WriteLine($"    实得: {got}");
        if (!ok)
        {
            Console.WriteLine($"    「别」识别成功={hasBie}；出现错误的「另」={hasWrong}");
        }

        try { File.Delete(png); } catch { }
    }

    Console.WriteLine();
    Console.WriteLine("--------------------------------------------------");
    Console.WriteLine("结论：");
    Console.WriteLine("  若各字号都错在同一个字 ⇒ Windows OCR 对该字的固有弱点，");
    Console.WriteLine("  属于**引擎能力上限**，应写进 README 的「已知限制」，");
    Console.WriteLine("  并且自检用例不应断言「整句逐字一致」（那等于要求 OCR 零错误）。");
}
finally
{
    try { Directory.Delete(tempDir, true); } catch { }
}
