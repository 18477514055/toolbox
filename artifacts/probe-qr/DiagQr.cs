using System;
using System.Collections.Generic;
using System.Linq;
using Toolbox.Tools.QrCode;

// ============================================================================
// 定位 QR 编码器的 bug：为什么只有部分能过？
//
// 观察到的规律：
//   · 版本 1 有时过；版本 2/3 基本不过
//   · 越长越容易失败
// ⇒ 强烈怀疑**分块交错**或**数据容量/填充**算错。
//   这里把编码器的中间产物打出来核对。
// ============================================================================

Console.WriteLine("=== QR 编码中间产物核对 ===");
Console.WriteLine();

// 先用最小用例：版本 1、EC 等级 M、"hello"
var text = "hello";
foreach (var ecc in new[] { QrEcc.L, QrEcc.M, QrEcc.Q, QrEcc.H })
{
    var payload = System.Text.Encoding.UTF8.GetBytes(text);
    var version = QrEncoder.FindVersion(payload.Length, ecc);
    var info = QrEncoder.GetBlockInfo(version, ecc.ToString());

    Console.WriteLine($"\"{text}\"  {ecc}");
    Console.WriteLine($"  估算版本     : {version}");
    Console.WriteLine($"  数据码字总数 : {info.DataCodewords}");
    Console.WriteLine($"  每块纠错码字 : {info.EcPerBlock}");
    Console.WriteLine($"  组1: {info.G1Blocks} 块 × {info.G1Data} 数据码字");
    Console.WriteLine($"  组2: {info.G2Blocks} 块 × {info.G2Data} 数据码字");

    var totalData = info.G1Blocks * info.G1Data + info.G2Blocks * info.G2Data;
    var totalEc = (info.G1Blocks + info.G2Blocks) * info.EcPerBlock;
    var expectTotal = QrTotalCodewords(version);

    Console.WriteLine($"  校验：数据 {totalData} + 纠错 {totalEc} = {totalData + totalEc}，"
                      + $"该版本总码字应为 {expectTotal}  →  {(totalData + totalEc == expectTotal ? "✅ 对得上" : "❌ 对不上！")}");
    Console.WriteLine();
}

Console.WriteLine("--------------------------------------------------");
Console.WriteLine("各版本总码字参考值（ISO/IEC 18004）:");
Console.WriteLine("  v1=26 v2=44 v3=70 v4=100 v5=134 v6=172 v7=196 v8=242 v9=292 v10=346");
Console.WriteLine();

static int QrTotalCodewords(int version) => version switch
{
    1 => 26, 2 => 44, 3 => 70, 4 => 100, 5 => 134,
    6 => 172, 7 => 196, 8 => 242, 9 => 292, 10 => 346,
    _ => -1,
};
