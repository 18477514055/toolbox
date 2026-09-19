using System.Text.Json.Serialization;

namespace Toolbox.Core;

public enum ClipKind
{
    Text,
    Image,
    Files,
    Empty,
    Failed,
}

/// <summary>
/// 一条剪贴板历史记录。
///
/// 字段在 P0 探针的基础上扩展（见 03-快捷剪贴板设计.md §三）：
///   Origin  —— user = 用户自己复制的；tool = 工具箱自己写的。见「通道与档案分离」。
///   Pinned  —— 置顶，LRU 淘汰时永不淘汰。
///   Thumb   —— 缩略图相对路径，列表用它，避免为了显示一行去解码原图。
///   Volatile—— 超长正文的外置文件（档案里只留一行摘要）。
/// </summary>
public sealed class ClipEntry
{
    public int Seq { get; set; }
    public string Time { get; set; } = "";
    public ClipKind Kind { get; set; }
    public List<string> Formats { get; set; } = new();
    public string? Hash { get; set; }
    public long Bytes { get; set; }
    public List<ClipFile>? Files { get; set; }
    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }
    public string? TextPreview { get; set; }
    public int? TextLength { get; set; }
    public bool Duplicate { get; set; }
    public int Attempts { get; set; }
    public long ReadMs { get; set; }

    /// <summary>图片原图相对路径（blobs\xxx.png）</summary>
    public string? Blob { get; set; }

    /// <summary>缩略图相对路径（thumbs\xxx.png）</summary>
    public string? Thumb { get; set; }

    /// <summary>超长文本正文相对路径（volatile\xxx.txt）</summary>
    public string? Volatile { get; set; }

    /// <summary>user = 用户自己复制的；tool = 工具箱自己写的（不进档案、默认不显示）</summary>
    public string Origin { get; set; } = "user";

    public bool Pinned { get; set; }

    /// <summary>最近一次被使用/再次复制的时间，排序用</summary>
    public string? LastUsedAt { get; set; }

    public string? Error { get; set; }
    public List<string>? Extras { get; set; }

    // ---------------- 以下是运行期状态，不写进 JSONL ----------------

    [JsonIgnore]
    public bool IsToolWritten => string.Equals(Origin, "tool", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public DateTime TimeValue => DateTime.TryParse(Time, out var t) ? t : DateTime.MinValue;
}

public sealed class ClipFile
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public long Size { get; set; }
    public bool IsDirectory { get; set; }
}

/// <summary>
/// 一次剪贴板读取的完整结果：元数据 + 真正的载荷。
///
/// 为什么元数据和载荷分开：元数据进 JSONL（要小、要能全文扫描），
/// 载荷可能很大（一篇 5 万字文章、一张 4K 截图），要么外置要么丢弃。
/// </summary>
public sealed class ClipReadResult
{
    public ClipEntry Entry { get; set; } = new();

    /// <summary>完整文本（不截断）。超长时由存储层决定是否外置。</summary>
    public string? FullText { get; set; }

    /// <summary>图片的 PNG 字节（原样落盘用）</summary>
    public byte[]? PngBytes { get; set; }

    /// <summary>解码后的图片对象（做缩略图用）</summary>
    public System.Windows.Media.Imaging.BitmapSource? Image { get; set; }

    public string? Html { get; set; }
    public string? Rtf { get; set; }
    public string[]? FilePaths { get; set; }

    /// <summary>命中「我们自己写的」标记时，这里是我们写进去的 token。</summary>
    public string? SelfToken { get; set; }
}
