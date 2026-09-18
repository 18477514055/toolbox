using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Shell;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace Toolbox.Tools.Clipboard;

/// <summary>
/// 剪贴板历史的存储层：JSONL 索引 + blobs/thumbs/volatile 三类外置文件。
///
/// 为什么还是 JSONL 而不是 SQLite（DECISIONS.md §五·3）：
/// 本机 NuGet 缓存是空的，引任何库都要联网；而线性扫描几万条是毫秒级，够用。
/// 真到十万级或需要分词搜索时再上 SQLite FTS5，**索引格式不用重做**。
///
/// 三条设计要点：
///   1. **超长内容不进档案** —— 复制一整篇文章时，历史里只留一行摘要，
///      正文放 volatile\。这条同时是 AI 工具"读整页"能安全落地的前提。
///   2. **Origin=tool 的条目默认不显示** —— 见「通道与档案分离」。
///   3. **文件条目只存路径** —— 永远不复制文件本体。
/// </summary>
internal sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly List<ClipEntry> _entries = new(); // 最新的在前
    private readonly SettingsStore _settings;

    public HistoryStore(SettingsStore settings)
    {
        _settings = settings;
    }

    public event Action? Changed;

    public string Root => AppPaths.ClipboardDir;

    /// <summary>内存里的全部条目（最新在前）。调用方不要改这个列表。</summary>
    public IReadOnlyList<ClipEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    // ---------------------------------------------------------------- 载入

    public void Load()
    {
        try
        {
            var path = AppPaths.IndexFile;
            if (!File.Exists(path))
            {
                // ⚠️ 这里**不能直接 return**。
                //
                // 原来的写法是 `return;`，于是"索引文件不存在"这条早退路径
                // 会**连底下的 SweepOrphanPayloads() 一起跳过**。
                // 后果：全新安装（尤其是便携模式，初次运行根本没有 index.jsonl）时，
                // volatile\ 里的孤儿正文文件**永远不会被清理** —— 正好把这个自愈机制
                // 关在最需要它的那个场景里（全新环境 + 反复自检/试用产生的残留）。
                //
                // 这是"早退跳过收尾逻辑"的典型 bug：收尾代码写在 try 之后，
                // 一旦 try 里出现 return，它就变成了这条路径上不可达的死代码。
                // 现在改成不早退，让流程统一走到最后。
                Log.Line($"剪贴板历史为空（{path} 不存在），从零开始。");
            }
            else
            {
                var loaded = new List<ClipEntry>();
                var broken = 0;

                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var entry = JsonSerializer.Deserialize<ClipEntry>(line, ReadOptions);
                        if (entry is not null)
                        {
                            loaded.Add(entry);
                        }
                    }
                    catch
                    {
                        // 单行坏掉不该让整个历史打不开——这正是 JSONL 相对单文件 JSON 的优势
                        broken++;
                    }
                }

                lock (_gate)
                {
                    _entries.Clear();
                    _entries.AddRange(loaded
                        .OrderByDescending(e => e.Pinned)
                        .ThenByDescending(e => SortTime(e)));
                }

                Log.Line($"剪贴板历史已载入 {loaded.Count} 条（跳过损坏行 {broken} 行）。");
            }
        }
        catch (Exception ex)
        {
            Log.Exception("载入剪贴板历史失败", ex);
        }

        // 载入完顺手把历史遗留的孤儿正文文件清掉（见方法上的说明）。
        // ★ 这一行必须在**所有**早退路径之外 —— 索引文件不存在时也要扫
        //   （那时 _entries 为空，alive 集合为空 ⇒ 目录里一切都会被判为孤儿并清掉，
        //    这正是"全新环境"下想要的行为）。
        SweepOrphanPayloads();
    }

    private static DateTime SortTime(ClipEntry e)
        => DateTime.TryParse(e.LastUsedAt ?? e.Time, out var t) ? t : DateTime.MinValue;

    // ---------------------------------------------------------------- 新增

    /// <summary>
    /// 把一条抓取结果并入历史。
    /// 返回 null 表示这条不该进档案（我们自己写的，或者不是有效内容）。
    /// </summary>
    public ClipEntry? Add(ClipReadResult result)
    {
        var entry = result.Entry;

        // 规则③：我们自己写的一律不进档案
        if (entry.IsToolWritten)
        {
            Log.Line($"跳过归档（工具箱自己写入）：{entry.Kind} {entry.TextLength ?? 0} 字");
            return null;
        }

        if (entry.Kind is ClipKind.Empty or ClipKind.Failed)
        {
            return null;
        }

        ClipEntry? existing;
        lock (_gate)
        {
            existing = entry.Hash is null
                ? null
                : _entries.FirstOrDefault(e => e.Hash == entry.Hash);
        }

        // 内容级去重：命中就把原条目顶到最前并更新时间戳（P1 行为，P0 只标记）
        if (existing is not null)
        {
            lock (_gate)
            {
                _entries.Remove(existing);
                existing.LastUsedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                existing.Duplicate = true;
                _entries.Insert(0, existing);
            }

            PersistAll();
            Changed?.Invoke();
            return existing;
        }

        // 落盘载荷
        try
        {
            SavePayload(result);
        }
        catch (Exception ex)
        {
            Log.Exception("保存剪贴板载荷失败", ex);
        }

        entry.LastUsedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        entry.Duplicate = false;

        lock (_gate)
        {
            _entries.Insert(0, entry);
        }

        Append(entry);
        EnforceQuota();
        Changed?.Invoke();
        return entry;
    }

    private void SavePayload(ClipReadResult result)
    {
        var entry = result.Entry;

        switch (entry.Kind)
        {
            case ClipKind.Image when result.PngBytes is not null && entry.Hash is not null:
            {
                // 按内容哈希命名 → 天然去重
                var blobName = entry.Hash + ".png";
                var blobFull = Path.Combine(AppPaths.BlobDir, blobName);
                if (!File.Exists(blobFull))
                {
                    File.WriteAllBytes(blobFull, result.PngBytes);
                }

                entry.Blob = Path.Combine("blobs", blobName);

                if (result.Image is not null)
                {
                    var thumbName = entry.Hash + ".png";
                    var thumbFull = Path.Combine(AppPaths.ThumbDir, thumbName);
                    if (!File.Exists(thumbFull))
                    {
                        var thumb = MakeThumbnail(result.Image);
                        File.WriteAllBytes(thumbFull, thumb);
                    }

                    entry.Thumb = Path.Combine("thumbs", thumbName);
                }

                break;
            }

            case ClipKind.Text when result.FullText is not null && entry.Hash is not null:
            {
                // 正文一律外置到 volatile\，索引里只留 200 字预览。
                // 两个理由：① 索引文件要小、要能全文扫描；② 超长内容绝不能整篇塞进档案
                //（复制一整篇文章时，列表里必须只是一行摘要，而不是一整屏正文）。
                var name = entry.Hash + ".txt";
                File.WriteAllText(Path.Combine(AppPaths.VolatileDir, name),
                    result.FullText, new UTF8Encoding(false));

                if (result.FullText.Length > ClipboardReader.LongTextThreshold)
                {
                    // 只有超长的才在条目上打标记——UI 据此把它显示成「文章（3200 字）」
                    entry.Volatile = Path.Combine("volatile", name);
                    Log.Line($"超长文本（{result.FullText.Length} 字）正文已外置，历史里只留一行摘要。");
                }

                break;
            }
        }

        // HTML / RTF 原文也留一份（用于排错与未来富文本粘贴）
        if (!string.IsNullOrEmpty(result.Html) && entry.Hash is not null)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppPaths.VolatileDir, entry.Hash + ".html"),
                    result.Html, new UTF8Encoding(false));
            }
            catch
            {
                // 不影响主流程
            }
        }
    }

    private static byte[] MakeThumbnail(BitmapSource source, int maxSide = 320)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        BitmapSource target = source;

        if (longest > maxSide)
        {
            var scale = (double)maxSide / longest;
            var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));

            // 转成 Bgra32 再编码：TransformedBitmap 的输出格式跟输入有关，
            // 统一一下可以避免个别格式编码失败
            var converted = new FormatConvertedBitmap(transformed, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            target = converted;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- 读取正文

    /// <summary>取完整正文（超长条目会去 volatile\ 里读）。</summary>
    public string? GetFullText(ClipEntry entry)
    {
        try
        {
            if (!string.IsNullOrEmpty(entry.Volatile))
            {
                var full = Path.Combine(AppPaths.ClipboardDir, entry.Volatile);
                return File.Exists(full) ? File.ReadAllText(full, Encoding.UTF8) : null;
            }

            // 未外置的：全文就在 volatile 目录里也留了一份（见 SavePayload 的文本分支）
            if (entry.Kind == ClipKind.Text && entry.Hash is not null)
            {
                var full = Path.Combine(AppPaths.VolatileDir, entry.Hash + ".txt");
                if (File.Exists(full))
                {
                    return File.ReadAllText(full, Encoding.UTF8);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Exception("读取历史正文失败", ex);
        }

        return entry.TextPreview;
    }

    public BitmapSource? LoadImage(ClipEntry entry)
    {
        try
        {
            if (string.IsNullOrEmpty(entry.Blob))
            {
                return null;
            }

            var full = Path.Combine(AppPaths.ClipboardDir, entry.Blob);
            if (!File.Exists(full))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // 立刻读进内存，别锁文件
            image.UriSource = new Uri(full);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Exception("加载历史图片失败", ex);
            return null;
        }
    }

    public BitmapSource? LoadThumb(ClipEntry entry)
    {
        try
        {
            if (string.IsNullOrEmpty(entry.Thumb))
            {
                return LoadImage(entry);
            }

            var full = Path.Combine(AppPaths.ClipboardDir, entry.Thumb);
            if (!File.Exists(full))
            {
                return LoadImage(entry);
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(full);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 修改

    public void SetPinned(ClipEntry entry, bool pinned)
    {
        entry.Pinned = pinned;
        lock (_gate)
        {
            _entries.Remove(entry);
            _entries.Insert(0, entry);
        }

        PersistAll();
        Changed?.Invoke();
    }

    public void Touch(ClipEntry entry)
    {
        entry.LastUsedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        lock (_gate)
        {
            _entries.Remove(entry);
            _entries.Insert(0, entry);
        }

        PersistAll();
        Changed?.Invoke();
    }

    public void Remove(ClipEntry entry)
    {
        lock (_gate)
        {
            _entries.Remove(entry);
        }

        DeletePayload(entry);
        PersistAll();
        Changed?.Invoke();
    }

    public void ClearAll(bool keepPinned)
    {
        List<ClipEntry> removed;
        lock (_gate)
        {
            removed = keepPinned ? _entries.Where(e => !e.Pinned).ToList() : _entries.ToList();
            foreach (var e in removed)
            {
                _entries.Remove(e);
            }
        }

        foreach (var e in removed)
        {
            DeletePayload(e);
        }

        PersistAll();
        Changed?.Invoke();
    }

    private static void DeletePayload(ClipEntry entry)
    {
        try
        {
            if (!string.IsNullOrEmpty(entry.Blob))
            {
                File.Delete(Path.Combine(AppPaths.ClipboardDir, entry.Blob));
            }

            if (!string.IsNullOrEmpty(entry.Thumb))
            {
                File.Delete(Path.Combine(AppPaths.ClipboardDir, entry.Thumb));
            }

            if (!string.IsNullOrEmpty(entry.Volatile))
            {
                File.Delete(Path.Combine(AppPaths.ClipboardDir, entry.Volatile));
            }

            // ★ 按 Hash 一并清理 volatile\ 里的正文文件。
            //
            // 为什么必须有这一步（这是一个真实存在过、且会静默膨胀磁盘的缺陷）：
            //   文本正文是**无条件**落盘到 volatile\<hash>.txt 的（见 SavePayload 的文本分支），
            //   但只有**超长**文本才会在条目上写 entry.Volatile 指针。
            //   于是普通长度的文本：文件躺在磁盘上、条目上没有指针 →
            //   上面那个 `if (entry.Volatile)` 永远命中不了 →
            //   删除条目 / LRU 淘汰 / 清空历史 统统回收不到它，只增不减。
            //   文件名就是 <hash>.txt，可以直接从 entry.Hash 推导出来，所以按 Hash 删即可。
            //
            //   .html 同理，而且它原来**根本没有任何删除路径**，产生的全是孤儿。
            if (!string.IsNullOrEmpty(entry.Hash))
            {
                File.Delete(Path.Combine(AppPaths.VolatileDir, entry.Hash + ".txt"));
                File.Delete(Path.Combine(AppPaths.VolatileDir, entry.Hash + ".html"));
            }
        }
        catch
        {
            // 删不掉就留着，不该因此让操作失败
        }
    }

    // ---------------------------------------------------------------- 孤儿清理

    /// <summary>
    /// 清掉 volatile\ 里不被任何条目引用的孤儿文件。
    ///
    /// 为什么需要这一步（光修 DeletePayload 是不够的）：
    ///   修好删除路径只能保证「以后不再产生」孤儿，清不掉**已经产生的**。
    ///   这个缺陷在用户机器上已经跑了一段时间，攒了一批删不掉的正文文件。
    ///   载入时扫一遍，把历史的账一并结了 —— 这是"自愈"而不是"打补丁"。
    ///
    /// 判据是「文件名是否被某个条目的 Hash 引用」，而不是看 entry.Volatile：
    /// 因为普通长度文本的正文文件本来就没有指针，只能按 Hash 认。
    /// </summary>
    private void SweepOrphanPayloads()
    {
        try
        {
            if (!Directory.Exists(AppPaths.VolatileDir))
            {
                return;
            }

            var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                foreach (var e in _entries)
                {
                    if (!string.IsNullOrEmpty(e.Hash))
                    {
                        alive.Add(e.Hash + ".txt");
                        alive.Add(e.Hash + ".html");
                    }

                    // 指针形式的也一并认下（超长条目走的是这条路）
                    if (!string.IsNullOrEmpty(e.Volatile))
                    {
                        alive.Add(Path.GetFileName(e.Volatile));
                    }
                }
            }

            var removed = 0;
            long freed = 0;

            foreach (var file in Directory.EnumerateFiles(AppPaths.VolatileDir))
            {
                if (alive.Contains(Path.GetFileName(file)))
                {
                    continue;
                }

                try
                {
                    freed += new FileInfo(file).Length;
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // 单个删不掉（被占用）就跳过，下次启动再试
                }
            }

            if (removed > 0)
            {
                Log.Line($"清理 volatile 孤儿正文文件 {removed} 个，释放 {freed / 1024.0:F1} KB。");
            }
        }
        catch (Exception ex)
        {
            Log.Exception("清理孤儿正文文件失败", ex);
        }
    }

    // ---------------------------------------------------------------- 配额

    /// <summary>条数上限 + 图片总量上限，LRU 淘汰，**置顶条目永不淘汰**。</summary>
    private void EnforceQuota()
    {
        var settings = _settings.Current;
        var removedAny = false;

        lock (_gate)
        {
            if (_entries.Count > settings.MaxEntries)
            {
                var victims = _entries
                    .Where(e => !e.Pinned)
                    .OrderBy(SortTime)
                    .Take(_entries.Count - settings.MaxEntries)
                    .ToList();

                foreach (var v in victims)
                {
                    _entries.Remove(v);
                    DeletePayload(v);
                    removedAny = true;
                }
            }

            var imageBytes = _entries
                .Where(e => e.Kind == ClipKind.Image && !e.Pinned)
                .Sum(e => e.Bytes);

            if (imageBytes > settings.MaxImageBytes)
            {
                foreach (var victim in _entries
                             .Where(e => e.Kind == ClipKind.Image && !e.Pinned)
                             .OrderBy(SortTime))
                {
                    if (imageBytes <= settings.MaxImageBytes)
                    {
                        break;
                    }

                    imageBytes -= victim.Bytes;
                    _entries.Remove(victim);
                    DeletePayload(victim);
                    removedAny = true;
                }
            }
        }

        if (removedAny)
        {
            Log.Line("已按配额淘汰旧条目（置顶条目不受影响）。");
            PersistAll();
        }
    }

    // ---------------------------------------------------------------- 持久化

    /// <summary>新增时走这条：只追加一行，进程被硬杀也不丢。</summary>
    private void Append(ClipEntry entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            lock (_gate)
            {
                File.AppendAllText(AppPaths.IndexFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            Log.Exception("追加历史索引失败", ex);
        }
    }

    /// <summary>结构变化（置顶/删除/淘汰/顶到最前）时整份重写。先写 .tmp 再原子替换。</summary>
    private void PersistAll()
    {
        try
        {
            string payload;
            lock (_gate)
            {
                var sb = new StringBuilder();
                foreach (var e in _entries)
                {
                    sb.AppendLine(JsonSerializer.Serialize(e, JsonOptions));
                }

                payload = sb.ToString();
            }

            var path = AppPaths.IndexFile;
            var temp = path + ".tmp";
            File.WriteAllText(temp, payload, new UTF8Encoding(false));

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("重写历史索引失败", ex);
        }
    }

    // ---------------------------------------------------------------- 查询

    /// <summary>按关键词过滤。文本内容、文件名、图片尺寸都参与匹配。</summary>
    public List<ClipEntry> Search(string? keyword, bool includeToolWritten)
    {
        var all = Entries;
        IEnumerable<ClipEntry> query = includeToolWritten
            ? all
            : all.Where(e => !e.IsToolWritten);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var k = keyword.Trim();
            query = query.Where(e => Matches(e, k));
        }

        return query
            .OrderByDescending(e => e.Pinned)
            .ThenByDescending(SortTime)
            .ToList();
    }

    private static bool Matches(ClipEntry e, string keyword)
    {
        if (!string.IsNullOrEmpty(e.TextPreview)
            && e.TextPreview.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (e.Files is not null
            && e.Files.Any(f => f.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (e.ImageWidth is not null
            && $"{e.ImageWidth}x{e.ImageHeight}".Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return e.Kind switch
        {
            ClipKind.Text => "文本".Contains(keyword, StringComparison.OrdinalIgnoreCase),
            ClipKind.Image => "图片".Contains(keyword, StringComparison.OrdinalIgnoreCase),
            ClipKind.Files => "文件".Contains(keyword, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public int Count => Entries.Count;
}
