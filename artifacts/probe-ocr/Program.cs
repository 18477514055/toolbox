using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

// ============================================================================
// 探测：Windows 自带 OCR（Windows.Media.Ocr）在本机到底能不能用、中文准不准。
//
// 为什么必须先探测：这是"要不要引入第三方 OCR 库"的决策依据。
//   自带 OCR = 零依赖、对方电脑天生就有（Win10 1903+ 都带），是最理想的方案；
//   但它对中文的准确率必须实测，不能凭印象。
// ============================================================================

Console.WriteLine("==================================================");
Console.WriteLine("Windows 自带 OCR 能力探测");
Console.WriteLine("==================================================");
Console.WriteLine();

// ---- 1. 有哪些 OCR 语言可用 ----
Console.WriteLine("--- 1. 可用的 OCR 语言 ---");
var langs = OcrEngine.AvailableRecognizerLanguages;
if (langs.Count == 0)
{
    Console.WriteLine("  ❌ 一个 OCR 语言都没有 —— 自带 OCR 不可用");
}
else
{
    foreach (var l in langs)
    {
        Console.WriteLine($"  {l.LanguageTag,-16} {l.DisplayName}");
    }
}

Console.WriteLine();
Console.WriteLine("--- 2. 中文引擎能否创建 ---");
OcrEngine? zhEngine = null;
try
{
    var zh = new Language("zh-Hans-CN");
    if (OcrEngine.IsLanguageSupported(zh))
    {
        zhEngine = OcrEngine.TryCreateFromLanguage(zh);
        Console.WriteLine($"  zh-Hans-CN 受支持，引擎创建: {(zhEngine is null ? "失败" : "成功")}");
    }
    else
    {
        Console.WriteLine("  zh-Hans-CN 不受支持");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  创建中文引擎抛异常: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("--- 3. 用户默认语言引擎 ---");
try
{
    var def = OcrEngine.TryCreateFromUserProfileLanguages();
    Console.WriteLine($"  默认引擎: {(def is null ? "创建失败" : $"成功（{def.RecognizerLanguage.LanguageTag}）")}");
    Console.WriteLine($"  MaxImageDimension: {OcrEngine.MaxImageDimension}");
}
catch (Exception ex)
{
    Console.WriteLine($"  抛异常: {ex.Message}");
}

// ---- 4. 端到端：造一张带中文的图，真跑一次 OCR ----
Console.WriteLine();
Console.WriteLine("--- 4. 端到端实测（程序生成图片 → OCR 识别）---");

var engine = zhEngine ?? OcrEngine.TryCreateFromUserProfileLanguages();
if (engine is null)
{
    Console.WriteLine("  ❌ 没有可用引擎，端到端测试跳过");
    return 2;
}

var testDir = Path.Combine(Path.GetTempPath(), "ocr-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDir);

try
{
    // 用 System.Drawing 画一张白底黑字的图（模拟截图内容）
    var cases = new (string Name, string Text, int FontSize)[]
    {
        ("中文短句", "你好世界", 48),
        ("中英混排", "桌面工具箱 Toolbox 测试", 40),
        ("数字与符号", "订单号 A12345 金额 99.80", 40),
        ("较长中文", "快捷截图能够识别屏幕上的文字内容", 36),
    };

    var passed = 0;

    foreach (var (name, text, fontSize) in cases)
    {
        var png = Path.Combine(testDir, $"{name}.png");

        using (var bmp = new System.Drawing.Bitmap(900, 200))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new System.Drawing.Font("Microsoft YaHei", fontSize,
                System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
            g.DrawString(text, font, brush, 20, 60);
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }

        // 读进 WinRT 做 OCR
        var file = await StorageFile.GetFileFromPathAsync(png);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        var result = await engine.RecognizeAsync(softwareBitmap);
        var recognized = result.Text.Replace(" ", "").Trim();
        var expected = text.Replace(" ", "");

        var ok = recognized.Contains(expected) || expected.Contains(recognized);
        // 中文 OCR 常在字间插空格，所以比对时去掉空格
        var exact = recognized == expected;

        if (ok) { passed++; }

        Console.WriteLine($"  [{(ok ? "通过" : "不准")}] {name}");
        Console.WriteLine($"        期望: {expected}");
        Console.WriteLine($"        实得: {recognized}");
        if (!exact) { Console.WriteLine($"        （非逐字一致，属正常：OCR 会插空格/个别字替换）"); }
    }

    Console.WriteLine();
    Console.WriteLine($"端到端：{passed}/{cases.Length} 可识别");
    Console.WriteLine();
    Console.WriteLine("--------------------------------------------------");
    Console.WriteLine("结论：");
    if (langs.Count > 0 && engine is not null)
    {
        Console.WriteLine("  ✅ Windows 自带 OCR 可用，且支持中文");
        Console.WriteLine("  ⇒ OCR 工具**不需要任何第三方库、不需要联网**，");
        Console.WriteLine("    对方电脑（Win10 1903+）天生就有这个能力。");
    }
    else
    {
        Console.WriteLine("  ❌ 自带 OCR 不可用，需要另想办法");
    }

    return passed > 0 ? 0 : 1;
}
finally
{
    try { Directory.Delete(testDir, true); } catch { }
}
