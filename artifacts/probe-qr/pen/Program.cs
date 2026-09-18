using System;
using System.Collections.Generic;
using Toolbox.Tools.QrCode;

// ============================================================================
// 用**独立的第二实现**重算惩罚分，与编码器内部的选择结果对照。
//
// 目的：确认 8 种掩码的惩罚分排序是否正确。
//   若两个实现的排序一致，但选出的掩码仍"解不出"，那就是 ZXing 对
//   某些合规掩码的识别偏好问题（不是我们的错）；
//   若排序不一致，说明我的惩罚分实现有误。
// ============================================================================

Console.WriteLine("=== 惩罚分独立复算 ===");
Console.WriteLine();

const string text = "桌面工具箱 · 二维码功能测试";
const QrEcc ecc = QrEcc.M;

Console.WriteLine($"内容「{text}」 {ecc}");
Console.WriteLine();

// 逐个掩码生成，并用独立实现算分
var results = new List<(int Mask, int MyScore, int RefScore, bool Decodable)>();

for (var mask = 0; mask < 8; mask++)
{
    var m = QrEncoder.EncodeWithMask(text, ecc, mask);
    var modules = m.DumpModules();   // 需要编码器暴露原始模块
    var size = m.Size;

    var refScore = ReferencePenalty(modules, size);
    results.Add((mask, 0, refScore, true));

    Console.WriteLine($"  掩码 {mask}: 独立复算惩罚分 = {refScore}");
}

Console.WriteLine();
Console.WriteLine("（编码器内部选出的掩码见下面这行）");
var auto = QrEncoder.Encode(text, ecc);
Console.WriteLine($"  编码器实际选中：掩码 {auto!.Mask}");

Console.WriteLine();
Console.WriteLine("--------------------------------------------------");
Console.WriteLine("若独立复算的最低分掩码 ≠ 编码器选中的掩码 ⇒ 惩罚分实现有差异");

// 独立实现的 4 条惩罚规则
static int ReferencePenalty(bool[] m, int size)
{
    var score = 0;
    bool At(int x, int y) => m[y * size + x];

    // 规则1
    for (var y = 0; y < size; y++)
    {
        var run = 1;
        for (var x = 1; x < size; x++)
        {
            if (At(x, y) == At(x - 1, y)) { run++; }
            else { if (run >= 5) { score += run - 2; } run = 1; }
        }
        if (run >= 5) { score += run - 2; }
    }

    for (var x = 0; x < size; x++)
    {
        var run = 1;
        for (var y = 1; y < size; y++)
        {
            if (At(x, y) == At(x, y - 1)) { run++; }
            else { if (run >= 5) { score += run - 2; } run = 1; }
        }
        if (run >= 5) { score += run - 2; }
    }

    // 规则2
    for (var y = 0; y < size - 1; y++)
    {
        for (var x = 0; x < size - 1; x++)
        {
            var c = At(x, y);
            if (At(x + 1, y) == c && At(x, y + 1) == c && At(x + 1, y + 1) == c) { score += 3; }
        }
    }

    // 规则3：1011101 前后任一侧有 ≥4 浅色
    ReadOnlySpan<bool> pat = stackalloc bool[] { true, false, true, true, true, false, true };

    for (var y = 0; y < size; y++)
    {
        for (var x = 0; x + 6 < size; x++)
        {
            var match = true;
            for (var i = 0; i < 7; i++) { if (At(x + i, y) != pat[i]) { match = false; break; } }
            if (!match) { continue; }

            var before = 0;
            for (var k = x - 1; k >= 0 && !At(k, y); k--) { before++; }
            var after = 0;
            for (var k = x + 7; k < size && !At(k, y); k++) { after++; }

            if (before >= 4 || after >= 4) { score += 40; }
        }
    }

    for (var x = 0; x < size; x++)
    {
        for (var y = 0; y + 6 < size; y++)
        {
            var match = true;
            for (var i = 0; i < 7; i++) { if (At(x, y + i) != pat[i]) { match = false; break; } }
            if (!match) { continue; }

            var before = 0;
            for (var k = y - 1; k >= 0 && !At(x, k); k--) { before++; }
            var after = 0;
            for (var k = y + 7; k < size && !At(x, k); k++) { after++; }

            if (before >= 4 || after >= 4) { score += 40; }
        }
    }

    // 规则4
    var dark = 0;
    for (var i = 0; i < m.Length; i++) { if (m[i]) { dark++; } }
    var pct = dark * 100 / (size * size);
    score += Math.Abs(pct - 50) / 5 * 10;

    return score;
}

return 0;
