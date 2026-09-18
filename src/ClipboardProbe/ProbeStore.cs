using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// 同 Program.cs：显式绑定到 System.IO，避免与 WPF 的 Shapes.Path 等同名类型冲突。
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace ClipboardProbe;

internal enum ClipboardKind
{
    Text,
    Image,
    Files,
    Empty,
    Failed,
}

/// <summary>一条抓取记录。字段刻意留全，方便事后回看“到底漏了什么”。</summary>
internal sealed class ProbeEntry
{
    public int Seq { get; set; }
    public string Time { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<string> Formats { get; set; } = new();
    public string? Hash { get; set; }
    public long Bytes { get; set; }
    public List<FileInfoItem>? Files { get; set; }
    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }
    public string? TextPreview { get; set; }
    public int? TextLength { get; set; }
    public bool Duplicate { get; set; }
    public int Attempts { get; set; }
    public long ReadMs { get; set; }
    public string? Blob { get; set; }
    public List<string>? Extras { get; set; }
    public string? Error { get; set; }
}

internal sealed class FileInfoItem
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public long Size { get; set; }
    public bool IsDirectory { get; set; }
}

/// <summary>
/// 落盘：每天一个 JSONL（一行一条，追加写，崩溃不丢历史）+ blobs 存图片本体。
/// 拒绝数据库依赖，因为本机 NuGet 缓存是空的——V1 也不打算引第三方库。
/// </summary>
internal sealed class ProbeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string _root;
    private readonly string _blobDir;
    private readonly object _gate = new();
    private readonly string _jsonlPath;

    public ProbeStore(string root)
    {
        _root = root;
        _blobDir = Path.Combine(root, "blobs");
        Directory.CreateDirectory(_blobDir);
        _jsonlPath = Path.Combine(root, $"history-{DateTime.Now:yyyyMMdd}.jsonl");
    }

    public string Root => _root;
    public string JsonlPath => _jsonlPath;
    public string BlobDir => _blobDir;
    public string StatsPath => Path.Combine(_root, "stats.json");

    /// <summary>
    /// 把统计**滚动**写进文件（每条事件后调用一次）。
    ///
    /// 为什么不能只在退出时写：验收时实测——被强制结束（关窗口 / 注销 / 重启 / 任务管理器结束 /
    /// 别的进程 kill）时，退出钩子根本不执行，汇总文件就不会产生。
    /// 而汇总里那句“索引文件在哪、图片在哪”恰恰是接手方最需要的信息。
    /// 先写临时文件再替换（原子），避免写到一半被杀留下半个 JSON。
    /// </summary>
    public void WriteStats(object stats)
    {
        var temp = StatsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stats, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
        }), new UTF8Encoding(false));

        if (File.Exists(StatsPath))
        {
            File.Replace(temp, StatsPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, StatsPath);
        }
    }

    /// <summary>存图片/HTML/RTF 等二进制载荷，返回相对路径。</summary>
    public string SaveBlob(int seq, string hash, byte[] bytes, string ext)
    {
        // hash 可能为空（比如内容读取成功但哈希没算出来的边角情况），
        // 直接 hash[..12] 会 NRE——这里退化成用序号命名，不能让存证动作把整条链路炸掉。
        var tag = string.IsNullOrEmpty(hash) ? "nohash" : hash[..Math.Min(12, hash.Length)];
        var name = $"{seq:D5}-{tag}{ext}";
        var full = Path.Combine(_blobDir, name);
        File.WriteAllBytes(full, bytes);
        return Path.Combine("blobs", name);
    }

    public void Append(ProbeEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        lock (_gate)
        {
            File.AppendAllText(_jsonlPath, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    public void WriteSummary(object summary)
    {
        var path = Path.Combine(_root, "summary.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
        }), new UTF8Encoding(false));
    }

    /// <summary>把一条记录渲染成给人看的单行摘要。</summary>
    public static string Describe(ProbeEntry e)
    {
        var tag = e.Duplicate ? "dup " : "    ";
        return e.Kind switch
        {
            "text" => $"{tag}text  {e.Bytes,8} B  len={e.TextLength}  {Quote(e.TextPreview)}",
            "image" => $"{tag}image {e.Bytes,8} B  {e.ImageWidth}x{e.ImageHeight}  {e.Blob}",
            "files" => $"{tag}files {e.Bytes,8} B  x{e.Files?.Count}  {Quote(e.Files?.FirstOrDefault()?.Path)}",
            "empty" => $"{tag}empty （剪贴板被清空 / 或内容我们不认识）  formats=[{string.Join(",", e.Formats)}]",
            _ => $"{tag}FAIL  {e.Error}  formats=[{string.Join(",", e.Formats)}]",
        };
    }

    private static string Quote(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "\"\"";
        }

        var flat = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        return flat.Length > 70 ? "\"" + flat[..70] + "…\"" : "\"" + flat + "\"";
    }
}
