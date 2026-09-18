namespace Toolbox.Tools.QrCode;

/// <summary>
/// QR 码的**数学核心**：GF(256) 伽罗华域运算 + Reed-Solomon 纠错码。
///
/// 这部分完全按 ISO/IEC 18004 标准实现，是确定性算法，没有任何外部依赖。
/// 单独成文件是因为它最独立、最容易单独验证（自检里直接断言已知的纠错码字）。
/// </summary>
internal static class QrReedSolomon
{
    /// <summary>
    /// GF(256) 的指数表与对数表。
    ///
    /// QR 用的生成多项式是 x^8 + x^4 + x^3 + x^2 + 1（0x11D）。
    /// 用查表法做乘除，比每次现算快得多，也是标准做法。
    /// </summary>
    private static readonly byte[] ExpTable = new byte[512];
    private static readonly byte[] LogTable = new byte[256];

    static QrReedSolomon()
    {
        // 本原元 α = 2
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            ExpTable[i] = (byte)x;
            LogTable[x] = (byte)i;
            x <<= 1;
            if (x >= 256)
            {
                x ^= 0x11D;   // 对生成多项式取模
            }
        }

        // 指数表延长一倍，省掉乘法时的取模
        for (var i = 255; i < 512; i++)
        {
            ExpTable[i] = ExpTable[i - 255];
        }
    }

    /// <summary>GF(256) 乘法。</summary>
    private static byte Multiply(byte a, byte b)
    {
        if (a == 0 || b == 0)
        {
            return 0;
        }

        return ExpTable[LogTable[a] + LogTable[b]];
    }

    /// <summary>
    /// 生成多项式 g(x) = (x-α^0)(x-α^1)...(x-α^(ecCount-1))。
    /// 系数按次数**从高到低**排列。
    /// </summary>
    private static byte[] GeneratorPolynomial(int ecCount)
    {
        var poly = new byte[] { 1 };

        for (var i = 0; i < ecCount; i++)
        {
            var next = new byte[poly.Length + 1];
            for (var j = 0; j < poly.Length; j++)
            {
                // 乘以 (x - α^i)，在 GF(256) 里减法就是异或
                next[j] ^= poly[j];
                next[j + 1] ^= Multiply(poly[j], ExpTable[i]);
            }

            poly = next;
        }

        return poly;
    }

    /// <summary>
    /// 对一个数据块计算 Reed-Solomon 纠错码字。
    /// </summary>
    /// <param name="data">数据码字。</param>
    /// <param name="ecCount">要生成多少个纠错码字。</param>
    /// <returns>纠错码字（长度 = ecCount）。</returns>
    public static byte[] ComputeErrorCorrection(ReadOnlySpan<byte> data, int ecCount)
    {
        var generator = GeneratorPolynomial(ecCount);

        // 标准的"多项式长除法"求余：把数据左移 ecCount 位后对生成多项式取余
        var remainder = new byte[ecCount];

        foreach (var b in data)
        {
            var factor = (byte)(b ^ remainder[0]);

            // 整体左移一位
            for (var i = 0; i < ecCount - 1; i++)
            {
                remainder[i] = remainder[i + 1];
            }

            remainder[ecCount - 1] = 0;

            // 减去 factor * generator
            for (var i = 0; i < ecCount; i++)
            {
                remainder[i] ^= Multiply(generator[i + 1], factor);
            }
        }

        return remainder;
    }
}
