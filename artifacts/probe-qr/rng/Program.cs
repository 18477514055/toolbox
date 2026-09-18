using System;
using System.Windows.Media.Imaging;
using Toolbox.Tools.QrCode;

// ============================================================================
// 最后一块拼图：掩码 2 为何唯独失败？
//
// 已知：其余 7 个掩码都能解，说明数据、ECC、矩阵布局、格式信息**都是对的**。
//       数据模块数也与标准完全吻合。
// ⇒ 差异只可能出在"掩码 2 作用在了不该作用的格子上"。
//
// 由于掩码 2 的公式是 `x % 3 == 0`（按列），它会翻转**整列**。
// 如果有一列功能图案被误判成数据模块，只有会影响整列外观的掩码才会暴露问题 ——
// 掩码 2 正是其中之一（还有掩码 1 按行、掩码 4 按块）。
//
// 本诊断：把"被掩码翻转的格子"单独画出来，看有没有功能图案被卷进去。
// ============================================================================

Console.WriteLine("=== 掩码 2 的翻转范围检查 ===");
Console.WriteLine();

const int version = 3;   // 失败用例的版本
var size = version * 4 + 17;

Console.WriteLine($"版本 {version}，尺寸 {size}×{size}");
Console.WriteLine();
Console.WriteLine("被掩码 2 (x%3==0) 翻转的列：");

for (var x = 0; x < size; x++)
{
    if (x % 3 != 0)
    {
        continue;
    }

    // 这一列里有多少格子是"非功能图案"（会被翻转）
    var flipped = 0;
    var functionCells = 0;

    for (var y = 0; y < size; y++)
    {
        if (QrMatrix.IsFunctionModule(version, x, y))
        {
            functionCells++;
        }
        else
        {
            flipped++;
        }
    }

    var marker = "";
    // 第 6 列是定时图案 —— 若被卷入就说明 IsFunctionModule 有漏
    if (x == 6) { marker = "  ← 定时图案列！"; }

    Console.WriteLine($"  x={x,2}: 功能格 {functionCells,2}  可翻转 {flipped,2}{marker}");
}

Console.WriteLine();
Console.WriteLine("=== 关键核对：功能图案列 x=6 有没有被漏掉 ===");
var missed = 0;
for (var y = 0; y < size; y++)
{
    if (!QrMatrix.IsFunctionModule(version, 6, y))
    {
        missed++;
        Console.WriteLine($"  ★ (6,{y}) 被判为**数据模块**，但它在定时图案列上！");
    }
}

Console.WriteLine(missed == 0 ? "  ✅ x=6 整列都被正确识别为功能图案" : $"  ❌ 有 {missed} 个漏判");

Console.WriteLine();
Console.WriteLine("=== 同理核对 y=6 行 ===");
missed = 0;
for (var x = 0; x < size; x++)
{
    if (!QrMatrix.IsFunctionModule(version, x, 6))
    {
        missed++;
        Console.WriteLine($"  ★ ({x},6) 被判为**数据模块**，但它在定时图案行上！");
    }
}

Console.WriteLine(missed == 0 ? "  ✅ y=6 整行都被正确识别为功能图案" : $"  ❌ 有 {missed} 个漏判");

Console.WriteLine();
Console.WriteLine("=== 定位图案区核对（左上 9×9）===");
missed = 0;
for (var y = 0; y < 9; y++)
{
    for (var x = 0; x < 9; x++)
    {
        if (!QrMatrix.IsFunctionModule(version, x, y)) { missed++; }
    }
}

Console.WriteLine(missed == 0 ? "  ✅ 左上 9×9 全部为功能图案" : $"  ❌ 有 {missed} 个漏判");

return 0;
