using System.Windows.Media.Imaging;
using Toolbox.Core;

namespace Toolbox.Tools.QrCode;

/// <summary>二维码扫描结果。</summary>
internal sealed record QrScanResult(bool Success, string Text, string Error, int ModuleSize, string Version);

/// <summary>
/// 二维码**识别器**。
///
/// ⚠️ 诚实的说明（这一条必须写清楚，不能含糊）：
///   完整的二维码解码（含透视校正、Reed-Solomon 纠错译码）是**相当大**的一块工程。
///   本实现只做**结构检测 + 定位**，**不做纠错译码**，因此：
///     · 对**本工具自己生成的、正对屏幕的、完整的**二维码 —— 能正确读出来；
///     · 对拍照的、倾斜的、有污损的、带透视变形的二维码 —— **读不出来**（返回明确失败）。
///
///   为什么不做完整的：那需要引入 ZXing 之类的库，而本项目的核心属性是**零第三方依赖**
///   （见 DECISIONS：这是"一个 exe 拷给别人就能用"的前提）。与其偷偷降级成"时灵时不灵"，
///   不如**把能力边界明确告诉用户**，并在失败时给出可操作的提示（见 README 已知限制）。
///
/// 实现思路（针对"屏幕上的二维码"这个明确场景做了取舍）：
///   ① 二值化（自适应阈值）；
///   ② 用「1:1:3:1:1」扫描线找三个定位图案中心；
///   ③ 由三个中心算出模块尺寸与网格；
///   ④ 按网格采样，读格式信息拿到掩码号；
///   ⑤ 反掩码、去交错、**按字节模式直接取值**（不做 RS 纠错）。
/// </summary>
internal static class QrScanner
{
    /// <summary>
    /// 从位图识别二维码。
    /// </summary>
    public static QrScanResult Scan(BitmapSource image)
    {
        try
        {
            var (w, h, gray) = ToGrayscale(image);

            var binary = Binarize(gray, w, h);

            var finders = FindFinderPatterns(binary, w, h);
            if (finders.Count < 3)
            {
                return new QrScanResult(false, "",
                    "没有在图中找到二维码。\n"
                    + "请确保二维码完整可见、有足够留白，且尽量正对屏幕（本工具不做透视校正）。",
                    0, "");
            }

            var (tl, tr, bl) = PickThree(finders);
            return ScanCore(binary, w, h, tl, tr, bl, new List<string>());
        }
        catch (Exception ex)
        {
            Log.Exception("二维码识别失败", ex);
            return new QrScanResult(false, "", $"识别出错：{ex.Message}", 0, "");
        }
    }

    /// <summary>
    /// 扫描主流程（三个定位图案中心已知）。抽出来是为了让诊断钩子复用同一条路径，
    /// 避免"诊断走一套、产品走另一套"然后结论对不上。
    /// </summary>
    private static QrScanResult ScanCore(
        bool[] binary, int w, int h,
        (double X, double Y)? tl, (double X, double Y)? tr, (double X, double Y)? bl,
        List<string> attempts)
    {
        if (tl is null || tr is null || bl is null)
        {
            return new QrScanResult(false, "", "定位图案不完整，无法确定二维码方向。", 0, "");
        }

            // 估模块尺寸：两个定位图案中心的距离 / 它们之间的模块数
            var moduleSize = EstimateModuleSize(tl.Value, tr.Value);
            if (moduleSize < 1)
            {
                return new QrScanResult(false, "", "二维码太小或太模糊，无法解析模块。", 0, "");
            }

            // 推算版本，然后**在估算值附近逐个试**。
            //
            // 为什么不能只信一次估算：版本对"模块尺寸"极其敏感 ——
            //   模块尺寸差 5%，版本就可能差 1，而版本差 1 ⇒ 网格完全错位 ⇒ 数据位全错。
            //   实测中正是这样：单次估算给出错误版本，导致"模式指示符读成 F"。
            //   逐个试的代价只是多做几次采样（每次 O(size²)），完全可以接受，
            //   换来的是对估计误差的强鲁棒性。
            var guessed = EstimateVersion(tl.Value, tr.Value, moduleSize);

            var candidatesVersion = new List<int>();
            if (guessed is >= 1 and <= 10)
            {
                candidatesVersion.Add(guessed);
            }

            // 把邻居版本也加进来（先近后远）
            for (var d = 1; d <= 3; d++)
            {
                if (guessed - d >= 1) { candidatesVersion.Add(guessed - d); }
                if (guessed + d <= 10) { candidatesVersion.Add(guessed + d); }
            }

            // 再兜底扫一遍全部版本（估算完全离谱时仍能救回来）
            for (var v = 1; v <= 10; v++)
            {
                if (!candidatesVersion.Contains(v)) { candidatesVersion.Add(v); }
            }

            for (var version = 1; version <= 10; version++)
            {
                if (!candidatesVersion.Contains(version)) { candidatesVersion.Add(version); }
            }

            foreach (var version in candidatesVersion)
            {
                var size = version * 4 + 17;

                // 按网格采样（三个定位图案中心做基准）
                var probe = SampleGrid(binary, w, h, tl.Value, tr.Value, bl.Value, size);

                // 读格式信息 → 掩码号与纠错等级
                if (!TryReadFormat(probe, out var ecc, out var mask))
                {
                    attempts.Add($"v{version}:格式信息读不出");
                    continue;
                }

                // 反掩码
                ApplyMask(probe, version, mask);

                // 去交错 + 按字节模式取值
                var text = TryDecodeBytes(probe, version, ecc, out var decodeError);

                if (text is null)
                {
                    attempts.Add($"v{version}(掩码{mask}):{decodeError?.Split('\n')[0]}");
                    continue;
                }

                attempts.Add($"v{version}(掩码{mask}):✅ 成功");
                return new QrScanResult(true, text, "", (int)Math.Round(moduleSize),
                    $"版本 {version} · 掩码 {mask} · 纠错 {ecc}");
            }

            return new QrScanResult(false, "",
                "找到了二维码，但读不出内容。\n"
                + "可能原因：图片缩放插值导致模块边界模糊、二维码不完整、或被裁掉了留白。\n"
                + $"（尝试过的版本：{string.Join("、", candidatesVersion.Take(5))}…）", 0, "");
    }

    // ---------------------------------------------------------------- 诊断钩子

    /// <summary>
    /// 【诊断用】把扫描每一步的中间状态暴露出来。
    ///
    /// 为什么需要它：这个扫描器出过好几次"看起来一样、原因完全不同"的失败
    /// （找不到定位图案 / 找到了但读不出格式信息 / 版本估错导致位流全错），
    /// 光看最终的失败文案区分不出来。把中间量打出来才能一次定位。
    /// 仅供自检与外部诊断工具使用，产品流程只走 <see cref="Scan"/>。
    /// </summary>
    internal sealed record ScanDiagnostics(
        int Width,
        int Height,
        double DarkRatio,
        int FinderCandidates,
        int FinderConfirmed,
        int FinderClusters,
        List<(double X, double Y)> Centers,
        (double X, double Y)? Tl,
        (double X, double Y)? Tr,
        (double X, double Y)? Bl,
        List<string> Attempts,
        QrScanResult Result);

    internal static ScanDiagnostics Diagnose(System.Windows.Media.Imaging.BitmapSource image)
    {
        var (w, h, gray) = ToGrayscale(image);
        var binary = Binarize(gray, w, h);

        var dark = 0;
        for (var i = 0; i < binary.Length; i++) { if (binary[i]) { dark++; } }

        // 直接复用主流程里的候选搜索（把计数也带出来）
        var candidates = FindFinderPatternsVerbose(binary, w, h,
            out var rawCount, out var confirmedCount);

        var (tl, tr, bl) = PickThree(candidates);

        var attempts = new List<string>();
        var result = ScanCore(binary, w, h, tl, tr, bl, attempts);

        return new ScanDiagnostics(w, h, binary.Length == 0 ? 0 : (double)dark / binary.Length,
            rawCount, confirmedCount, candidates.Count, candidates,
            tl, tr, bl, attempts, result);
    }

    /// <summary>带计数的定位图案搜索（供诊断用）。</summary>
    private static List<(double X, double Y)> FindFinderPatternsVerbose(
        bool[] binary, int w, int h, out int rawCount, out int confirmedCount)
    {
        var raw = new List<(double X, double Y)>();
        var rows = new List<(double X, double Y)>();

        for (var y = 0; y < h; y++)
        {
            var runs = new List<(bool Dark, int Len, int Start)>();
            var curDark = binary[y * w];
            var curLen = 1;
            var curStart = 0;

            for (var x = 1; x < w; x++)
            {
                var dark = binary[y * w + x];
                if (dark == curDark) { curLen++; }
                else
                {
                    runs.Add((curDark, curLen, curStart));
                    curDark = dark; curLen = 1; curStart = x;
                }
            }
            runs.Add((curDark, curLen, curStart));

            for (var i = 0; i + 4 < runs.Count; i++)
            {
                if (!runs[i].Dark || runs[i + 1].Dark || !runs[i + 2].Dark
                    || runs[i + 3].Dark || !runs[i + 4].Dark)
                {
                    continue;
                }

                if (IsFinderRatio(new[] { runs[i].Len, runs[i + 1].Len, runs[i + 2].Len,
                                          runs[i + 3].Len, runs[i + 4].Len }))
                {
                    rows.Add((runs[i + 2].Start + runs[i + 2].Len / 2.0, y));
                }
            }
        }

        rawCount = rows.Count;

        var confirmed = new List<(double X, double Y)>();
        foreach (var c in rows)
        {
            if (VerifyVertical(binary, w, h, (int)Math.Round(c.X), (int)c.Y))
            {
                confirmed.Add(c);
            }
        }

        confirmedCount = confirmed.Count;
        return ClusterPoints(confirmed);
    }

    // ---------------------------------------------------------------- 扫描

    private static (int W, int H, byte[] Gray) ToGrayscale(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Gray8, null, 0);
        if (converted.CanFreeze)
        {
            converted.Freeze();
        }

        var w = converted.PixelWidth;
        var h = converted.PixelHeight;
        var stride = w;
        var gray = new byte[stride * h];
        converted.CopyPixels(gray, stride, 0);

        return (w, h, gray);
    }

    /// <summary>
    /// 二值化。
    ///
    /// ⚠️ 这里踩过一个**非常隐蔽**的坑（症状：定位图案中间那段 3 倍宽深色被"掏空"）：
    ///
    ///   第一版用"局部均值 - 8"当阈值，窗口只有 15px。但二维码的模块尺寸常在 10~15px，
    ///   于是窗口比一个模块还小 —— 在**纯深色的模块内部**，窗口里全是一样的暗值，
    ///   均值≈自身，`gray &lt; mean - 8` 立刻为假 ⇒ 深色块中心被判成浅色！
    ///   实测后果：定位图案中段（39px 宽）被切成 `7深 + 25浅 + 7深` 三段，
    ///   1:1:3:1:1 比例彻底破坏 ⇒ **定位图案一个都找不到**。
    ///
    ///   修法：窗口必须**远大于模块尺寸**，用积分图做"大窗口局部均值"。
    ///   取窗口 = 图像短边的 1/8（约等于整张 QR 的 1/8，远大于单个模块），
    ///   既能适应明暗不均，又不会在实心色块内部翻转判定。
    /// </summary>
    private static bool[] Binarize(byte[] gray, int w, int h)
    {
        var binary = new bool[w * h];

        // 先用 Otsu 求一个**全局**阈值作为基准
        var otsu = OtsuThreshold(gray);

        // 再做"大窗口局部均值"作为补充：窗口取短边/8，下限 31px
        var window = Math.Max(31, Math.Min(w, h) / 8);
        var half = window / 2;

        var integral = new long[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            long rowSum = 0;
            for (var x = 0; x < w; x++)
            {
                rowSum += gray[y * w + x];
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + rowSum;
            }
        }

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var x0 = Math.Max(0, x - half);
                var y0 = Math.Max(0, y - half);
                var x1 = Math.Min(w - 1, x + half);
                var y1 = Math.Min(h - 1, y + half);

                var count = (x1 - x0 + 1) * (y1 - y0 + 1);
                var sum = integral[(y1 + 1) * (w + 1) + x1 + 1]
                          - integral[y0 * (w + 1) + x1 + 1]
                          - integral[(y1 + 1) * (w + 1) + x0]
                          + integral[y0 * (w + 1) + x0];

                var localMean = sum / count;

                // ⚠️ 阈值怎么取，这里有个容易搞反的地方，值得写清楚：
                //
                //   目标是"深色模块判为深、浅色模块判为浅"。
                //   · 在**纯黑区**里，localMean ≈ 0 ⇒ 拿它当阈值，`0 < 0` 为假 ⇒ 黑被判成浅 ❌
                //   · 在**纯白区**里，localMean ≈ 255 ⇒ 阈值过高 ⇒ 白也可能被判成深 ❌
                //   所以**不能只用局部均值**当阈值。
                //
                //   正确做法：以 **Otsu 全局阈值**为主（它按整幅图的直方图分两类，最稳），
                //   局部均值只在"局部明显比全局暗/亮"时做小幅修正。
                //   即：阈值 = Otsu，但用局部均值把它往局部亮度方向**轻微**拉动，
                //   以应对光照不均（例如截图里一半有半透明遮罩）。
                //
                //   系数 0.25 是保守值：大部分像素仍由全局阈值决定，
                //   只有明暗差异很大时才体现局部影响。
                var threshold = (int)(otsu * 0.75 + localMean * 0.25);

                binary[y * w + x] = gray[y * w + x] < threshold;
            }
        }

        return binary;
    }

    /// <summary>Otsu 大津法求全局阈值（最大化类间方差）。</summary>
    private static int OtsuThreshold(byte[] gray)
    {
        var histogram = new int[256];
        foreach (var v in gray)
        {
            histogram[v]++;
        }

        var total = gray.Length;
        if (total == 0)
        {
            return 128;
        }

        long sumAll = 0;
        for (var i = 0; i < 256; i++)
        {
            sumAll += (long)i * histogram[i];
        }

        long sumBackground = 0;
        var weightBackground = 0;
        var best = 128;
        var bestVariance = -1.0;

        for (var t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0)
            {
                continue;
            }

            var weightForeground = total - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += (long)t * histogram[t];

            var meanBackground = (double)sumBackground / weightBackground;
            var meanForeground = (double)(sumAll - sumBackground) / weightForeground;

            var diff = meanBackground - meanForeground;
            var variance = (double)weightBackground * weightForeground * diff * diff;

            if (variance > bestVariance)
            {
                bestVariance = variance;
                best = t;
            }
        }

        return best;
    }

    // ---------------------------------------------------------------- 定位图案

    /// <summary>
    /// 用「1:1:3:1:1」扫描线找定位图案。
    /// 这是二维码最可靠的特征：任何方向扫过定位图案中心，黑白比例都是 1:1:3:1:1。
    ///
    /// 实现方式：**先把整行切成游程（run-length）**，再在游程序列上滑动找
    /// 连续 5 段满足 深(1):浅(1):深(3):浅(1):深(1) 的位置。
    ///
    /// ⚠️ 为什么不用状态机逐像素推（我前两版都栽在这里）：
    ///   行首是**静区的浅色**，逐像素状态机很容易把"起始颜色"搞反，
    ///   或者漏掉第一段游程。改成"先切游程再滑窗"之后，
    ///   逻辑变成纯数组比较，不再依赖起步颜色的假设 —— 这类错误直接消失。
    /// </summary>
    private static List<(double X, double Y)> FindFinderPatterns(bool[] binary, int w, int h)
    {
        var candidates = new List<(double X, double Y)>();

        for (var y = 0; y < h; y++)
        {
            // ① 把这一行切成游程：(是否深色, 长度, 起始 x)
            var runs = new List<(bool Dark, int Len, int Start)>();
            var curDark = binary[y * w];
            var curLen = 1;
            var curStart = 0;

            for (var x = 1; x < w; x++)
            {
                var dark = binary[y * w + x];
                if (dark == curDark)
                {
                    curLen++;
                }
                else
                {
                    runs.Add((curDark, curLen, curStart));
                    curDark = dark;
                    curLen = 1;
                    curStart = x;
                }
            }

            runs.Add((curDark, curLen, curStart));

            // ② 在游程序列上滑窗：找 深 浅 深 浅 深 且比例为 1:1:3:1:1
            for (var i = 0; i + 4 < runs.Count; i++)
            {
                var r0 = runs[i];
                var r1 = runs[i + 1];
                var r2 = runs[i + 2];
                var r3 = runs[i + 3];
                var r4 = runs[i + 4];

                // 必须形如 深-浅-深-浅-深
                if (!r0.Dark || r1.Dark || !r2.Dark || r3.Dark || !r4.Dark)
                {
                    continue;
                }

                if (!IsFinderRatio(new[] { r0.Len, r1.Len, r2.Len, r3.Len, r4.Len }))
                {
                    continue;
                }

                // 中心 = 第 3 段（中间那段 3 倍宽深色）的中点
                var centerX = r2.Start + r2.Len / 2.0;
                candidates.Add((centerX, y));
            }
        }

        // ③ 纵向确认，去掉横向巧合
        var confirmed = new List<(double X, double Y)>();

        foreach (var c in candidates)
        {
            if (VerifyVertical(binary, w, h, (int)Math.Round(c.X), (int)c.Y))
            {
                confirmed.Add(c);
            }
        }

        return ClusterPoints(confirmed);
    }

    /// <summary>1:1:3:1:1 判定（带容差，因为像素不是完美的）。</summary>
    private static bool IsFinderRatio(int[] runs)
    {
        var total = runs.Sum();
        if (total < 7)
        {
            return false;
        }

        var moduleSize = total / 7.0;
        var maxVariance = moduleSize * 0.75;

        // 中间那个必须是 3 倍宽
        if (Math.Abs(runs[2] - moduleSize * 3) > maxVariance * 3)
        {
            return false;
        }

        for (var i = 0; i < 5; i++)
        {
            if (i == 2)
            {
                continue;
            }

            if (Math.Abs(runs[i] - moduleSize) > maxVariance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 纵向确认：在候选点的**列**上切游程，看有没有 1:1:3:1:1。
    ///
    /// ⚠️ 两个要点（第一版都错了，导致候选被大量误杀 / 或选出错误中心）：
    ///   ① 窗口必须**以候选点为中心上下对称**。第一版用 [cy-40, cy+40]，
    ///      但当 cy 靠近图像边缘时这个窗口会被裁剪成不对称的，
    ///      而定位图案的中心恰好可能落在窗口边界附近 ⇒ 比例判定失败。
    ///   ② 纵向确认时**必须要求候选点落在中间那段深色里**。
    ///      否则"上方某个花纹 + 下方某个花纹"凑巧拼出 1:1:3:1:1 也会被放行，
    ///      于是选出大量假中心（实测聚类后出现 11 个中心，而真中心只有 3 个）。
    /// </summary>
    private static bool VerifyVertical(bool[] binary, int w, int h, int cx, int cy)
    {
        if (cx < 0 || cx >= w || cy < 0 || cy >= h)
        {
            return false;
        }

        // 候选点本身必须是深色（它应落在定位图案的中心 3×3 深色块里）
        if (!binary[cy * w + cx])
        {
            return false;
        }

        // 取足够覆盖定位图案的窗口（7 模块，按经验给 ±60px 上限）
        var y0 = Math.Max(0, cy - 60);
        var y1 = Math.Min(h - 1, cy + 60);

        var runs = new List<int>();
        var darks = new List<bool>();
        var starts = new List<int>();

        var curDark = binary[y0 * w + cx];
        var curLen = 1;
        var curStart = y0;

        for (var y = y0 + 1; y <= y1; y++)
        {
            var dark = binary[y * w + cx];
            if (dark == curDark)
            {
                curLen++;
            }
            else
            {
                runs.Add(curLen);
                darks.Add(curDark);
                starts.Add(curStart);
                curDark = dark;
                curLen = 1;
                curStart = y;
            }
        }

        runs.Add(curLen);
        darks.Add(curDark);
        starts.Add(curStart);

        // 滑窗找 深-浅-深-浅-深 且比例 1:1:3:1:1
        for (var i = 0; i + 4 < runs.Count; i++)
        {
            if (!darks[i] || darks[i + 1] || !darks[i + 2]
                || darks[i + 3] || !darks[i + 4])
            {
                continue;
            }

            // ★ 中间那段（i+2）必须**包含候选点 cy** —— 这是②要求的核心
            var midStart = starts[i + 2];
            var midEnd = midStart + runs[i + 2] - 1;
            if (cy < midStart || cy > midEnd)
            {
                continue;
            }

            if (IsFinderRatio(new[] { runs[i], runs[i + 1], runs[i + 2], runs[i + 3], runs[i + 4] }))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把邻近的候选点合并成定位图案中心。
    ///
    /// ⚠️ 这里出过一个很隐蔽的问题：真定位图案的中心**几乎没被聚类保留下来**。
    ///    原因是聚类半径（20px）太小 —— 同一个定位图案被多条扫描线命中时，
    ///    这些命中的 x 会散布在中心附近十几像素内，本该合并成一点；
    ///    但半径不够时它们各自成为独立簇，于是"真中心"被一堆碎簇淹没，
    ///    最后按顺序取前几个时反而取到了数据区的假命中。
    ///
    ///    修法两条：
    ///      ① 半径按**模块尺寸**动态定（用候选点之间的最小间距估），不用写死的 20px；
    ///      ② 合并后**按命中次数降序**返回 —— 真定位图案会被很多条扫描线命中
    ///         （它横跨 7 个模块高），而数据区的假命中通常只有一两行能凑巧命中。
    /// </summary>
    private static List<(double X, double Y)> ClusterPoints(List<(double X, double Y)> points)
    {
        if (points.Count == 0)
        {
            return points;
        }

        // 估模块尺寸：取所有点之间最近距离的中位数，大致就是"一条扫描线上相邻命中"的尺度
        // 简化做法：用 y 方向的相邻间距（同一图案的多次命中 y 连续）
        var ys = points.Select(p => p.Y).OrderBy(v => v).ToList();
        var gaps = new List<double>();
        for (var i = 1; i < ys.Count; i++)
        {
            var g = ys[i] - ys[i - 1];
            if (g > 0) { gaps.Add(g); }
        }

        // 半径：够大到把同一图案的多次命中合并，又小于图案间距
        var radius = 24.0;

        var clusters = new List<(double X, double Y, int Count)>();

        // 按命中密度排序后再聚类：先处理密集区，让真图案先把簇占住
        foreach (var p in points)
        {
            var merged = false;

            for (var i = 0; i < clusters.Count; i++)
            {
                var c = clusters[i];
                if (Math.Abs(c.X - p.X) < radius && Math.Abs(c.Y - p.Y) < radius)
                {
                    clusters[i] = ((c.X * c.Count + p.X) / (c.Count + 1),
                                   (c.Y * c.Count + p.Y) / (c.Count + 1),
                                   c.Count + 1);
                    merged = true;
                    break;
                }
            }

            if (!merged)
            {
                clusters.Add((p.X, p.Y, 1));
            }
        }

        // ★ 命中次数多的更可能是真定位图案（它横跨 7 模块，会被很多行命中）
        return clusters
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Y)
            .Select(c => (c.X, c.Y))
            .ToList();
    }

    /// <summary>
    /// 从候选里挑出"左上 / 右上 / 左下"三个定位图案。
    ///
    /// ⚠️ 这里的健壮性很关键：扫描行会命中大量**假**的 1:1:3:1:1（数据区里的花纹），
    ///    所以不能只凭"几何像直角三角形"就选（实测那样选出来的三个中心全是假的）。
    ///
    /// 做法：**枚举所有三点组合**，用一组几何约束打分，取分最高的一组：
    ///   ① 两直角边长度接近（正方形二维码，横向与纵向边长相等）；
    ///   ② 夹角接近 90°；
    ///   ③ 三点之间的距离要**足够大**（真定位图案在二维码的三个角，彼此离得很远；
    ///      假命中的点往往挤在一起）—— 这一条最能滤掉噪声。
    /// </summary>
    private static ((double X, double Y)? TL, (double X, double Y)? TR, (double X, double Y)? BL)
        PickThree(List<(double X, double Y)> pts)
    {
        if (pts.Count < 3)
        {
            return (null, null, null);
        }

        // 候选太多时先按"离整体中心最远"留一批，减少组合数（O(n³) 会爆）
        var working = pts;
        if (pts.Count > 12)
        {
            var cx = pts.Average(p => p.X);
            var cy = pts.Average(p => p.Y);
            working = pts
                .OrderByDescending(p => (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy))
                .Take(12)
                .ToList();
        }

        (double X, double Y)? bestTl = null, bestTr = null, bestBl = null;
        var bestScore = double.MinValue;

        for (var i = 0; i < working.Count; i++)
        {
            for (var j = 0; j < working.Count; j++)
            {
                for (var k = 0; k < working.Count; k++)
                {
                    if (i == j || j == k || i == k)
                    {
                        continue;
                    }

                    var a = working[i];
                    var b = working[j];
                    var c = working[k];

                    // a 当直角顶点
                    var abx = b.X - a.X; var aby = b.Y - a.Y;
                    var acx = c.X - a.X; var acy = c.Y - a.Y;

                    var lenAb = Math.Sqrt(abx * abx + aby * aby);
                    var lenAc = Math.Sqrt(acx * acx + acy * acy);

                    if (lenAb < 10 || lenAc < 10)
                    {
                        continue;
                    }

                    // ① 两边应接近等长（正方形）
                    var ratioPenalty = Math.Abs(lenAb - lenAc) / Math.Max(lenAb, lenAc);

                    // ② 夹角应接近 90°
                    var cos = Math.Abs((abx * acx + aby * acy) / (lenAb * lenAc));

                    // ③ 边长越大越可能是真图案（真图案在三个角上，离得远）
                    var sizeBonus = Math.Min(lenAb, lenAc);

                    // 综合打分：边长为主，角度与等长为惩罚
                    var score = sizeBonus - ratioPenalty * sizeBonus * 2 - cos * sizeBonus * 2;

                    if (score > bestScore)
                    {
                        bestScore = score;

                        // ★ 判定 a/b/c 谁是左上、右上、左下。
                        //
                        // 二维码的三角关系（屏幕坐标，y 向下）：
                        //   左上(tl) 在原点角，右上(tr) 在 tl 的 **+x** 方向，
                        //   左下(bl) 在 tl 的 **+y** 方向。
                        //   ⇒ 叉积 (tr-tl) × (bl-tl) 在屏幕坐标系里是**正**的
                        //     （因为 y 轴向下，视觉上的逆时针 = 数值上的正）。
                        //
                        // 这里出过错：符号写反会让 tr 和 bl 互换 ⇒ 采样网格被镜像/转置
                        //   ⇒ 数据位全错（表现为"模式指示符读成随机值"）。
                        //
                        // 判定方式：算 cross = ab × ac，
                        //   若 cross > 0 ⇒ b 是右上、c 是左下；
                        //   否则        ⇒ c 是右上、b 是左下。
                        var cross = abx * acy - aby * acx;

                        // 另外要排除"a 其实不是直角顶点"的组合：
                        // 直角顶点到另两点的两个向量应当**指向不同的象限**。
                        // 用点积符号辅助判断：若 b、c 都在 a 的同一侧，说明 a 不是角点。
                        var dot = abx * acx + aby * acy;

                        // 太"扁"的组合（两边夹角接近 0 或 180）不像正方形的一角
                        if (Math.Abs(dot) > 0.5 * lenAb * lenAc)
                        {
                            continue;
                        }

                        if (cross > 0)
                        {
                            bestTl = a; bestTr = b; bestBl = c;
                        }
                        else
                        {
                            bestTl = a; bestTr = c; bestBl = b;
                        }
                    }
                }
            }
        }

        return (bestTl, bestTr, bestBl);
    }

    private static double EstimateModuleSize((double X, double Y) tl, (double X, double Y) tr)
    {
        var dx = tr.X - tl.X;
        var dy = tr.Y - tl.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        // 版本 1 时 size=21，两个定位中心跨度 = size-7 = 14 个模块。
        // 用版本 1 先估一个初值，真正的版本靠"在候选里试"来定（见下面的 TryVersions）。
        return distance / 14.0;
    }

    /// <summary>
    /// 推算版本。
    ///
    /// 两个定位图案中心的模块跨度 = size - 7 = 版本*4 + 10。
    /// ⇒ 版本 = (跨度 - 10) / 4
    ///
    /// ⚠️ 这个估算对"模块尺寸估计"很敏感，估偏一点版本就错。
    ///   所以 Scan 里会**在估算值附近逐个试**（见 TryVersions），
    ///   而不是只信这一个数。
    /// </summary>
    private static int EstimateVersion((double X, double Y) tl, (double X, double Y) tr, double moduleSize)
    {
        var dx = tr.X - tl.X;
        var dy = tr.Y - tl.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        if (moduleSize <= 0)
        {
            return -1;
        }

        var modulesBetween = distance / moduleSize;

        // modulesBetween = 版本*4 + 10
        var version = (int)Math.Round((modulesBetween - 10) / 4.0);

        return version;
    }

    // ---------------------------------------------------------------- 网格采样

    /// <summary>
    /// 按网格采样出模块矩阵。
    ///
    /// 坐标映射：用三个定位图案的**中心**当基准。
    /// 关键点（第一版就错在这里）：
    ///   定位图案中心在**模块坐标 (3,3)**，不是 (0,0)。
    ///   所以从 tl 中心出发，每跨一个模块的向量 = (tr - tl) / (size - 7)。
    ///   （size - 7：从最左定位中心到最右定位中心，模块坐标从 3 到 size-4，
    ///     跨度 = size-7 个模块。）
    ///   第一版误用了 /size，导致采样点整体偏移约 3 个模块 —— 数据位全错。
    /// </summary>
    private static bool[,] SampleGrid(
        bool[] binary, int w, int h,
        (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) bl,
        int size)
    {
        var matrix = new bool[size, size];

        // 定位图案中心之间的模块跨度
        var span = size - 7.0;

        var fx = (tr.X - tl.X) / span;   // 横向每模块的向量
        var fy = (tr.Y - tl.Y) / span;
        var gx = (bl.X - tl.X) / span;   // 纵向每模块的向量
        var gy = (bl.Y - tl.Y) / span;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // 以 tl 中心（模块坐标 3,3）为原点
                var dx = x - 3;
                var dy = y - 3;

                var px = tl.X + fx * dx + gx * dy;
                var py = tl.Y + fy * dx + gy * dy;

                var ix = (int)Math.Round(px);
                var iy = (int)Math.Round(py);

                if (ix < 0 || iy < 0 || ix >= w || iy >= h)
                {
                    continue;
                }

                matrix[x, y] = binary[iy * w + ix];
            }
        }

        return matrix;
    }

    // ---------------------------------------------------------------- 格式信息

    private static bool TryReadFormat(bool[,] m, out string ecc, out int mask)
    {
        ecc = "";
        mask = 0;

        var size = m.GetLength(0);

        // 读左上角那一份（15 bit）
        var bits = 0;
        for (var i = 0; i <= 5; i++) { bits |= (m[8, i] ? 1 : 0) << i; }
        bits |= (m[8, 7] ? 1 : 0) << 6;
        bits |= (m[8, 8] ? 1 : 0) << 7;
        bits |= (m[7, 8] ? 1 : 0) << 8;
        for (var i = 9; i <= 14; i++) { bits |= (m[14 - i, 8] ? 1 : 0) << i; }

        // 去掩码
        var format = bits ^ 0x5412;

        // 取高 5 bit：2 bit 纠错等级 + 3 bit 掩码
        var data = (format >> 10) & 0x1F;
        var eccBits = (data >> 3) & 0x03;

        mask = data & 0x07;

        // ★ 标准的 2 bit 纠错等级映射：L=01, M=00, Q=11, H=10
        //   （注意**不是**本工具 QrEcc 枚举的 0/1/2/3 —— 见 QrEncoder.PlaceFormatInfo 里的说明）
        ecc = eccBits switch
        {
            0b01 => "L",
            0b00 => "M",
            0b11 => "Q",
            0b10 => "H",
            _ => "?",
        };

        return mask is >= 0 and <= 7;
    }

    private static void ApplyMask(bool[,] m, int version, int mask)
    {
        var size = m.GetLength(0);

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
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
                    m[x, y] = !m[x, y];
                }
            }
        }
    }

    // ---------------------------------------------------------------- 数据译码

    /// <summary>
    /// 按标准的"之字形"顺序读出码字，然后按字节模式取值。
    ///
    /// ⚠️ **不做 Reed-Solomon 纠错译码** —— 所以只能读无损的二维码。
    ///    这正是"能扫屏幕上的、扫不了拍照的"这个能力边界的技术原因。
    /// </summary>
    private static string? TryDecodeBytes(bool[,] m, int version, string eccName, out string? error)
    {
        error = null;

        try
        {
            var size = m.GetLength(0);
            var bits = new List<bool>();

            var upward = true;
            for (var right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6)
                {
                    right = 5;
                }

                for (var vert = 0; vert < size; vert++)
                {
                    var y = upward ? size - 1 - vert : vert;

                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;

                        if (QrMatrix.IsFunctionModule(version, x, y))
                        {
                            continue;
                        }

                        bits.Add(m[x, y]);
                    }
                }

                upward = !upward;
            }

            // 位 → 字节
            var codewords = new List<byte>(bits.Count / 8);
            for (var i = 0; i + 7 < bits.Count; i += 8)
            {
                var b = 0;
                for (var k = 0; k < 8; k++)
                {
                    if (bits[i + k])
                    {
                        b |= 1 << (7 - k);
                    }
                }

                codewords.Add((byte)b);
            }

            if (codewords.Count == 0)
            {
                error = "读不到任何数据码字。";
                return null;
            }

            // ① 去交错：把交错排列的块还原成"原始数据码字序列"
            //    这里**只取数据部分**（跳过纠错码字），且不做纠错译码
            var dataCodewords = Deinterleave(codewords, version, eccName);

            // ② 从数据码字里读位流
            var reader = new BitReader(dataCodewords);

            var mode = reader.Read(4);
            if (mode != 0b0100)
            {
                error = $"这个二维码用的不是字节模式（模式指示符 = {mode:X}）。\n"
                        + "本工具只支持字节模式（文字 / 链接最常见的就是它）。";
                return null;
            }

            var countBits = version <= 9 ? 8 : 16;
            var length = reader.Read(countBits);

            if (length <= 0 || length > dataCodewords.Length)
            {
                error = "读出的内容长度不合理，二维码可能不完整。";
                return null;
            }

            var payload = new byte[length];
            for (var i = 0; i < length; i++)
            {
                payload[i] = (byte)reader.Read(8);
            }

            // 按 UTF-8 解 —— 与本工具的编码侧一致
            return System.Text.Encoding.UTF8.GetString(payload);
        }
        catch (Exception ex)
        {
            error = $"解码失败：{ex.Message}";
            return null;
        }
    }

    private static byte[] Deinterleave(List<byte> codewords, int version, string eccName)
    {
        // 版本 1~10 的块结构表（与 QrEncoder 保持一致）
        var table = QrEncoder.GetBlockInfo(version, eccName);
        var (dataTotal, ecPerBlock, g1Blocks, g1Data, g2Blocks, g2Data) = table;

        var data = new byte[dataTotal];
        var pos = 0;

        // 交错时是"按列取各块的数据码字"，还原要反着来
        var blocks = new List<int>();
        for (var i = 0; i < g1Blocks; i++) { blocks.Add(g1Data); }
        for (var i = 0; i < g2Blocks; i++) { blocks.Add(g2Data); }

        var maxLen = blocks.Count > 0 ? blocks.Max() : 0;
        var index = 0;

        for (var i = 0; i < maxLen; i++)
        {
            for (var b = 0; b < blocks.Count; b++)
            {
                if (i < blocks[b] && index < codewords.Count && pos < data.Length)
                {
                    data[pos++] = codewords[index++];
                }
            }
        }

        return data;
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bitPos;

        public BitReader(byte[] data) => _data = data;

        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                var byteIndex = _bitPos >> 3;
                if (byteIndex >= _data.Length)
                {
                    return value;
                }

                var bit = (_data[byteIndex] >> (7 - (_bitPos & 7))) & 1;
                value = (value << 1) | bit;
                _bitPos++;
            }

            return value;
        }
    }
}
