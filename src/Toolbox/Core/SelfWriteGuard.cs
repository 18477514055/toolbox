using System.Security.Cryptography;
using System.Text;

namespace Toolbox.Core;

/// <summary>
/// 「我们自己写的，不进档案」—— 硬性规则③的实现。
///
/// 背景（DECISIONS.md）：剪贴板是「通道」，剪贴板历史是「档案」。
/// 工具箱为了搬数据往剪贴板写东西（比如用户点「复制译文」），
/// 如果不管，历史里就会多出一条我们自己造成的垃圾记录。
///
/// 两条识别手段一起用，因为各有盲区：
///   1. **自定义格式 token** —— 写的时候在剪贴板里塞一个只有我们认识的格式，
///      读的时候看到它就 100% 确定是自己写的。图片走 DIB 往返会改变像素格式，
///      哈希对不上，所以 token 是图片场景的唯一可靠手段。
///   2. **内容哈希** —— 文本场景下即使 token 因为某些程序重建剪贴板而丢失，
///      哈希还能兜住。
///
/// ⚠️ 刻意**不做**的事：靠"来源进程"判断。GetClipboardOwner 常常返回 0，
/// 第三方进程不可信。只做"我们自己写的我们标记"。
/// </summary>
internal static class SelfWriteGuard
{
    /// <summary>放在剪贴板里的私有格式名。别的程序不认识，也不会用它。</summary>
    public const string SelfWriteFormat = "Toolbox.SelfWrite.Token";

    /// <summary>标记的有效期。太短会在慢机器上漏判，太长会误判用户之后的复制。</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> Tokens = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> Hashes = new(StringComparer.Ordinal);

    /// <summary>写入前调用：生成一个 token，并在剪贴板里带上它。</summary>
    public static string NewToken()
    {
        var token = Guid.NewGuid().ToString("N");
        lock (Gate)
        {
            Prune();
            Tokens[token] = DateTime.UtcNow + Ttl;
        }

        return token;
    }

    public static void RegisterHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return;
        }

        lock (Gate)
        {
            Prune();
            Hashes[hash] = DateTime.UtcNow + Ttl;
        }
    }

    /// <summary>读取后调用：这条内容是不是我们自己写进去的？</summary>
    public static bool IsOurs(string? token, string? hash)
    {
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            Prune();

            if (!string.IsNullOrEmpty(token) && Tokens.ContainsKey(token))
            {
                return true;
            }

            return !string.IsNullOrEmpty(hash) && Hashes.ContainsKey(hash);
        }
    }

    private static void Prune()
    {
        var now = DateTime.UtcNow;

        foreach (var key in Tokens.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
        {
            Tokens.Remove(key);
        }

        foreach (var key in Hashes.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
        {
            Hashes.Remove(key);
        }
    }

    public static string HashText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string HashBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string HashPaths(IEnumerable<string> paths)
        => HashText(string.Join('\u0000', paths));
}
