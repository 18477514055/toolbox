using System;
using Toolbox.Tools.QrCode;

// ============================================================================
// 关键怀疑：掩码 2 失败而其余 7 个成功 —— 这不是"公式错"（公式错会全错），
// 而是**掩码作用的格子范围**有问题。
//
// 掩码只能作用于**数据模块**。如果我把某个功能图案误判成数据模块（或反之），
// 那么不同掩码受影响的程度不同：某些掩码恰好把那个格子翻成"正确"的样子，
// 另一些翻错 —— 表现就是"部分掩码能扫、部分不能"。
//
// 本诊断：统计每个版本下"被判为数据模块"的数量，与该版本**应有的数据模块数**对照。
// 数据模块总数 = 总模块数 - 功能图案模块数。这个数可以从标准容量反推。
// ============================================================================

Console.WriteLine("=== 数据模块数核对 ===");
Console.WriteLine();

for (var version = 1; version <= 10; version++)
{
    var size = version * 4 + 17;
    var total = size * size;

    var dataCount = 0;
    for (var y = 0; y < size; y++)
    {
        for (var x = 0; x < size; x++)
        {
            if (!QrMatrix.IsFunctionModule(version, x, y))
            {
                dataCount++;
            }
        }
    }

    // 该版本应有的码字总数（数据+纠错），乘 8 得到总位数
    var codewords = version switch
    {
        1 => 26, 2 => 44, 3 => 70, 4 => 100, 5 => 134,
        6 => 172, 7 => 196, 8 => 242, 9 => 292, 10 => 346,
        _ => 0,
    };

    var needBits = codewords * 8;
    var remainder = dataCount - needBits;   // 剩余位（标准允许有 0~7 位的剩余，用 0 填充）

    var ok = remainder >= 0 && remainder <= 7;

    Console.WriteLine($"  版本 {version,2}: 尺寸 {size,2}×{size,2}  数据模块 {dataCount,5}" +
                      $"  需 {needBits,5} 位  余 {remainder,3}  {(ok ? "✅" : "❌ 不合理")}");
}

Console.WriteLine();
Console.WriteLine("--------------------------------------------------");
Console.WriteLine("说明：数据模块数 - 码字总位数 应落在 0~7（标准允许的剩余位）。");
Console.WriteLine("      若为负 = 数据放不下；若明显大于 7 = 有功能图案没被排除。");

return 0;
