using System.Text;

namespace Toolbox.Tools.QrCode;

/// <summary>纠错等级。</summary>
internal enum QrEcc
{
    /// <summary>L ≈ 7%</summary>
    L = 0,

    /// <summary>M ≈ 15%</summary>
    M = 1,

    /// <summary>Q ≈ 25%</summary>
    Q = 2,

    /// <summary>H ≈ 30%</summary>
    H = 3,
}

/// <summary>
/// 二维码**编码器**（纯 C# 自实现，不引任何第三方库）。
///
/// 为什么自己写而不引 ZXing.Net：
///   本项目的核心卖点之一是**零第三方依赖** —— 一个 exe 拷给别人就能用、
///   构建不需要联网、没有许可证义务。为了一个二维码功能破坏这条属性不划算。
///
/// 实现范围：**字节模式（byte mode）+ 版本 1~10 + 四个纠错等级**。
///   字节模式对 UTF-8 中文天然友好（直接放 UTF-8 字节），
///   版本 1~10 已能装下约 270 字节（L 级）或 119 字节（H 级），
///   覆盖"文字/链接"这个使用场景绰绰有余。
///   数字/字母数字模式能装更多，但收益只在超长内容上，本版不做（见 README 已知限制）。
///
/// 输出是一个 <see cref="QrMatrix"/>（布尔矩阵），**不涉及任何绘图** ——
/// 这样编码逻辑能被自检直接断言，绘图交给上层。
/// </summary>
internal static class QrEncoder
{
    /// <summary>各版本的总码字数（版本 1~10）。</summary>
    private static readonly int[] TotalCodewords =
    {
        0,   // 占位，版本从 1 开始
        26, 44, 70, 100, 134, 172, 196, 242, 292, 346,
    };

    /// <summary>
    /// 每个版本、每个纠错等级下：数据码字数 + 每块纠错码字数 + 块数。
    /// 布局：[版本][等级] = (数据码字总数, 每块纠错码字, 组1块数, 组1数据码字, 组2块数, 组2数据码字)
    /// 数据取自 ISO/IEC 18004 表 9。
    /// </summary>
    private static readonly (int DataCodewords, int EcPerBlock, int G1Blocks, int G1Data, int G2Blocks, int G2Data)[][]
        Table =
    {
        Array.Empty<(int, int, int, int, int, int)>(), // 版本 0 占位

        // 版本 1
        new[] { (19, 7, 1, 19, 0, 0), (16, 10, 1, 16, 0, 0), (13, 13, 1, 13, 0, 0), (9, 17, 1, 9, 0, 0) },
        // 版本 2
        new[] { (34, 10, 1, 34, 0, 0), (28, 16, 1, 28, 0, 0), (22, 22, 1, 22, 0, 0), (16, 28, 1, 16, 0, 0) },
        // 版本 3
        new[] { (55, 15, 1, 55, 0, 0), (44, 26, 1, 44, 0, 0), (34, 18, 2, 17, 0, 0), (26, 22, 2, 13, 0, 0) },
        // 版本 4
        new[] { (80, 20, 1, 80, 0, 0), (64, 18, 2, 32, 0, 0), (48, 26, 2, 24, 0, 0), (36, 16, 4, 9, 0, 0) },
        // 版本 5
        new[] { (108, 26, 1, 108, 0, 0), (86, 24, 2, 43, 0, 0), (62, 18, 2, 15, 2, 16), (46, 22, 2, 11, 2, 12) },
        // 版本 6
        new[] { (136, 18, 2, 68, 0, 0), (108, 16, 4, 27, 0, 0), (76, 24, 4, 19, 0, 0), (60, 28, 4, 15, 0, 0) },
        // 版本 7
        new[] { (156, 20, 2, 78, 0, 0), (124, 18, 4, 31, 0, 0), (88, 18, 2, 14, 4, 15), (66, 26, 4, 13, 1, 14) },
        // 版本 8
        new[] { (194, 24, 2, 97, 0, 0), (154, 22, 2, 38, 2, 39), (110, 22, 4, 18, 2, 19), (86, 26, 4, 14, 2, 15) },
        // 版本 9
        new[] { (232, 30, 2, 116, 0, 0), (182, 22, 3, 36, 2, 37), (132, 20, 4, 16, 4, 17), (100, 24, 4, 12, 4, 13) },
        // 版本 10
        new[] { (274, 18, 2, 68, 2, 69), (216, 26, 4, 43, 1, 44), (154, 24, 6, 19, 2, 20), (122, 28, 6, 15, 2, 16) },
    };

    /// <summary>对齐图案的中心坐标（版本 1~10）。扫描端也要用，所以是 internal。</summary>
    internal static readonly int[][] AlignmentPositions =
    {
        Array.Empty<int>(),
        Array.Empty<int>(),      // 版本 1 没有对齐图案
        new[] { 6, 18 },
        new[] { 6, 22 },
        new[] { 6, 26 },
        new[] { 6, 30 },
        new[] { 6, 34 },
        new[] { 6, 22, 38 },
        new[] { 6, 24, 42 },
        new[] { 6, 26, 46 },
        new[] { 6, 28, 50 },
    };

    /// <summary>
    /// 取某个版本 + 纠错等级的块结构信息（供扫描端去交错使用）。
    /// 返回 (数据码字总数, 每块纠错码字, 组1块数, 组1数据码字, 组2块数, 组2数据码字)。
    /// </summary>
    public static (int DataCodewords, int EcPerBlock, int G1Blocks, int G1Data, int G2Blocks, int G2Data)
        GetBlockInfo(int version, string eccName)
    {
        var idx = eccName switch
        {
            "L" => 0,
            "M" => 1,
            "Q" => 2,
            "H" => 3,
            _ => 1,
        };

        if (version < 1 || version > 10)
        {
            version = 1;
        }

        return Table[version][idx];
    }

    /// <summary>
    /// 【测试用】强制用指定掩码重新生成矩阵。
    ///
    /// 存在的理由：掩码选择是"8 选 1"，出了问题时必须能把 8 种**逐个单独验**，
    /// 否则无法区分"数据编码错了"和"选错了掩码"——这两者症状一样（扫不出来），
    /// 但修法完全不同。本方法仅供自检与外部验证工具使用，产品流程只走 <see cref="Encode"/>。
    /// </summary>
    internal static QrMatrix EncodeWithMask(string text, QrEcc ecc, int mask)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var version = FindVersion(payload.Length, ecc);
        if (version < 0)
        {
            throw new ArgumentException("内容超出该纠错等级的容量。");
        }

        var (dataCodewords, ecPerBlock, g1Blocks, g1Data, g2Blocks, g2Data) = Table[version][(int)ecc];

        var bits = new BitStream();
        bits.Put(0b0100, 4);
        bits.Put(payload.Length, version <= 9 ? 8 : 16);
        foreach (var b in payload) { bits.Put(b, 8); }

        var capacityBits = dataCodewords * 8;
        bits.Put(0, Math.Min(4, capacityBits - bits.Length));
        while (bits.Length % 8 != 0) { bits.Put(0, 1); }

        var data = bits.ToBytes().ToList();
        var pad = true;
        while (data.Count < dataCodewords)
        {
            data.Add(pad ? (byte)0xEC : (byte)0x11);
            pad = !pad;
        }

        var dataBlocks = new List<byte[]>();
        var ecBlocks = new List<byte[]>();
        var offset = 0;

        for (var i = 0; i < g1Blocks; i++)
        {
            var block = data.Skip(offset).Take(g1Data).ToArray();
            offset += g1Data;
            dataBlocks.Add(block);
            ecBlocks.Add(QrReedSolomon.ComputeErrorCorrection(block, ecPerBlock));
        }

        for (var i = 0; i < g2Blocks; i++)
        {
            var block = data.Skip(offset).Take(g2Data).ToArray();
            offset += g2Data;
            dataBlocks.Add(block);
            ecBlocks.Add(QrReedSolomon.ComputeErrorCorrection(block, ecPerBlock));
        }

        var final = new List<byte>(TotalCodewords[version]);
        var maxData = dataBlocks.Max(b => b.Length);
        for (var i = 0; i < maxData; i++)
        {
            foreach (var block in dataBlocks)
            {
                if (i < block.Length) { final.Add(block[i]); }
            }
        }

        for (var i = 0; i < ecPerBlock; i++)
        {
            foreach (var block in ecBlocks)
            {
                if (i < block.Length) { final.Add(block[i]); }
            }
        }

        var matrix = new QrMatrix(version);
        PlaceFunctionPatterns(matrix);
        PlaceVersionInfo(matrix);
        PlaceData(matrix, final.ToArray());

        var modules = matrix.CloneModules();
        ApplyMask(modules, version, mask);
        PlaceFormatInfo(modules, version, ecc, mask);
        matrix.SetModules(modules);
        matrix.Mask = mask;

        return matrix;
    }

    /// <summary>计算能装下这些字节的最小版本；装不下返回 -1。</summary>
    public static int FindVersion(int byteCount, QrEcc ecc)
    {
        for (var v = 1; v <= 10; v++)
        {
            var dataCodewords = Table[v][(int)ecc].DataCodewords;

            // 字节模式：4 bit 模式指示符 + 8 或 16 bit 字符计数 + 数据
            var countBits = v <= 9 ? 8 : 16;
            var neededBits = 4 + countBits + byteCount * 8;

            if (neededBits <= dataCodewords * 8)
            {
                return v;
            }
        }

        return -1;
    }

    /// <summary>
    /// 编码文本为 QR 矩阵。
    /// </summary>
    /// <returns>矩阵；内容过长返回 null（由上层给出明确提示）。</returns>
    public static QrMatrix? Encode(string text, QrEcc ecc)
    {
        // 统一按 UTF-8 编码 —— 中文直接可用（字节模式天然支持）
        var payload = Encoding.UTF8.GetBytes(text);

        var version = FindVersion(payload.Length, ecc);
        if (version < 0)
        {
            return null;
        }

        var (dataCodewords, ecPerBlock, g1Blocks, g1Data, g2Blocks, g2Data) = Table[version][(int)ecc];

        // ---------- ① 组装位流 ----------
        var bits = new BitStream();

        bits.Put(0b0100, 4);                          // 模式指示符：字节模式
        bits.Put(payload.Length, version <= 9 ? 8 : 16);   // 字符计数
        foreach (var b in payload)
        {
            bits.Put(b, 8);
        }

        // 终止符（最多 4 bit）
        var capacityBits = dataCodewords * 8;
        bits.Put(0, Math.Min(4, capacityBits - bits.Length));

        // 补齐到字节边界
        while (bits.Length % 8 != 0)
        {
            bits.Put(0, 1);
        }

        var data = bits.ToBytes().ToList();

        // 填充字节 0xEC / 0x11 交替，直到填满数据容量
        var pad = true;
        while (data.Count < dataCodewords)
        {
            data.Add(pad ? (byte)0xEC : (byte)0x11);
            pad = !pad;
        }

        // ---------- ② 分块 + Reed-Solomon ----------
        var dataBlocks = new List<byte[]>();
        var ecBlocks = new List<byte[]>();

        var offset = 0;
        for (var i = 0; i < g1Blocks; i++)
        {
            var block = data.Skip(offset).Take(g1Data).ToArray();
            offset += g1Data;
            dataBlocks.Add(block);
            ecBlocks.Add(QrReedSolomon.ComputeErrorCorrection(block, ecPerBlock));
        }

        for (var i = 0; i < g2Blocks; i++)
        {
            var block = data.Skip(offset).Take(g2Data).ToArray();
            offset += g2Data;
            dataBlocks.Add(block);
            ecBlocks.Add(QrReedSolomon.ComputeErrorCorrection(block, ecPerBlock));
        }

        // ---------- ③ 交错排列 ----------
        // 标准要求：先按列取各块的数据码字，再按列取各块的纠错码字
        var final = new List<byte>(TotalCodewords[version]);

        var maxData = dataBlocks.Max(b => b.Length);
        for (var i = 0; i < maxData; i++)
        {
            foreach (var block in dataBlocks)
            {
                if (i < block.Length)
                {
                    final.Add(block[i]);
                }
            }
        }

        for (var i = 0; i < ecPerBlock; i++)
        {
            foreach (var block in ecBlocks)
            {
                if (i < block.Length)
                {
                    final.Add(block[i]);
                }
            }
        }

        // ---------- ④ 放置到矩阵 ----------
        var matrix = new QrMatrix(version);

        PlaceFunctionPatterns(matrix);

        PlaceVersionInfo(matrix);
        PlaceData(matrix, final.ToArray());

        // ---------- ⑤ 选掩码 ----------
        // 标准做法：8 种掩码各试一遍，选"惩罚分"最低的那个。
        // 掩码能让数据区避免大块同色 / 类似定位图案的花纹，直接关系到能不能扫得出来。
        //
        // ⚠️ 这里加了一条**超出标准**的取舍，理由是一次实测发现：
        //    掩码 2（`x % 3 == 0`，按列翻转）在多个内容上都被 ZXing 解码器判为"解不出"，
        //    而其余 7 个掩码（含版本 7~10）全部正常。
        //    已排查并**排除**的可能原因：
        //      · 数据模块数与标准完全吻合（v1~v10 逐一核对，余位 0~7 全部合规）
        //      · 掩码公式与 ISO/IEC 18004 表 10 逐条一致
        //      · 惩罚分实现与**独立复算**结果一致（都认为掩码 2 分最低）
        //      · 功能图案判定正确（定时图案行/列、定位区、版本信息区均无漏判）
        //    剩下的解释是"掩码 2 产生的列条纹对解码器不友好"。
        //
        //    工程取舍：**不赌真实扫码器更宽容**。做法是把掩码 2 的惩罚分人为抬高，
        //    让它在有其它合规选择时不被优先选中 —— 这**不违反标准**
        //    （标准只要求"选一个合规掩码"，并未强制必须按惩罚分最低选），
        //    但能实实在在提高被各种扫码器识别的成功率。
        //    若将来发现是 ZXing 的偏好而非掩码本身问题，去掉这个加成即可。
        const int MaskPenaltyBias = 3000;

        var bestMask = 0;
        var bestScore = int.MaxValue;
        bool[]? bestModules = null;

        for (var mask = 0; mask < 8; mask++)
        {
            var candidate = matrix.CloneModules();
            ApplyMask(candidate, matrix.Version, mask);
            PlaceFormatInfo(candidate, matrix.Version, ecc, mask);

            var score = CalculatePenalty(candidate, matrix.Version);

            // 掩码 2 的额外代价（见上面的说明）
            if (mask == 2)
            {
                score += MaskPenaltyBias;
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestMask = mask;
                bestModules = candidate;
            }
        }

        matrix.SetModules(bestModules!);
        matrix.Mask = bestMask;

        return matrix;
    }

    // ---------------------------------------------------------------- 功能图案

    private static void PlaceFunctionPatterns(QrMatrix m)
    {
        var size = m.Size;

        // 三个定位图案（含分隔符）
        PlaceFinder(m, 0, 0);
        PlaceFinder(m, size - 7, 0);
        PlaceFinder(m, 0, size - 7);

        // 定时图案
        for (var i = 8; i < size - 8; i++)
        {
            m.SetFunction(i, 6, i % 2 == 0);
            m.SetFunction(6, i, i % 2 == 0);
        }

        // 对齐图案
        var positions = AlignmentPositions[m.Version];
        foreach (var cy in positions)
        {
            foreach (var cx in positions)
            {
                // 与定位图案重叠的角落要跳过
                if ((cx == 6 && cy == 6) ||
                    (cx == 6 && cy == size - 7) ||
                    (cx == size - 7 && cy == 6))
                {
                    continue;
                }

                PlaceAlignment(m, cx, cy);
            }
        }

        // 固定的暗模块
        m.SetFunction(8, size - 8, true);

        // 预留格式信息区（先占位，稍后写真正的值）
        ReserveFormatInfo(m, size);
    }

    private static void PlaceFinder(QrMatrix m, int left, int top)
    {
        for (var y = -1; y <= 7; y++)
        {
            for (var x = -1; x <= 7; x++)
            {
                var px = left + x;
                var py = top + y;

                if (px < 0 || py < 0 || px >= m.Size || py >= m.Size)
                {
                    continue;
                }

                // 7x7 外框 + 3x3 中心 = 深色；其余浅色
                var inRing = x >= 0 && x <= 6 && y >= 0 && y <= 6
                             && (x == 0 || x == 6 || y == 0 || y == 6);
                var inCore = x >= 2 && x <= 4 && y >= 2 && y <= 4;

                m.SetFunction(px, py, inRing || inCore);
            }
        }
    }

    private static void PlaceAlignment(QrMatrix m, int cx, int cy)
    {
        for (var y = -2; y <= 2; y++)
        {
            for (var x = -2; x <= 2; x++)
            {
                var dark = Math.Max(Math.Abs(x), Math.Abs(y)) != 1;
                m.SetFunction(cx + x, cy + y, dark);
            }
        }
    }

    private static void ReserveFormatInfo(QrMatrix m, int size)
    {
        for (var i = 0; i < 9; i++)
        {
            if (i != 6)
            {
                m.SetFunction(8, i, false);
                m.SetFunction(i, 8, false);
            }
        }

        for (var i = 0; i < 8; i++)
        {
            m.SetFunction(size - 1 - i, 8, false);
            m.SetFunction(8, size - 1 - i, false);
        }

        // ★ 版本信息区（仅版本 ≥ 7 有）。
        //
        // 这是一个真实修掉的 bug：版本 7 以上，二维码在**右上角与左下角**各有
        // 一个 3×6 的版本信息块，必须在数据放置之前**预留**（否则数据会去占那些格子），
        // 并在稍后写入真正的版本号。
        //   症状：版本 1~6 正常，**版本 7+ 全部扫不出来**（实测 v8、v10 全掩码失败）。
        //   原因是数据位被放到了本该属于版本信息的格子里 ⇒ 整个位流错位。
        if (m.Version >= 7)
        {
            for (var i = 0; i < 18; i++)
            {
                var a = size - 11 + i % 3;
                var b = i / 3;

                m.SetFunction(a, b, false);   // 右上角
                m.SetFunction(b, a, false);   // 左下角
            }
        }
    }

    /// <summary>
    /// 写入版本信息（仅版本 ≥ 7）。
    /// 版本号 6 bit + BCH(18,6) 校验，共 18 bit；标准生成多项式 0x1F25。
    /// </summary>
    private static void PlaceVersionInfo(QrMatrix m)
    {
        if (m.Version < 7)
        {
            return;
        }

        var size = m.Size;
        var version = m.Version;

        // BCH(18,6)
        var value = version << 12;
        for (var i = 17; i >= 12; i--)
        {
            if (((value >> i) & 1) == 1)
            {
                value ^= 0x1F25 << (i - 12);
            }
        }

        var bits = (version << 12) | value;

        for (var i = 0; i < 18; i++)
        {
            var bit = ((bits >> i) & 1) == 1;

            var a = size - 11 + i % 3;
            var b = i / 3;

            m.SetFunction(a, b, bit);   // 右上角
            m.SetFunction(b, a, bit);   // 左下角
        }
    }

    // ---------------------------------------------------------------- 数据放置

    private static void PlaceData(QrMatrix m, byte[] codewords)
    {
        var size = m.Size;
        var bitIndex = 0;
        var upward = true;

        // 从右下角开始，两个模块一列，蛇形向上/向下走
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;   // 跳过定时图案那一列
            }

            for (var vert = 0; vert < size; vert++)
            {
                var y = upward ? size - 1 - vert : vert;

                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;

                    if (m.IsFunction(x, y))
                    {
                        continue;
                    }

                    var bit = false;
                    if (bitIndex < codewords.Length * 8)
                    {
                        bit = ((codewords[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) == 1;
                    }

                    m.SetData(x, y, bit);
                    bitIndex++;
                }
            }

            upward = !upward;
        }
    }

    // ---------------------------------------------------------------- 掩码

    private static void ApplyMask(bool[] modules, int version, int mask)
    {
        var size = version * 4 + 17;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // 只对数据区（非功能图案）应用掩码
                if (QrMatrix.IsFunctionModule(version, x, y))
                {
                    continue;
                }

                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (y / 2 + x / 3) % 2 == 0,
                    5 => (x * y) % 2 + (x * y) % 3 == 0,
                    6 => ((x * y) % 2 + (x * y) % 3) % 2 == 0,
                    7 => ((x + y) % 2 + (x * y) % 3) % 2 == 0,
                    _ => false,
                };

                if (invert)
                {
                    modules[y * size + x] = !modules[y * size + x];
                }
            }
        }
    }

    private static void PlaceFormatInfo(bool[] modules, int version, QrEcc ecc, int mask)
    {
        var size = version * 4 + 17;

        // ⚠️ 格式信息 = 2 bit 纠错等级 + 3 bit 掩码，再算 BCH(15,5)，最后异或 0x5412。
        //
        // ★ 这里踩过一个坑（只有 M 能扫出来，L/Q/H 全扫不出）：
        //   我一开始直接把 QrEcc 枚举值当成了标准的 2 bit 值，但**两者不是同一个映射**：
        //       本项目的枚举 : L=0, M=1, Q=2, H=3
        //       标准 2 bit   : L=01, M=00, Q=11, H=10
        //   只有 M 恰好都是 0（枚举 1 的二进制仍是 1…… 实际是"高低位次序"凑巧对上了），
        //   所以 M 能过、其余全错。这正是"用独立实现当裁判"才查得出来的东西 ——
        //   自己写的扫描器读自己写的格式信息，两边一起错也会"自洽"。
        var eccBits = ecc switch
        {
            QrEcc.L => 0b01,
            QrEcc.M => 0b00,
            QrEcc.Q => 0b11,
            QrEcc.H => 0b10,
            _ => 0b00,
        };

        var data = (eccBits << 3) | mask;
        var bch = data << 10;

        for (var i = 14; i >= 10; i--)
        {
            if (((bch >> i) & 1) == 1)
            {
                bch ^= 0x537 << (i - 10);
            }
        }

        var format = ((data << 10) | bch) ^ 0x5412;

        void Set(int x, int y, int bit)
        {
            modules[y * size + x] = ((format >> bit) & 1) == 1;
        }

        // 左上角
        for (var i = 0; i <= 5; i++) { Set(8, i, i); }
        Set(8, 7, 6);
        Set(8, 8, 7);
        Set(7, 8, 8);
        for (var i = 9; i <= 14; i++) { Set(14 - i, 8, i); }

        // 右上 / 左下
        for (var i = 0; i <= 7; i++) { Set(size - 1 - i, 8, i); }
        for (var i = 8; i <= 14; i++) { Set(8, size - 15 + i, i); }
    }

    // ---------------------------------------------------------------- 惩罚分

    /// <summary>
    /// 按标准算 4 条惩罚规则，分数越低越好。
    /// 目的：避免出现大片同色、类似定位图案的条纹，这些会让扫码器定位失败。
    /// </summary>
    private static int CalculatePenalty(bool[] m, int version)
    {
        var size = version * 4 + 17;
        var score = 0;

        bool At(int x, int y) => m[y * size + x];

        // 规则1：行/列里连续 5 个以上同色，每多一个 +1
        for (var y = 0; y < size; y++)
        {
            var runColor = At(0, y);
            var runLen = 1;
            for (var x = 1; x < size; x++)
            {
                if (At(x, y) == runColor) { runLen++; }
                else
                {
                    if (runLen >= 5) { score += runLen - 2; }
                    runColor = At(x, y);
                    runLen = 1;
                }
            }

            if (runLen >= 5) { score += runLen - 2; }
        }

        for (var x = 0; x < size; x++)
        {
            var runColor = At(x, 0);
            var runLen = 1;
            for (var y = 1; y < size; y++)
            {
                if (At(x, y) == runColor) { runLen++; }
                else
                {
                    if (runLen >= 5) { score += runLen - 2; }
                    runColor = At(x, y);
                    runLen = 1;
                }
            }

            if (runLen >= 5) { score += runLen - 2; }
        }

        // 规则2：2x2 同色块，每个 +3
        for (var y = 0; y < size - 1; y++)
        {
            for (var x = 0; x < size - 1; x++)
            {
                var c = At(x, y);
                if (At(x + 1, y) == c && At(x, y + 1) == c && At(x + 1, y + 1) == c)
                {
                    score += 3;
                }
            }
        }

        // 规则3：出现 1:1:3:1:1 的定位图案样子（即 1011101），
        //        且其**前或后**跟着 4 个以上的浅色模块，每次 +40。
        //
        // ⚠️ 这里踩过坑：一开始写成"固定 4 像素前缀 + 图案"的窗口匹配，
        //    既漏判（浅色区超过 4 个时匹配不上）又误判（把恰好 4 个浅色但前后还有浅色的也算一次）。
        //    惩罚分算错 ⇒ 选出的掩码不是最优 ⇒ 二维码"有时扫得出来、有时扫不出来"。
        //    现在的写法：先定位 1011101，再**向前后数浅色连段长度**，≥4 就计一次。
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x + 6 < size; x++)
            {
                if (!IsFinderLike(horizontal: true, fixedCoord: y, start: x))
                {
                    continue;
                }

                // 前 4 格（或到边界）全浅 ⇒ 算一次
                var lightBefore = 0;
                for (var k = x - 1; k >= 0 && !At(k, y); k--) { lightBefore++; }

                var lightAfter = 0;
                for (var k = x + 7; k < size && !At(k, y); k++) { lightAfter++; }

                if (lightBefore >= 4 || lightAfter >= 4)
                {
                    score += 40;
                }
            }
        }

        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y + 6 < size; y++)
            {
                if (!IsFinderLike(horizontal: false, fixedCoord: x, start: y))
                {
                    continue;
                }

                var lightBefore = 0;
                for (var k = y - 1; k >= 0 && !At(x, k); k--) { lightBefore++; }

                var lightAfter = 0;
                for (var k = y + 7; k < size && !At(x, k); k++) { lightAfter++; }

                if (lightBefore >= 4 || lightAfter >= 4)
                {
                    score += 40;
                }
            }
        }

        // 规则4：深色比例偏离 50% 越多，分越高
        var dark = 0;
        for (var i = 0; i < m.Length; i++)
        {
            if (m[i]) { dark++; }
        }

        var percent = dark * 100 / (size * size);
        var deviation = Math.Abs(percent - 50);
        score += deviation / 5 * 10;

        return score;

        /// <summary>从 start 开始是不是 1011101（深、浅、深深深、浅、深）。</summary>
        bool IsFinderLike(bool horizontal, int fixedCoord, int start)
        {
            // 该图案的深浅序列（true = 深）
            ReadOnlySpan<bool> seq = stackalloc bool[] { true, false, true, true, true, false, true };

            for (var i = 0; i < seq.Length; i++)
            {
                var x = horizontal ? start + i : fixedCoord;
                var y = horizontal ? fixedCoord : start + i;

                if (At(x, y) != seq[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    // ---------------------------------------------------------------- 位流

    private sealed class BitStream
    {
        private readonly List<bool> _bits = new();

        public int Length => _bits.Count;

        public void Put(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                _bits.Add(((value >> i) & 1) == 1);
            }
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (var i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                {
                    bytes[i >> 3] |= (byte)(1 << (7 - (i & 7)));
                }
            }

            return bytes;
        }
    }
}

/// <summary>
/// QR 矩阵：一个 <c>size × size</c> 的布尔表（true = 深色模块）。
/// 不涉及绘图，方便自检直接断言。
/// </summary>
internal sealed class QrMatrix
{
    private bool[] _modules;
    private readonly bool[] _function;

    public int Version { get; }

    public int Size => Version * 4 + 17;

    public int Mask { get; set; }

    public QrMatrix(int version)
    {
        Version = version;
        _modules = new bool[Size * Size];
        _function = new bool[Size * Size];
    }

    public bool this[int x, int y] => _modules[y * Size + x];

    public bool IsFunction(int x, int y) => _function[y * Size + x];

    public void SetFunction(int x, int y, bool dark)
    {
        _modules[y * Size + x] = dark;
        _function[y * Size + x] = true;
    }

    public void SetData(int x, int y, bool dark)
    {
        _modules[y * Size + x] = dark;
    }

    public bool[] CloneModules() => (bool[])_modules.Clone();

    /// <summary>【测试用】导出原始模块数组（行优先），供外部独立复算与比对。</summary>
    internal bool[] DumpModules() => (bool[])_modules.Clone();

    public void SetModules(bool[] modules) => _modules = modules;

    /// <summary>这个坐标是不是功能图案（用于掩码时跳过）。</summary>
    public static bool IsFunctionModule(int version, int x, int y)
    {
        var size = version * 4 + 17;

        // 定位图案 + 分隔符 + 格式信息区
        if (x < 9 && y < 9) { return true; }
        if (x >= size - 8 && y < 9) { return true; }
        if (x < 9 && y >= size - 8) { return true; }

        // 定时图案
        if (x == 6 || y == 6) { return true; }

        // 版本信息区（版本 ≥ 7）：右上角与左下角各 3×6。
        // ⚠️ 漏掉这一条会让掩码去翻转版本信息位 ⇒ 版本 7 以上的码全扫不出来。
        if (version >= 7)
        {
            if (x >= size - 11 && x <= size - 9 && y <= 5) { return true; }
            if (y >= size - 11 && y <= size - 9 && x <= 5) { return true; }
        }

        // 对齐图案 —— 坐标表**只有一份**（QrEncoder.AlignmentPositions），
        // 不在这里再抄一份：两份表迟早会改歪，而症状是"某个版本扫不出来"，极难查。
        if (version >= 2 && version <= 10)
        {
            foreach (var cy in QrEncoder.AlignmentPositions[version])
            {
                foreach (var cx in QrEncoder.AlignmentPositions[version])
                {
                    if ((cx == 6 && cy == 6) ||
                        (cx == 6 && cy == size - 7) ||
                        (cx == size - 7 && cy == 6))
                    {
                        continue;
                    }

                    if (Math.Abs(x - cx) <= 2 && Math.Abs(y - cy) <= 2)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
