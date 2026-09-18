using System;
using System.IO;
using System.Linq;
using Toolbox.Tools.Convert;

// ============================================================================
// 「缺依赖时不崩」验证
//
// 分享给别人时最怕的不是"某个功能用不了"，而是**缺一样东西整个程序打不开**。
// 本夹具直接调用产品源码里的能力探测逻辑，验证缺 LibreOffice / Office 时的行为。
//
// 注意：这里**故意不 mock**，而是看真实的探测实现会不会抛异常。
// ============================================================================

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"[通过] {name}" + (detail.Length > 0 ? $"\n        {detail}" : "")); }
    else { fail++; Console.WriteLine($"[失败] {name}" + (detail.Length > 0 ? $"\n        {detail}" : "")); }
}

Console.WriteLine("==================================================");
Console.WriteLine("缺依赖时的降级行为验证");
Console.WriteLine("==================================================");
Console.WriteLine();

// ---------------------------------------------------------------- 1. 格式矩阵
Console.WriteLine("--- 1. 格式支持矩阵（纯逻辑，任何机器都一样）---");

Check("图片格式被识别（.png）", Formats.IsImage("a.png"));
Check("Word 格式被识别（.docx）", Formats.IsWord("a.docx"));
Check("表格格式被识别（.xlsx）", Formats.IsSheet("a.xlsx"));
Check("PDF 被识别", Formats.IsPdf("a.pdf"));
Check("视频/音频**不被**识别为可转换（.mp4）", !Formats.IsSupported("a.mp4"),
    "⇒ 拖进来会被明确拒绝，而不是静默跳过");
Check("扩展名大小写不敏感（.PNG）", Formats.IsImage("a.PNG"));

// 各来源类型的可选目标
var pngTargets = Formats.TargetsFor("a.png");
var docxTargets = Formats.TargetsFor("a.docx");
Console.WriteLine($"        a.png  可选目标: {string.Join(", ", pngTargets)}");
Console.WriteLine($"        a.docx 可选目标: {string.Join(", ", docxTargets)}");

Check("图片有可选目标（不依赖任何外部程序，纯内置）", pngTargets.Count > 2);
Check("docx 有可选目标（需要 LibreOffice 或 Office）", docxTargets.Count > 0);

// ---------------------------------------------------------------- 2. 探测器不抛异常
Console.WriteLine();
Console.WriteLine("--- 2. 依赖探测器在「缺依赖」时**不抛异常**，而是返回 false + 原因 ---");

var office = new OfficeComConverter();
var availOk = true;
string? reason = null;
try
{
    var r = office.IsAvailable(out reason);
    Console.WriteLine($"        Office 可用 = {r}；原因 = {reason ?? "(无)"}");
}
catch (Exception ex)
{
    availOk = false;
    Console.WriteLine($"        抛了异常：{ex.GetType().Name}: {ex.Message}");
}

Check("OfficeComConverter.IsAvailable 不抛异常（缺 Office 也只是返回 false）",
    availOk, availOk ? "返回了布尔值 + 原因字符串" : "★ 这条会让缺 Office 的机器直接崩");

// 图片转换器必须**永远可用**（零依赖）
var img = new ImageConverter();
var imgOk = img.IsAvailable(out var imgReason);
Check("ImageConverter 永远可用（零依赖，不依赖任何外部程序）",
    imgOk, $"可用={imgOk}；原因={imgReason ?? "(无)"}");

// ---------------------------------------------------------------- 3. CanConvert 不抛异常
Console.WriteLine();
Console.WriteLine("--- 3. CanConvert 对任意输入都不抛异常 ---");

var probes = new[]
{
    ("a.png", "jpg"), ("a.png", "pdf"), ("a.docx", "pdf"),
    ("a.mp4", "mp3"), ("a.xyz", "pdf"), ("", "pdf"),
};

var canConvertOk = true;
foreach (var (src, tgt) in probes)
{
    try
    {
        var c1 = img.CanConvert(src, tgt);
        var c2 = office.CanConvert(src, tgt);
        Console.WriteLine($"        CanConvert(\"{src}\", \"{tgt}\") → 图片={c1}, Office={c2}");
    }
    catch (Exception ex)
    {
        canConvertOk = false;
        Console.WriteLine($"        ★ 抛异常 (\"{src}\",\"{tgt}\")：{ex.GetType().Name}: {ex.Message}");
    }
}

Check("CanConvert 对空串 / 未知扩展名 / 音视频**都不抛异常**",
    canConvertOk, canConvertOk ? "全部返回布尔值" : "★ 有输入能让它抛异常");

// ---------------------------------------------------------------- 4. 结论
Console.WriteLine();
Console.WriteLine("--------------------------------------------------");
Console.WriteLine($"结果：{pass}/{pass + fail} 通过");
Console.WriteLine("--------------------------------------------------");
Console.WriteLine();
Console.WriteLine("结论：缺 LibreOffice / Office 只会让**部分格式的转换不可用**，");
Console.WriteLine("      不会让程序崩溃或打不开 —— 这是「能不能分享」最关键的一条。");

return fail == 0 ? 0 : 1;
