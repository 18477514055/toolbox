using System.Text;
using System.IO;
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Toolbox.Tools.Clipboard;
using Toolbox.Tools.Ai;
using Toolbox.Tools.Convert;

// ============================================================================
// 独立验收夹具（由 DSH 验收方编写，不属于原交付方）
//
// 目的：**不采信交付方自述**，用可复现的机械断言去核它的关键承诺。
// 只链接被测源码（见 csproj 的 <Compile Include>），验的是真源码本身。
// ============================================================================

var pass = 0;
var fail = 0;
var notes = new List<string>();

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"[通过] {name}" + (detail.Length > 0 ? $"\n        {detail}" : "")); }
    else { fail++; Console.WriteLine($"[失败] {name}" + (detail.Length > 0 ? $"\n        {detail}" : "")); }
}

Console.WriteLine("==================================================");
Console.WriteLine("桌面工具箱 · 独立验收夹具（验收方自写，非交付方自检）");
Console.WriteLine($"时间     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine("==================================================");
Console.WriteLine();

// ---------------------------------------------------------------- 1. 日期筛选
Console.WriteLine("--- 1. 剪贴板日期筛选（off-by-one 高发区）---");

var today = new DateTime(2026, 3, 1, 0, 0, 0); // 故意选 3/1：跨月边界

Check("今天 00:00 算「今天」", DateRangeFilter.Matches(new DateTime(2026, 3, 1, 0, 0, 0), DateRangeFilter.Today, today));
Check("今天 23:59 算「今天」", DateRangeFilter.Matches(new DateTime(2026, 3, 1, 23, 59, 59), DateRangeFilter.Today, today));
Check("昨天 23:59 **不**算「今天」", !DateRangeFilter.Matches(new DateTime(2026, 2, 28, 23, 59, 59), DateRangeFilter.Today, today));
Check("昨天 23:59 算「昨天」（跨月：2/28）", DateRangeFilter.Matches(new DateTime(2026, 2, 28, 23, 59, 59), DateRangeFilter.Yesterday, today));
Check("今天 00:00 **不**算「昨天」", !DateRangeFilter.Matches(new DateTime(2026, 3, 1, 0, 0, 0), DateRangeFilter.Yesterday, today));
Check("最近7天含今天（2/23 00:00 是第 7 天）", DateRangeFilter.Matches(new DateTime(2026, 2, 23, 0, 0, 0), DateRangeFilter.Last7Days, today));
Check("最近7天不含第 8 天（2/22 23:59）", !DateRangeFilter.Matches(new DateTime(2026, 2, 22, 23, 59, 59), DateRangeFilter.Last7Days, today));
Check("最近30天含今天（1/31 00:00，跨月）", DateRangeFilter.Matches(new DateTime(2026, 1, 31, 0, 0, 0), DateRangeFilter.Last30Days, today));
Check("最近30天不含第 31 天（1/30 23:59）", !DateRangeFilter.Matches(new DateTime(2026, 1, 30, 23, 59, 59), DateRangeFilter.Last30Days, today));
Check("时间戳损坏(MinValue)在任何档位都保留", DateRangeFilter.Matches(DateTime.MinValue, DateRangeFilter.Today, today)
    && DateRangeFilter.Matches(DateTime.MinValue, DateRangeFilter.Yesterday, today));

// ★ 这一条是交付方声称"已在函数内 .Date 归一化"的实证：传未归一化的 today
Check("today 传 DateTime.Now（未归一化）也不误判今天凌晨",
    DateRangeFilter.Matches(new DateTime(2026, 3, 1, 0, 30, 0), DateRangeFilter.Today, new DateTime(2026, 3, 1, 15, 30, 0)),
    "传 2026-03-01 15:30 当 today，00:30 的记录必须仍算「今天」");

// ---------------------------------------------------------------- 2. 语言判向
Console.WriteLine();
Console.WriteLine("--- 2. 中英双向判方向（阈值 0.30）---");

Check("纯中文 → 译成英文", LanguageDetector.TargetLanguageFor("今天天气很好，我们出去走走吧。") == "英文");
Check("纯英文 → 译成中文", LanguageDetector.TargetLanguageFor("The weather is nice today.") == "中文");
Check("空串按中文处理（不崩）", LanguageDetector.IsMostlyChinese(""));
Check("纯数字/符号（无字母）按中文处理", LanguageDetector.IsMostlyChinese("12345 !!! ---"));

// ⚠️ 探边界：交付方在 06 报告 §五 自认「中英混排带大量代码的技术文档可能被判成英文」
var techDoc = "这是一个中文技术文档，讲解如何配置 Nginx 和 Docker。\n" +
              "第一步：docker run -d --name web -p 80:80 nginx:latest\n" +
              "第二步：修改 nginx.conf，把 root 指向 /var/www/html\n" +
              "第三步：docker restart web，然后用 curl 验证";
var techIsChinese = LanguageDetector.IsMostlyChinese(techDoc);
var techTarget = LanguageDetector.TargetLanguageFor(techDoc);
notes.Add($"技术文档判向 = {(techIsChinese ? "中文" : "英文")} → 译成{techTarget}");

Check("中文技术文档（含大量英文代码/术语）方向应为「中文→英文」",
    techTarget == "英文",
    $"实际译成{techTarget}；CJK 占比低于 30% 时会被判成英文为主。这是交付方自己标注的已知假阳性风险。");

// ---------------------------------------------------------------- 3. 文件命名
Console.WriteLine();
Console.WriteLine("--- 3. 不覆盖同名文件 ---");

var tmp = Path.Combine(Path.GetTempPath(), "toolbox-verify-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
try
{
    var target = Path.Combine(tmp, "照片.jpg");
    Check("目标不存在 → 原样返回", FileNaming.UniquePath(target) == target);

    File.WriteAllText(target, "x");
    var second = FileNaming.UniquePath(target);
    Check("目标已存在 → 加 (1)", Path.GetFileName(second) == "照片 (1).jpg", $"实际：{Path.GetFileName(second)}");

    File.WriteAllText(second, "x");
    var third = FileNaming.UniquePath(target);
    Check("再加一个 → 加 (2)", Path.GetFileName(third) == "照片 (2).jpg", $"实际：{Path.GetFileName(third)}");

    File.WriteAllText(Path.Combine(tmp, "无扩展名"), "x");
    var noExt = FileNaming.UniquePath(Path.Combine(tmp, "无扩展名"));
    Check("无扩展名文件也不覆盖", Path.GetFileName(noExt) == "无扩展名 (1)", $"实际：{Path.GetFileName(noExt)}");
}
finally
{
    try { Directory.Delete(tmp, true); } catch { }
}

// ---------------------------------------------------------------- 4. 截图坐标换算
Console.WriteLine();
Console.WriteLine("--- 4. 快捷截图：逻辑坐标 → 物理像素换算 ---");
Console.WriteLine("     现状：系数取**窗口所在显示器的真实 DPI**（dpi/96），见 Core\\ScreenCoordinateMapper.cs");

// 【保留的历史证据】旧算法：交付方原来的写法 —— 「位图像素宽 ÷ 背景图显示宽」这**一个全局比值**。
//   单屏下它恰好等于 dpi/96，所以看不出问题；多屏混合 DPI 时必然算错。
//   这里保留它，是为了让"旧算法确实会错"这件事**持续可复现**（而不是只在报告里写一句话）。
static (int x, int y, int w, int h) MapSelOld(
    (double X, double Y, double W, double H) sel,
    double backdropLogicalW, double backdropLogicalH,
    int bitmapPxW, int bitmapPxH)
{
    var scaleX = bitmapPxW / backdropLogicalW;
    var scaleY = bitmapPxH / backdropLogicalH;
    var x = (int)Math.Round(sel.X * scaleX);
    var y = (int)Math.Round(sel.Y * scaleY);
    var cw = (int)Math.Round(sel.W * scaleX);
    var ch = (int)Math.Round(sel.H * scaleY);
    cw = Math.Max(1, Math.Min(cw, bitmapPxW - x));
    ch = Math.Max(1, Math.Min(ch, bitmapPxH - y));
    return (x, y, cw, ch);
}

// 现状算法：直接用被测源码里的纯函数
static (int x, int y, int w, int h) MapSel(
    (double X, double Y, double W, double H) sel,
    double scale, int bitmapPxW, int bitmapPxH)
{
    var r = Toolbox.Core.ScreenCoordinateMapper.ToPixelRect(
        sel.X, sel.Y, sel.W, sel.H, scale, scale, bitmapPxW, bitmapPxH);
    return (r.X, r.Y, r.Width, r.Height);
}

{
    // 150% 单屏：用户在逻辑坐标 (100,100) 拖出 200x100 的框
    // 期望物理像素：x=150, y=150, w=300, h=150
    var r = MapSel((100, 100, 200, 100), 1.5, 1920, 1080);
    Check("150% 缩放：逻辑(100,100,200x100) → 物理(150,150,300x150)",
        r == (150, 150, 300, 150), $"实际：{r}");

    // 100% 单屏：逻辑 == 物理
    var r100 = MapSel((300, 200, 400, 300), 1.0, 1920, 1080);
    Check("100% 缩放：逻辑(300,200,400x300) → 物理(300,200,400x300)",
        r100 == (300, 200, 400, 300), $"实际：{r100}");

    // 125% / 200%
    Check("125% 缩放：逻辑(200,160,400x240) → 物理(250,200,500x300)",
        MapSel((200, 160, 400, 240), 1.25, 2400, 1350) == (250, 200, 500, 300));
    Check("200% 缩放：逻辑(50,60,100x80) → 物理(100,120,200x160)",
        MapSel((50, 60, 100, 80), 2.0, 3840, 2160) == (100, 120, 200, 160));

    // ★★ 关键场景：多屏且**两块屏 DPI 不同**（主屏 150%，副屏 100%，副屏在右）
    //   物理虚拟屏幕 3840 宽，WPF 逻辑虚拟屏幕 3200 宽。
    //   旧算法只能用全局系数 3840/3200 = 1.2 —— 主屏上本来该 ×1.5。
    //   现在：系数取自**窗口所在显示器**，主屏上就是 1.5 ⇒ 正确。
    var fixedResult = MapSel((100, 100, 200, 100), 1.5, 3840, 1080);
    Check("★ 多屏 + 混合 DPI：主屏框选换算正确（这是本轮修掉的缺陷）",
        fixedResult == (150, 150, 300, 150),
        $"实际 {fixedResult}，正确值 (150,150,300x150)");

    // 同一场景下旧算法的结果 —— 断言它**确实不同**，证明这个修复是有意义的
    var oldResult = MapSelOld((100, 100, 200, 100), 3200, 720, 3840, 1080);
    Check("★ 旧算法在同一场景下确实算错（证明修复有意义）",
        oldResult != (150, 150, 300, 150),
        $"旧算法给 ({oldResult.x},{oldResult.y},{oldResult.w}x{oldResult.h})，" +
        $"水平偏 {150 - oldResult.x} px、宽度差 {300 - oldResult.w} px");

    // 单屏 100%（本机现状）：新旧算法必须一致 ⇒ 修复对现有用户零影响
    var newSingle = MapSel((300, 200, 400, 300), 1.0, 1920, 1080);
    var oldSingle = MapSelOld((300, 200, 400, 300), 1920, 1080, 1920, 1080);
    Check("单屏 100%：新旧算法结果一致（对现有环境零行为变化）",
        newSingle == oldSingle, $"新 {newSingle} vs 旧 {oldSingle}");

    // 越界夹取 + 非法系数兜底（旧实现没有这两条）
    var clamp = MapSel((1900, 1070, 500, 500), 1.0, 1920, 1080);
    Check("越界选区被夹取到位图内（不会让 CroppedBitmap 抛异常）",
        clamp.x >= 0 && clamp.y >= 0 && clamp.x + clamp.w <= 1920
        && clamp.y + clamp.h <= 1080 && clamp.w >= 1 && clamp.h >= 1,
        $"实际 {clamp}");
}

// ---------------------------------------------------------------- 5. 热键解析
Console.WriteLine();
Console.WriteLine("--- 5. 热键字符串解析（反射调用被测静态方法）---");

var hkType = Type.GetType("Toolbox.Shell.HotKeyManager, Toolbox");
if (hkType is null)
{
    Console.WriteLine("[跳过] 未能加载 Toolbox.Shell.HotKeyManager（本夹具未链接该文件）");
}
else
{
    var parse = hkType.GetMethod("TryParse",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
    object?[] parseArgs = { "Win+Shift+V", 0u, 0u, null };
    var ok = (bool)parse.Invoke(null, parseArgs)!;
    Check("Win+Shift+V 解析成功", ok);
}

// ---------------------------------------------------------------- 汇总
Console.WriteLine();
Console.WriteLine("--------------------------------------------------");
Console.WriteLine($"独立验收夹具结果：{pass}/{pass + fail} 通过 —— {(fail == 0 ? "全部通过" : $"有 {fail} 项失败")}");
if (notes.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("【记录到的观察（不一定是缺陷，但需要知情）】");
    foreach (var n in notes) Console.WriteLine("  · " + n);
}
Console.WriteLine("--------------------------------------------------");

Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "out"));
File.WriteAllText(
    Path.Combine(AppContext.BaseDirectory, "out", "verify-report.json"),
    System.Text.Json.JsonSerializer.Serialize(new
    {
        时间 = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        通过 = $"{pass}/{pass + fail}",
        失败数 = fail,
        观察 = notes,
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
    new UTF8Encoding(false));

return fail == 0 ? 0 : 1;
