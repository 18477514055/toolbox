using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Tools.Ocr;

// ============================================================================
// 定位 OCR 在**产品上下文**里返回空的原因。
//
// 独立探针（artifacts\probe-ocr）里识别是成功的，但同样的代码放进 SelfTest 就返回空。
// 差异点必须找出来 —— 不然就是"碰巧好了"，不是修好了。
// ============================================================================

Console.WriteLine("=== OCR 产品路径诊断 ===");
Console.WriteLine();

var tempDir = Path.Combine(Path.GetTempPath(), "ocr-diag-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);

try
{
    var png = Path.Combine(tempDir, "t.png");
    const string text = "你好世界";

    using (var bmp = new System.Drawing.Bitmap(1000, 180))
    using (var g = System.Drawing.Graphics.FromImage(bmp))
    {
        g.Clear(System.Drawing.Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new System.Drawing.Font("Microsoft YaHei", 48,
            System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
        g.DrawString(text, font, brush, 20, 50);
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
    }

    Console.WriteLine($"造图: {png} ({new FileInfo(png).Length} 字节)");

    // ---- 路线1：直接调 OcrEngineService（产品路径）----
    BitmapSource source;
    using (var fs = File.OpenRead(png))
    {
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        source = dec.Frames[0];
    }

    Console.WriteLine($"BitmapSource: {source.PixelWidth}x{source.PixelHeight} " +
                      $"format={source.Format} dpi={source.DpiX}x{source.DpiY}");
    Console.WriteLine($"IsFrozen={source.IsFrozen} CanFreeze={source.CanFreeze}");
    Console.WriteLine();

    if (!source.IsFrozen && source.CanFreeze) source.Freeze();
    Console.WriteLine($"Freeze 后 IsFrozen={source.IsFrozen}");
    Console.WriteLine();

    Console.WriteLine("--- 路线1：OcrEngineService.RecognizeAsync（产品路径）---");
    var r1 = await OcrEngineService.RecognizeAsync(source);
    Console.WriteLine($"  Success={r1.Success}");
    Console.WriteLine($"  Text=\"{r1.Text}\"");
    Console.WriteLine($"  Lines={r1.Lines.Count}");
    Console.WriteLine($"  Error={r1.Error}");
    Console.WriteLine();

    // ---- 路线2：完全照抄独立探针的写法，但用同一张图 ----
    Console.WriteLine("--- 路线2：照抄独立探针写法（PNG 文件 → WinRT 解码 → 识别）---");
    try
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
            new Windows.Globalization.Language("zh-Hans-CN"));
        Console.WriteLine($"  引擎: {(engine is null ? "null" : engine.RecognizerLanguage.LanguageTag)}");

        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(png);
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var sb = await decoder.GetSoftwareBitmapAsync();
        Console.WriteLine($"  SoftwareBitmap: {sb.PixelWidth}x{sb.PixelHeight} format={sb.BitmapPixelFormat}");

        var res = await engine!.RecognizeAsync(sb);
        Console.WriteLine($"  识别结果: \"{res.Text}\"  (Lines={res.Lines.Count})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  抛异常: {ex.GetType().Name}: {ex.Message}");
    }

    Console.WriteLine();
    Console.WriteLine("--- 路线3：产品路径但检查中间 PNG 字节 ---");
    try
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        enc.Save(ms);
        Console.WriteLine($"  编码出的 PNG 大小: {ms.Length} 字节");
        ms.Position = 0;

        var testPng = Path.Combine(tempDir, "encoded.png");
        File.WriteAllBytes(testPng, ms.ToArray());
        Console.WriteLine($"  已写出: {testPng}（可直接打开看是不是白图）");

        // 用同一张"编码后的图"再识别一次
        BitmapSource rt;
        using (var fs2 = File.OpenRead(testPng))
        {
            var d2 = BitmapDecoder.Create(fs2, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            rt = d2.Frames[0];
        }
        Console.WriteLine($"  往返后: {rt.PixelWidth}x{rt.PixelHeight} format={rt.Format}");

        var r3 = await OcrEngineService.RecognizeAsync(rt);
        Console.WriteLine($"  Success={r3.Success} Text=\"{r3.Text}\" Error={r3.Error}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  抛异常: {ex.GetType().Name}: {ex.Message}");
    }
}
finally
{
    Console.WriteLine();
    Console.WriteLine($"诊断产物保留在: {tempDir}");
}
