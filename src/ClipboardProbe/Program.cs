using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

// WPF 与 WinForms 同时引用会撞名（System.Windows.Shapes.Path vs System.IO.Path 等），
// 这里显式指定成 System.IO 的版本，避免全篇 CS0103。
using Path = System.IO.Path;
using File = System.IO.File;
using FileInfo = System.IO.FileInfo;
using Directory = System.IO.Directory;
using MemoryStream = System.IO.MemoryStream;

namespace ClipboardProbe;

/// <summary>
/// P0 探针：只做一件事——证明“文本 / 图片 / 文件”三类剪贴板内容能被完整、可靠地抓到。
///
/// 刻意没有界面、没有托盘、没有热键。UI 只有在这份探针证明抓取可靠之后才值得投资。
/// 用法：双击 run-probe.cmd，或 dotnet run --project src\ClipboardProbe
/// 停止：在窗口里按回车，或 Ctrl+C（两种都会写 summary.json）。
/// </summary>
internal static class Program
{
    private const int MaxTextPreview = 200;
    private const int ReadAttempts = 4;
    private const int FirstRetryDelayMs = 60;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> SeenHashes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> KindCounts = new(StringComparer.Ordinal);

    private static ProbeStore _store = null!;
    private static int _seq;
    private static int _retryTotal;
    private static int _failedTotal;
    private static int _emptyTotal;
    private static int _duplicateTotal;
    private static long _readMsTotal;
    private static DateTime _startedAt;
    private static int _summaryWritten;

    /// <summary>自检模式：只验证抓取链路，不落盘历史（避免把测试内容混进真实记录）。</summary>
    internal static bool SelfTestMode;

    /// <summary>最近一次抓取结果，自检用来断言。</summary>
    internal static volatile ProbeEntry? LastEntry;

    /// <summary>最近一次事件时间戳（Stopwatch ticks），自检用来等待。</summary>
    internal static long LastEventAt;

    /// <summary>落盘器。自检模式也会赋值，好让 blob 存储链路一起被验证。</summary>
    internal static ProbeStore Store
    {
        get => _store;
        set => _store = value;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _startedAt = DateTime.Now;

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            return SelfTest.Run(Console.Out);
        }

        var root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            is { } pathArg
            ? Path.GetFullPath(pathArg)
            : Path.Combine(ResolveWorkspaceDir(), "artifacts", "probe");

        _store = new ProbeStore(root);
        Log.Open(Path.Combine(root, "probe.log"));

        Log.Line("=== 快捷剪贴板 · P0 抓取探针 ===");
        Log.Line($"启动时间   : {_startedAt:yyyy-MM-dd HH:mm:ss}");
        Log.Line($"落盘目录   : {root}");
        Log.Line($"本次索引   : {_store.JsonlPath}");
        Log.Line($"图片本体   : {_store.BlobDir}");
        Log.Line($"日志文件   : {Path.Combine(root, "probe.log")}");
        Log.Line();
        Log.Line("现在起正常用电脑：复制文字、截图/复制图片、在资源管理器里复制文件…");
        Log.Line("每抓到一条就打一行。停止方式：Ctrl+C（会生成 summary.json）。");
        Log.Line("--------------------------------------------------------------");

        using var listener = new ClipboardListener();
        var messageLoopReady = new ManualResetEventSlim(false);
        Exception? threadFailure = null;

        // 剪贴板 API 与消息循环都必须在同一个 STA 线程上，所以单独开一条专用线程常驻。
        var probeThread = new Thread(() =>
        {
            try
            {
                var formsContext = new System.Windows.Forms.ApplicationContext();

                if (!listener.Start())
                {
                    var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        $"AddClipboardFormatListener 失败（Win32 错误码 {err}）。");
                }

                listener.ClipboardUpdated += OnClipboardUpdated;
                messageLoopReady.Set();
                System.Windows.Forms.Application.Run(formsContext);
            }
            catch (Exception ex)
            {
                threadFailure = ex;
                messageLoopReady.Set();
            }
        })
        {
            IsBackground = true,
            Name = "ClipboardProbe.MessageLoop",
        };

        probeThread.SetApartmentState(ApartmentState.STA);
        probeThread.Start();
        messageLoopReady.Wait();

        if (threadFailure is not null)
        {
            Log.Error($"[探针] 启动失败：{threadFailure.Message}");
            return 2;
        }

        Log.Line("监听已注册（AddClipboardFormatListener 成功）。等待剪贴板事件…");
        Log.Line();

        // 刻意不用 Console.ReadLine 收工：被放到后台跑时 stdin 立刻 EOF，ReadLine 返回 null，
        // 探针会“秒退”并且一条都抓不到（实测踩过）。这里改成等 Ctrl+C。
        using var shutdown = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // 自己控制收尾顺序，避免进程被直接掐掉导致 summary 写不出来
            shutdown.Set();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteSummary("进程退出");

        Log.Line("（停止探针请按 Ctrl+C；日志同时写在 probe.log，关掉窗口也不影响已抓到的记录。）");
        shutdown.Wait();

        WriteSummary("Ctrl+C");
        listener.Dispose();
        System.Windows.Forms.Application.ExitThread();
        probeThread.Join(TimeSpan.FromSeconds(3));
        return 0;
    }

    /// <summary>
    /// 定位工作区目录。用意是“路径写错就早点炸，而不是把记录默默写到别处去”——
    /// 探针要挂一整天，如果因为目录名多一个空格而写到别的地方，用户会白等一天。
    /// 两种常见写法都试一下（带空格 / 不带空格），找不到就抛出明确错误。
    /// </summary>
    private static string ResolveWorkspaceDir()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var candidates = new[] { "DeepSeek-Workspace", "DeepSeek Workspace" };

        foreach (var name in candidates)
        {
            var known = Path.Combine(desktop, name, "04-其他项目", "桌面工具箱");
            if (Directory.Exists(known))
            {
                return known;
            }
        }

        // 都不存在：用第一个候选名，并立刻建出来（宁可新建也不要写到别处）
        var fallback = Path.Combine(desktop, candidates[0], "04-其他项目", "桌面工具箱");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    internal static void OnClipboardUpdated()
    {
        var sw = Stopwatch.StartNew();
        var entry = new ProbeEntry
        {
            Seq = Interlocked.Increment(ref _seq),
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        };

        try
        {
            entry.Kind = ReadClipboard(entry);
        }
        catch (Exception ex)
        {
            entry.Kind = "failed";
            // 带上堆栈：剪贴板读取的异常栈很浅，不带堆栈就只能靠猜是哪个 API 炸的
            entry.Error = $"{ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";
            Interlocked.Increment(ref _failedTotal);
        }

        sw.Stop();
        entry.ReadMs = sw.ElapsedMilliseconds;
        Interlocked.Add(ref _readMsTotal, entry.ReadMs);
        Interlocked.Add(ref _retryTotal, Math.Max(0, entry.Attempts - 1));
        LastEntry = entry;

        lock (Gate)
        {
            KindCounts[entry.Kind] = KindCounts.TryGetValue(entry.Kind, out var c) ? c + 1 : 1;
        }

        if (entry.Kind == "empty")
        {
            Interlocked.Increment(ref _emptyTotal);
        }

        LastEventAt = Stopwatch.GetTimestamp();

        Log.Line($"[{entry.Time}] #{entry.Seq:D4} {ProbeStore.Describe(entry)}");

        if (entry.Kind == "failed")
        {
            WriteStats();
            return;
        }

        if (!SelfTestMode)
        {
            _store.Append(entry);
        }

        WriteStats();

        if (entry.Attempts > 1)
        {
            Log.Line($"                ↳ 重试了 {entry.Attempts - 1} 次才读到（延迟渲染，属正常）");
        }
    }

    /// <summary>
    /// 读取顺序是刻意的：文件 → 图片 → 文本。
    /// 原因：从资源管理器复制文件时，剪贴板里同时挂着 CF_HDROP 和一个“延迟渲染的 CF_BITMAP 缩略图”。
    /// 如果先读图片，会逼着 shell 现场生成缩略图——慢，而且在大目录上可能卡住。
    /// 反过来先认 CF_HDROP，既快又准。
    /// </summary>
    private static string ReadClipboard(ProbeEntry entry)
    {
        var attempts = 1;

        var data = WithRetry(() => System.Windows.Clipboard.GetDataObject(), out var a1);
        attempts = Math.Max(attempts, a1);
        if (data is null)
        {
            entry.Attempts = attempts;
            entry.Error = "GetDataObject 返回 null";
            return "empty";
        }

        var formats = WithRetry(() => data.GetFormats(false), out var a2) ?? Array.Empty<string>();
        attempts = Math.Max(attempts, a2);
        entry.Formats = formats.ToList();

        // ---- 1) 文件（CF_HDROP）----
        if (formats.Any(f => f.Equals("FileDrop", StringComparison.OrdinalIgnoreCase)
                          || f.Equals("FileNameW", StringComparison.OrdinalIgnoreCase)))
        {
            var list = WithRetry(() => System.Windows.Clipboard.GetFileDropList(), out var a3);
            attempts = Math.Max(attempts, a3);
            if (list is { Count: > 0 })
            {
                entry.Files = new List<FileInfoItem>();
                long total = 0;
                foreach (var path in list)
                {
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    var item = new FileInfoItem { Path = path };
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            item.IsDirectory = true;
                            item.Exists = true;
                        }
                        else if (File.Exists(path))
                        {
                            item.Exists = true;
                            item.Size = new FileInfo(path).Length;
                        }
                    }
                    catch
                    {
                        // 权限不足之类，就当读不到——探针不因此失败
                    }

                    total += item.Size;
                    entry.Files.Add(item);
                }

                entry.Bytes = total;
                entry.Hash = HashStrings(entry.Files.Select(f => f.Path));
                entry.Duplicate = MarkSeen(entry.Hash);
                entry.Attempts = attempts;
                return "files";
            }
        }

        // ---- 2) 图片 ----
        var hasBitmap = formats.Any(f =>
            f.Contains("Bitmap", StringComparison.OrdinalIgnoreCase) ||
            f.Equals("PNG", StringComparison.OrdinalIgnoreCase) ||
            f.Equals("DeviceIndependentBitmap", StringComparison.OrdinalIgnoreCase));

        if (hasBitmap)
        {
            // 图片读取单独特判：某些来源（非 OLE 的 GDI 位图、损坏的 DIB、shell 缩略图）
            // 会让 Clipboard.GetImage 直接抛 NullReferenceException——.NET 内部实现的问题。
            // 这里必须接住：异常逃出去会把调用方（窗口过程/消息循环线程）打死，
            // 表现为“之后所有剪贴板事件全部消失”，比丢一条记录严重得多。
            BitmapSource? source = null;
            string? imageError = null;
            try
            {
                source = WithRetry(() => System.Windows.Clipboard.GetImage(), out var a4);
                attempts = Math.Max(attempts, a4);
                Log.Line($"                ↳ 诊断：GetImage 返回 {(source is null ? "null" : $"{source.PixelWidth}x{source.PixelHeight} fmt={source.Format}")}");
            }
            catch (Exception ex)
            {
                imageError = $"{ex.GetType().Name}: {ex.Message}";
                Log.Line($"                ↳ 诊断：GetImage 抛异常 → {imageError}");
            }

            if (source is not null)
            {
                var png = EncodePng(source);
                entry.Bytes = png.Length;
                entry.ImageWidth = source.PixelWidth;
                entry.ImageHeight = source.PixelHeight;
                entry.Hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
                entry.Duplicate = MarkSeen(entry.Hash);
                entry.Blob = _store.SaveBlob(entry.Seq, entry.Hash, png, ".png");

                var textAlongside = TryGetText(formats, out var a4b);
                attempts = Math.Max(attempts, a4b);
                if (!string.IsNullOrEmpty(textAlongside))
                {
                    entry.TextLength = textAlongside.Length;
                    entry.TextPreview = Truncate(textAlongside);
                    AddExtra(entry, "同时带文本");
                }

                entry.Attempts = attempts;
                return "image";
            }

            if (imageError is not null)
            {
                AddExtra(entry, "图片读取失败：" + imageError);
            }
        }

        // ---- 3) 文本 ----
        var plain = TryGetText(formats, out var a5);
        attempts = Math.Max(attempts, a5);
        if (!string.IsNullOrEmpty(plain))
        {
            var utf8 = Encoding.UTF8.GetBytes(plain);
            entry.Bytes = utf8.Length;
            entry.TextLength = plain.Length;
            entry.TextPreview = Truncate(plain);
            entry.Hash = Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();
            entry.Duplicate = MarkSeen(entry.Hash);

            var html = TryGetCustom(formatPrefix: "HTML Format");
            if (!string.IsNullOrEmpty(html))
            {
                AddExtra(entry, "带 HTML 格式");
                var htmlBytes = Encoding.UTF8.GetBytes(html);
                _store.SaveBlob(entry.Seq, entry.Hash, htmlBytes, ".html");
            }

            var rtf = TryGetCustom(formatPrefix: "Rich Text Format");
            if (!string.IsNullOrEmpty(rtf))
            {
                AddExtra(entry, "带 RTF 格式");
            }

            entry.Attempts = attempts;
            return "text";
        }

        // ---- 4) 没认出来 ----
        entry.Attempts = attempts;
        if (formats.Length == 0)
        {
            return "empty"; // 典型的“剪贴板被清空”
        }

        entry.Error = "有格式但都不是我们支持的（txt/img/files）";
        return "empty";
    }

    private static string? TryGetText(string[] formats, out int attemptsUsed)
    {
        attemptsUsed = 1;
        if (!formats.Any(f => f.Contains("Text", StringComparison.OrdinalIgnoreCase)
                           || f.Equals("UnicodeText", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var text = WithRetry(() => System.Windows.Clipboard.GetText(), out var a);
        attemptsUsed = a;
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? TryGetCustom(string formatPrefix)
    {
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null)
            {
                return null;
            }

            foreach (var fmt in data.GetFormats(false))
            {
                if (!fmt.Contains(formatPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (data.GetData(fmt) is string s && !string.IsNullOrEmpty(s))
                {
                    return s;
                }
            }
        }
        catch
        {
            // 自定义格式读不到无所谓，探针不因此判失败
        }

        return null;
    }

    /// <summary>
    /// 带重试的剪贴板读取。延迟渲染（Office / 浏览器 / 部分编辑器）下第一次读常抛
    /// COMException 或返回空，退避几十毫秒再来一次基本就成。
    /// 注意：只读我们关心的那几个格式，绝不枚举全部格式——枚举会触发所有延迟渲染，
    /// 轻则变慢，重则把源程序或整个剪贴板卡死。
    /// </summary>
    private static T? WithRetry<T>(Func<T?> action, out int attemptsUsed)
    {
        var delay = FirstRetryDelayMs;
        Exception? last = null;

        for (var attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            try
            {
                var result = action();
                if (result is not null)
                {
                    attemptsUsed = attempt;
                    return result;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            if (attempt < ReadAttempts && delay > 0)
            {
                Thread.Sleep(delay);
                delay *= 2;
            }
        }

        attemptsUsed = ReadAttempts;
        if (last is not null)
        {
            throw last;
        }

        return default;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static string HashStrings(IEnumerable<string> items)
    {
        var joined = string.Join('\u0000', items);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    /// <summary>内容级去重：同一份内容重复复制只算一条，但会把“重复次数”记下来。</summary>
    private static bool MarkSeen(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        lock (Gate)
        {
            if (SeenHashes.ContainsKey(hash))
            {
                SeenHashes[hash]++;
                Interlocked.Increment(ref _duplicateTotal);
                return true;
            }

            SeenHashes[hash] = 1;
            return false;
        }
    }

    private static void AddExtra(ProbeEntry entry, string note)
    {
        entry.Extras ??= new List<string>();
        entry.Extras.Add(note);
    }

    private static string Truncate(string s) =>
        s.Length <= MaxTextPreview ? s : s[..MaxTextPreview] + "…";

    private static void WriteSummary(string reason)
    {
        if (Interlocked.Exchange(ref _summaryWritten, 1) == 1)
        {
            return;
        }

        var elapsed = DateTime.Now - _startedAt;
        var summary = new
        {
            停止原因 = reason,
            开始时间 = _startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            结束时间 = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            运行时长 = elapsed.ToString(@"hh\:mm\:ss"),
            总事件数 = _seq,
            分类计数 = KindCounts,
            内容唯一数 = SeenHashes.Count,
            重复内容次数 = _duplicateTotal,
            空事件数 = _emptyTotal,
            读取失败数 = _failedTotal,
            重试次数 = _retryTotal,
            平均读取毫秒 = _seq == 0 ? 0 : Math.Round((double)_readMsTotal / _seq, 2),
            索引文件 = _store.JsonlPath,
            图片目录 = _store.BlobDir,
        };

        try
        {
            _store.WriteSummary(summary);
            Log.Line();
            Log.Line("--------------------------------------------------------------");
            Log.Line($"探针已停止（{reason}）。");
            Log.Line($"总事件 {_seq}｜文本/图片/文件 {KindText()}｜唯一内容 {SeenHashes.Count}｜重复 {_duplicateTotal}");
            Log.Line($"空事件 {_emptyTotal}｜读取失败 {_failedTotal}｜重试 {_retryTotal}｜平均 {(summary.平均读取毫秒)} ms");
            Log.Line($"汇总已写入：{Path.Combine(_store.Root, "summary.json")}");
        }
        catch (Exception ex)
        {
            Log.Error($"[探针] 写汇总失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 每条事件后滚动写一次统计。被强制结束时（关窗口/注销/重启/被别的进程 kill）
    /// 退出钩子不会执行，只有这个滚动文件能留下“跑到哪了、文件在哪”。
    /// </summary>
    private static void WriteStats()
    {
        try
        {
            _store.WriteStats(new
            {
                状态 = "运行中或已结束（本文件每条事件刷新一次，被强制结束也留得住）",
                开始时间 = _startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                更新时间 = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                运行时长 = (DateTime.Now - _startedAt).ToString(@"hh\:mm\:ss"),
                总事件数 = _seq,
                分类计数 = KindCounts,
                内容唯一数 = SeenHashes.Count,
                重复内容次数 = _duplicateTotal,
                空事件数 = _emptyTotal,
                读取失败数 = _failedTotal,
                重试次数 = _retryTotal,
                索引文件 = _store.JsonlPath,
                图片目录 = _store.BlobDir,
                日志文件 = Path.Combine(_store.Root, "probe.log"),
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[探针] 写统计失败（不影响抓取）：{ex.Message}");
        }
    }

    private static string KindText()
    {
        lock (Gate)
        {
            return KindCounts.Count == 0
                ? "(无)"
                : string.Join(" / ", KindCounts.Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }
}
