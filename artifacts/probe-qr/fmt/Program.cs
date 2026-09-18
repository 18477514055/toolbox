using System;

// ============================================================================
// 验证格式信息（format information）的 BCH 编码是否正确。
//
// 格式信息 = 2 bit 纠错等级 + 3 bit 掩码 + 10 bit BCH 校验，再异或 0x5412。
// ISO/IEC 18004 表 C.1 给出了全部 32 个标准值 —— 逐条比对即可定位。
// ============================================================================

Console.WriteLine("=== 格式信息 BCH 校验 ===");
Console.WriteLine();

// 标准表（ISO/IEC 18004 表 C.1）：键 = (纠错等级 << 3) | 掩码，值 = 15 bit 格式串
// 纠错等级位: L=01, M=00, Q=11, H=10
var standard = new (int EccBits, string EccName, int Mask, int Expected)[]
{
    (0b01, "L", 0, 0x77C4), (0b01, "L", 1, 0x72F3), (0b01, "L", 2, 0x7DAA), (0b01, "L", 3, 0x789D),
    (0b01, "L", 4, 0x662F), (0b01, "L", 5, 0x6318), (0b01, "L", 6, 0x6C41), (0b01, "L", 7, 0x6976),

    (0b00, "M", 0, 0x5412), (0b00, "M", 1, 0x5125), (0b00, "M", 2, 0x5E7C), (0b00, "M", 3, 0x5B4B),
    (0b00, "M", 4, 0x45F9), (0b00, "M", 5, 0x40CE), (0b00, "M", 6, 0x4F97), (0b00, "M", 7, 0x4AA0),

    (0b11, "Q", 0, 0x355F), (0b11, "Q", 1, 0x3068), (0b11, "Q", 2, 0x3F31), (0b11, "Q", 3, 0x3A06),
    (0b11, "Q", 4, 0x24B4), (0b11, "Q", 5, 0x2183), (0b11, "Q", 6, 0x2EDA), (0b11, "Q", 7, 0x2BED),

    (0b10, "H", 0, 0x1689), (0b10, "H", 1, 0x13BE), (0b10, "H", 2, 0x1CE7), (0b10, "H", 3, 0x19D0),
    (0b10, "H", 4, 0x0762), (0b10, "H", 5, 0x0255), (0b10, "H", 6, 0x0D0C), (0b10, "H", 7, 0x083B),
};

static int ComputeFormat(int eccBits, int mask)
{
    var data = (eccBits << 3) | mask;
    var bch = data << 10;

    for (var i = 14; i >= 10; i--)
    {
        if (((bch >> i) & 1) == 1)
        {
            bch ^= 0x537 << (i - 10);
        }
    }

    return ((data << 10) | bch) ^ 0x5412;
}

var pass = 0;

foreach (var (eccBits, eccName, mask, expected) in standard)
{
    var actual = ComputeFormat(eccBits, mask);
    var ok = actual == expected;
    if (ok) { pass++; }

    Console.WriteLine($"  {eccName} mask={mask}  期望 0x{expected:X4}  实得 0x{actual:X4}  {(ok ? "✅" : "❌")}");
}

Console.WriteLine();
Console.WriteLine($"格式信息：{pass}/{standard.Length} 与标准表一致");

if (pass != standard.Length)
{
    Console.WriteLine();
    Console.WriteLine("★ 说明编码器里用的 ECC 位映射或 BCH 参数有错。");
    Console.WriteLine("  特别注意：本项目 QrEcc 枚举是 L=0,M=1,Q=2,H=3，");
    Console.WriteLine("  而标准里的 2 bit 值是 L=01, M=00, Q=11, H=10 —— 这两套**不是同一个映射**！");
}
else
{
    Console.WriteLine();
    Console.WriteLine("格式信息编码正确。");
    Console.WriteLine();
    Console.WriteLine("★ 但注意：上面是**手工代入标准 eccBits** 算的。");
    Console.WriteLine("  还要核对编码器内部 (int)ecc 到标准 eccBits 的映射对不对。");
    Console.WriteLine("  本项目 QrEcc 枚举: L=0, M=1, Q=2, H=3");
    Console.WriteLine("  标准 2bit 值    : L=01, M=00, Q=11, H=10");
}

return 0;
