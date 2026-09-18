using System.Security.Cryptography;
using Toolbox.Core;
using File = System.IO.File;
using Path = System.IO.Path;
// ⚠️ 必须显式 using System.IO 再单独别名 File / Path：
//    `using File = System.IO.File;` 这个别名会**遮蔽 System.IO 命名空间下的其他类型**
//    （FileShare / FileOptions / FileInfo / FileStream 全部找不到），
//    所以这里补一行 `using System.IO;` 让它们可见。
using System.IO;

namespace Toolbox.Tools.HashCheck;

/// <summary>
/// 支持的哈希算法。
/// </summary>
internal enum HashAlgorithmKind
{
    MD5,
    SHA1,
    SHA256,
    SHA512,
}

/// <summary>一次哈希计算的结果。</summary>
internal sealed record HashResult(
    bool Success,
    string Algorithm,
    string Hash,
    long Bytes,
    long ElapsedMs,
    string? Error)
{
    public static HashResult Fail(string algorithm, string error)
        => new(false, algorithm, "", 0, 0, error);
}

/// <summary>
/// 文件哈希计算。
///
/// ⚠️ **MD5 的安全说明**（必须写清楚，否则等于误导）：
///   MD5 与 SHA-1 在密码学上**已被攻破**——存在构造不同内容得到相同哈希的方法。
///   所以它们**只适合"校验文件有没有传坏"**这种非对抗场景，
///   **绝不能用来验证"这个文件有没有被恶意篡改"**。
///   本工具保留 MD5 是因为很多下载站/校验文件至今只提供 MD5（实用考虑），
///   但在界面上会明确标注这一点，不让人误以为 MD5 能防篡改。
///
/// 大文件策略：**流式分块读**，不把整个文件读进内存。
///   一个 4 GB 的镜像整读进内存会直接 OOM；分块读则内存占用恒定在缓冲区大小。
/// </summary>
internal static class HashService
{
    /// <summary>分块大小 1 MB：太小会频繁调系统调用，太大占内存，1 MB 是常见折中。</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>超过这个大小就在界面上提醒"要算一会儿"。</summary>
    public const long LargeFileThreshold = 100L * 1024 * 1024;

    /// <summary>
    /// 计算文件的哈希。
    ///
    /// <paramref name="progress"/> 每处理一块回调一次（0~1），用于显示进度。
    /// </summary>
    public static async Task<HashResult> ComputeAsync(
        string path,
        HashAlgorithmKind kind,
        CancellationToken ct,
        IProgress<double>? progress = null)
    {
        var algoName = ToDisplayName(kind);

        try
        {
            if (!File.Exists(path))
            {
                return HashResult.Fail(algoName, "文件不存在。");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);

            using HashAlgorithm hasher = kind switch
            {
                HashAlgorithmKind.MD5 => MD5.Create(),
                HashAlgorithmKind.SHA1 => SHA1.Create(),
                HashAlgorithmKind.SHA256 => SHA256.Create(),
                HashAlgorithmKind.SHA512 => SHA512.Create(),
                _ => SHA256.Create(),
            };

            var total = stream.Length;
            var buffer = new byte[BufferSize];
            long done = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                hasher.TransformBlock(buffer, 0, read, null, 0);
                done += read;

                if (total > 0)
                {
                    progress?.Report((double)done / total);
                }
            }

            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sw.Stop();

            var hash = System.Convert.ToHexString(hasher.Hash ?? Array.Empty<byte>());

            Log.Line($"哈希已计算：{Path.GetFileName(path)} {algoName} {hash[..Math.Min(16, hash.Length)]}…（{done} 字节，{sw.ElapsedMilliseconds} ms）");

            return new HashResult(true, algoName, hash, done, sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            return HashResult.Fail(algoName, "已取消。");
        }
        catch (Exception ex)
        {
            Log.Exception($"计算哈希失败：{path}", ex);
            return HashResult.Fail(algoName, $"{ex.Message}");
        }
    }

    /// <summary>
    /// 比较两个文件的哈希（直接用**同一个算法**各算一遍，再逐字符比）。
    ///
    /// 为什么不用"SHA-256 相同就等于内容相同"来做快速判定：
    ///   那本身就是哈希碰撞问题；对普通校验来说够用，但这里要给出**明确结论**，
    ///   所以先按需比**文件大小**（不等就一定不同，能省一次完整计算），
    ///   大小相同才算哈希。
    /// </summary>
    public static async Task<CompareResult> CompareAsync(
        string a, string b,
        HashAlgorithmKind kind,
        CancellationToken ct,
        IProgress<double>? progress = null)
    {
        try
        {
            if (!File.Exists(a)) { return CompareResult.Fail($"文件不存在：{a}"); }
            if (!File.Exists(b)) { return CompareResult.Fail($"文件不存在：{b}"); }

            var sizeA = new FileInfo(a).Length;
            var sizeB = new FileInfo(b).Length;

            // 大小不同 ⇒ 一定不同，省掉两次完整哈希计算（大文件上能省几十秒）
            if (sizeA != sizeB)
            {
                return CompareResult.MakeDifferent(
                    $"大小不同（{FormatSize(sizeA)} vs {FormatSize(sizeB)}），内容必然不同。",
                    sizeA, sizeB, "", "");
            }

            var ra = await ComputeAsync(a, kind, ct, progress);
            if (!ra.Success) { return CompareResult.Fail($"计算 {Path.GetFileName(a)} 失败：{ra.Error}"); }

            var rb = await ComputeAsync(b, kind, ct, progress);
            if (!rb.Success) { return CompareResult.Fail($"计算 {Path.GetFileName(b)} 失败：{rb.Error}"); }

            var same = string.Equals(ra.Hash, rb.Hash, StringComparison.OrdinalIgnoreCase);

            return same
                ? CompareResult.MakeSame($"{ra.Algorithm} 一致，两个文件内容相同。", sizeA, sizeB, ra.Hash, rb.Hash)
                : CompareResult.MakeDifferent($"{ra.Algorithm} 不一致，两个文件内容不同。", sizeA, sizeB, ra.Hash, rb.Hash);
        }
        catch (Exception ex)
        {
            Log.Exception("比较文件失败", ex);
            return CompareResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 把用户粘贴进来的"期望哈希"规范化：
    /// 去空格、去连字符（有些校验文件写成 `AA-BB-CC`）、统一大写。
    /// </summary>
    public static string NormalizeHash(string raw)
        => (raw ?? "").Replace(" ", "").Replace("-", "").Replace("\t", "").Trim().ToUpperInvariant();

    /// <summary>
    /// 校验"算出来的哈希"是否等于"期望的哈希"。
    /// 自动识别期望值是哪一种算法（按长度），并据此判断用的是不是同一个算法。
    /// </summary>
    public static (bool Match, string Message) Verify(string actual, string expectedRaw)
    {
        var expected = NormalizeHash(expectedRaw);

        if (expected.Length == 0)
        {
            return (false, "还没填要比对的哈希值。");
        }

        // 按长度识别算法，避免"拿 MD5 去比 SHA-256"这种无效比较
        var expectedAlgo = expected.Length switch
        {
            32 => "MD5",
            40 => "SHA1",
            64 => "SHA256",
            128 => "SHA512",
            _ => null,
        };

        if (expectedAlgo is null)
        {
            return (false, $"这个哈希长度是 {expected.Length} 位，不是 MD5(32)/SHA1(40)/SHA256(64)/SHA512(128) 中的任何一种，请检查是否复制完整。");
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"不一致。计算值 {actual[..Math.Min(16, actual.Length)]}… ≠ 期望值 {expected[..Math.Min(16, expected.Length)]}…（期望值看起来是 {expectedAlgo}）");
        }

        return (true, $"一致 —— 文件内容与期望的 {expectedAlgo} 完全相符。");
    }

    public static string ToDisplayName(HashAlgorithmKind kind) => kind switch
    {
        HashAlgorithmKind.MD5 => "MD5",
        HashAlgorithmKind.SHA1 => "SHA1",
        HashAlgorithmKind.SHA256 => "SHA256",
        HashAlgorithmKind.SHA512 => "SHA512",
        _ => "SHA256",
    };

    public static HashAlgorithmKind FromDisplayName(string? name) => name switch
    {
        "MD5" => HashAlgorithmKind.MD5,
        "SHA1" => HashAlgorithmKind.SHA1,
        "SHA512" => HashAlgorithmKind.SHA512,
        _ => HashAlgorithmKind.SHA256,
    };

    /// <summary>人看的体积格式。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) { return $"{bytes} B"; }
        if (bytes < 1024 * 1024) { return $"{bytes / 1024.0:F1} KB"; }
        if (bytes < 1024L * 1024 * 1024) { return $"{bytes / 1024.0 / 1024:F1} MB"; }
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }
}

/// <summary>两个文件的比较结果。</summary>
internal sealed record CompareResult(
    bool Ok,
    bool? Same,
    string Message,
    long SizeA,
    long SizeB,
    string HashA,
    string HashB)
{
    // ⚠️ 工厂方法不能叫 Same / Different —— 那会跟 record 自动生成的
    //    `Same` 属性重名（CS0102）。加 Make 前缀避开。
    public static CompareResult MakeSame(string msg, long sa, long sb, string ha, string hb)
        => new(true, true, msg, sa, sb, ha, hb);

    public static CompareResult MakeDifferent(string msg, long sa, long sb, string ha, string hb)
        => new(true, false, msg, sa, sb, ha, hb);

    public static CompareResult Fail(string msg)
        => new(false, null, msg, 0, 0, "", "");
}
