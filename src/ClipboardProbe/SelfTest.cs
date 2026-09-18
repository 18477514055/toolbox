using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

// 同 Program.cs：显式绑定 System.IO，避开 WPF/WinForms 的同名类型冲突
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace ClipboardProbe;

/// <summary>
/// 自检：不靠“人肉复制”，而是程序自己往剪贴板放三类内容，再验证监听链路是否正确抓回。
///
/// 意义：把“编译通过”升级成“功能验证过”。跑之前会备份剪贴板，跑完自动恢复。
/// 用法：ClipboardProbe.exe --selftest
/// </summary>
internal static class SelfTest
{
    private sealed class Step
    {
        public string Name { get; init; } = "";
        public bool Passed { get; set; }
        public string Kind { get; set; } = "";
        public int? Files { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? TextLength { get; set; }
        public long ReadMs { get; set; }
        public int Attempts { get; set; }
        public string Detail { get; set; } = "";
    }

    private static readonly ManualResetEventSlim ClipboardEvent = new(false);

    public static int Run(TextWriter output)
    {
        Directory.CreateDirectory(ArtifactsRoot);
        Program.SelfTestMode = true;
        Program.LastEventAt = Stopwatch.GetTimestamp();

        // 自检也要真练一遍落盘（图片 blob 的编码→写文件→回读解码）。
        // 第一版就是漏了这步：_store 在自检模式下是 null，图片分支走到 SaveBlob 直接 NRE。
        Program.Store = new ProbeStore(Path.Combine(ArtifactsRoot, "selftest"));

        output.WriteLine("=== 快捷剪贴板 · 抓取链路自检 ===");
        output.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        output.WriteLine("说明：以下内容由程序自己放进剪贴板，用完会恢复你原来的剪贴板内容。");
        output.WriteLine();

        // 监听必须在一条常驻的 STA 线程上跑消息循环——WM_CLIPBOARDUPDATE 是靠窗口消息送来的，
        // 没有 Application.Run 就永远收不到事件（第一版自检就栽在这里：注册成功却零事件）。
        var listener = new ClipboardListener();
        var ready = new ManualResetEventSlim(false);
        Exception? startupFailure = null;

        var probeThread = new Thread(() =>
        {
            try
            {
                var context = new System.Windows.Forms.ApplicationContext();
                if (!listener.Start())
                {
                    throw new InvalidOperationException(
                        $"AddClipboardFormatListener 失败（Win32 错误码 {listener.LastError}）");
                }

                listener.ClipboardUpdated += () =>
                {
                    try
                    {
                        Program.OnClipboardUpdated();
                        ClipboardEvent.Set();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[自检] 事件处理异常：{ex.Message}");
                    }
                };

                Console.WriteLine($"[自检·诊断] 监听已注册：窗口句柄=0x{listener.WindowHandle.ToInt64():X}，"
                                  + $"注册线程={listener.ThreadId}，当前线程={Environment.CurrentManagedThreadId}");
                ready.Set();
                System.Windows.Forms.Application.Run(context);
            }
            catch (Exception ex)
            {
                startupFailure = ex;
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "ClipboardProbe.SelfTest.MessageLoop",
        };

        probeThread.SetApartmentState(ApartmentState.STA);
        probeThread.Start();
        ready.Wait(TimeSpan.FromSeconds(10));

        if (startupFailure is not null || !probeThread.IsAlive)
        {
            output.WriteLine($"[自检] 失败：{startupFailure?.Message ?? "消息循环线程未能启动"}");
            listener.Dispose();
            return 2;
        }

        // 先验窗口与消息循环本身是否活着：收不到自投 ping，就说明问题在消息循环，跟剪贴板无关
        listener.SendTestMessage();
        var pingDeadline = Stopwatch.StartNew();
        while (listener.PingCount == 0 && pingDeadline.ElapsedMilliseconds < 1500)
        {
            Thread.Sleep(30);
        }

        output.WriteLine(listener.PingCount > 0
            ? "  [环境] 消息窗口与消息循环自检通过（ping 已收到）"
            : "  [环境] ⚠ 消息窗口收不到自投消息——消息循环本身有问题，剪贴板通知不可能到达");

        // 备份用户剪贴板，跑完恢复
        var backup = SnapshotClipboard();

        var steps = new List<Step>();
        var tempDir = Path.Combine(ArtifactsRoot, "selftest-temp");
        Directory.CreateDirectory(tempDir);
        var loopAliveAfterTests = true;
        var messagesSeen = 0;

        try
        {
            // 探针专用临时文件（名字带随机串，保证与上一次自检不重复）
            var stamp = Guid.NewGuid().ToString("N")[..8];
            var fileA = Path.Combine(tempDir, $"探针样本A-{stamp}.txt");
            var fileB = Path.Combine(tempDir, $"探针样本B-{stamp}.bin");
            File.WriteAllText(fileA, "这是探针用来测试文件抓取的样本 A（中文内容，顺便验证编码）。", new UTF8Encoding(false));
            File.WriteAllBytes(fileB, new byte[4096]);

            steps.Add(Step1_Text(output));
            steps.Add(Step2_Image(output));
            steps.Add(Step3_FileDropPriority(output, fileA, fileB));
        }
        finally
        {
            RestoreClipboard(backup);
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // 删不掉就算了，不影响结论
            }

            // 收工前先记录诊断量，再撤监听
            loopAliveAfterTests = probeThread.IsAlive;
            messagesSeen = listener.MessageCount;

            listener.Dispose();
            System.Windows.Forms.Application.ExitThread();
            probeThread.Join(TimeSpan.FromSeconds(3));
        }

        var passed = steps.Count(s => s.Passed);
        var report = new
        {
            时间 = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            结论 = passed == steps.Count ? "全部通过" : $"失败 {steps.Count - passed} 项",
            通过 = $"{passed}/{steps.Count}",
            用例 = steps,
        };

        var reportPath = Path.Combine(ArtifactsRoot, "selftest-report.json");
        File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true,
            }), new UTF8Encoding(false));

        output.WriteLine();
        output.WriteLine("--------------------------------------------------");
        output.WriteLine($"自检结果：{passed}/{steps.Count} 通过 —— {report.结论}");
        output.WriteLine($"消息循环线程存活：{(loopAliveAfterTests ? "是（正常）" : "否（⚠ 有异常把消息循环打死了）")}");
        output.WriteLine($"收到 WM_CLIPBOARDUPDATE 共 {messagesSeen} 次");
        output.WriteLine($"报告已写入：{reportPath}");
        return passed == steps.Count ? 0 : 1;
    }

    private static string ArtifactsRoot
    {
        get
        {
            // 与 Program.ResolveWorkspaceDir 同一套逻辑：目录名带不带空格都认，
            // 免得因为一个空格把测试结果写到别的地方去。
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            foreach (var name in new[] { "DeepSeek-Workspace", "DeepSeek Workspace" })
            {
                var known = Path.Combine(desktop, name, "04-其他项目", "桌面工具箱", "artifacts");
                if (Directory.Exists(known))
                {
                    return known;
                }
            }

            return Path.Combine(desktop, "DeepSeek-Workspace", "04-其他项目", "桌面工具箱", "artifacts");
        }
    }

    // ---------------------------------------------------------------- 用例

    private static Step Step1_Text(TextWriter output)
    {
        var step = new Step { Name = "纯文本复制 → 抓到 text" };
        var marker = $"探针自检-文本-{Guid.NewGuid():N}"[..24];

        ClearClipboard();
        SetWithRetry(() => System.Windows.Clipboard.SetText(marker));

        // 写入后立刻回读：区分“写剪贴板失败”和“收不到通知”这两种完全不同的病
        output.WriteLine($"  [自检·诊断] 写入后回读：{DescribeClipboardNow()}");
        Thread.Sleep(200);
        output.WriteLine($"  [自检·诊断] 200ms 后再看：{DescribeClipboardNow()}");

        var (entry, ms) = WaitFor(e => e.Kind == "text" && e.TextPreview == marker, TimeSpan.FromSeconds(6));

        step.Kind = entry?.Kind ?? "(超时)";
        step.TextLength = entry?.TextLength;
        step.ReadMs = entry?.ReadMs ?? ms;
        step.Attempts = entry?.Attempts ?? 0;
        step.Passed = entry is { Kind: "text" } && entry.TextPreview == marker;
        step.Detail = step.Passed
            ? $"内容逐字一致，读到 {entry!.Bytes} 字节"
            : $"期望 text 且内容一致，实际 kind={entry?.Kind ?? "无事件"} preview={entry?.TextPreview}";

        Report(output, step);
        return step;
    }

    private static Step Step2_Image(TextWriter output)
    {
        var step = new Step { Name = "纯图片复制 → 抓到 image 且尺寸正确" };

        var expected = MakeBitmap(120, 80);
        ClearClipboard();
        SetWithRetry(() => System.Windows.Clipboard.SetImage(expected));

        var (entry, ms) = WaitFor(e => e.Kind == "image", TimeSpan.FromSeconds(6));

        step.Kind = entry?.Kind ?? "(超时)";
        step.Width = entry?.ImageWidth;
        step.Height = entry?.ImageHeight;
        step.ReadMs = entry?.ReadMs ?? ms;
        step.Attempts = entry?.Attempts ?? 0;

        // 尺寸是硬指标：剪贴板图片要走 DIB 往返，尺寸能对上说明真的拿到像素而不是空壳
        var sizeOk = entry?.ImageWidth == 120 && entry?.ImageHeight == 80;

        // 顺带验证落盘：PNG 文件真的存在，而且能重新解码成同尺寸的图
        var blobOk = false;
        var blobNote = "";
        if (entry?.Blob is { Length: > 0 } blobRel)
        {
            var blobFull = Path.Combine(Program.Store.Root, blobRel);
            try
            {
                if (File.Exists(blobFull))
                {
                    using var fs = File.OpenRead(blobFull);
                    var decoded = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    blobOk = decoded.PixelWidth == 120 && decoded.PixelHeight == 80;
                    blobNote = $"PNG 已落盘并回读解码成功（{new FileInfo(blobFull).Length} 字节）";
                }
                else
                {
                    blobNote = $"⚠ blob 文件不存在：{blobFull}";
                }
            }
            catch (Exception ex)
            {
                blobNote = $"⚠ blob 回读失败：{ex.Message}";
            }
        }
        else
        {
            blobNote = "⚠ 记录里没有 blob 路径";
        }

        step.Passed = entry is { Kind: "image" } && sizeOk && blobOk;
        step.Detail = step.Passed
            ? $"{entry!.ImageWidth}x{entry.ImageHeight}，{blobNote}"
            : $"期望 image 120x80 且落盘可回读，实际 kind={entry?.Kind ?? "无事件"} "
              + $"size={entry?.ImageWidth}x{entry?.ImageHeight} {blobNote} err={entry?.Error}";

        Report(output, step);
        return step;
    }

    private static Step Step3_FileDropPriority(TextWriter output, string fileA, string fileB)
    {
        var step = new Step { Name = "混合剪贴板（文件+文本+图片）→ 优先判为 files" };

        // 这是最容易踩的坑：从资源管理器复制文件时，剪贴板里除了 CF_HDROP，
        // 还挂着 shell 提供的“延迟渲染缩略图”。如果读取顺序先图片后文件，
        // 就会逼 shell 现场生成缩略图——慢，而且大目录上可能卡住。
        // 本用例就是把这个顺序钉死。
        var data = new System.Windows.Forms.DataObject();
        var dropped = new System.Collections.Specialized.StringCollection { fileA, fileB };
        data.SetFileDropList(dropped);
        data.SetText("同时存在的文本，不应该被优先选中");
        data.SetImage(ToGdiBitmap(MakeBitmap(32, 32)));

        ClearClipboard();
        SetWithRetry(() => System.Windows.Clipboard.SetDataObject(data, true));

        var (entry, ms) = WaitFor(e => e.Kind == "files", TimeSpan.FromSeconds(6));

        step.Kind = entry?.Kind ?? "(超时)";
        step.Files = entry?.Files?.Count;
        step.ReadMs = entry?.ReadMs ?? ms;
        step.Attempts = entry?.Attempts ?? 0;

        var pathsOk = entry?.Files is { Count: 2 }
            && entry.Files.Any(f => f.Path == fileA && f.Exists)
            && entry.Files.Any(f => f.Path == fileB && f.Exists && f.Size == 4096);

        step.Passed = entry is { Kind: "files" } && pathsOk;
        step.Detail = step.Passed
            ? $"2 个文件、路径与大小均正确（B 文件 4096 字节）"
            : $"期望 files×2，实际 kind={entry?.Kind ?? "无事件"} count={entry?.Files?.Count} err={entry?.Error}";

        Report(output, step);
        return step;
    }

    private static void Report(TextWriter output, Step step)
    {
        var newline = step.ReadMs > 0 ? $"  读取 {step.ReadMs}ms" : "";
        output.WriteLine($"  [{(step.Passed ? "通过" : "失败")}] {step.Name}{newline}");
        output.WriteLine($"         {step.Detail}");
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>诊断用：把当前剪贴板的真实内容描述出来，判断是“没写进去”还是“收不到通知”。</summary>
    private static string DescribeClipboardNow()
    {
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null)
            {
                return "GetDataObject=null（剪贴板空）";
            }

            var formats = data.GetFormats(false);
            var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
            var textDesc = text is null
                ? "无文本"
                : $"文本=\"{text[..Math.Min(18, text.Length)]}\"（{text.Length} 字）";
            return $"{textDesc}；格式=[{string.Join(",", formats)}]";
        }
        catch (Exception ex)
        {
            return $"读取异常：{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// WinForms 的 DataObject.SetImage 只吃 GDI+ 的 System.Drawing.Image，
    /// 而剪贴板读取这条链路用的是 WPF 的 BitmapSource——所以混用时要转一道。
    /// 走 PNG 编码转换，无损，不引入额外的图形依赖。
    /// </summary>
    private static System.Drawing.Bitmap ToGdiBitmap(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        return new System.Drawing.Bitmap(ms);
    }

    private static BitmapSource MakeBitmap(int width, int height)
    {
        // 画个对角线渐变，保证不同用例的像素内容不同（避免哈希去重把两次测试算成一条）
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                pixels[i + 0] = (byte)(x * 255 / Math.Max(1, width - 1));   // B
                pixels[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));  // G
                pixels[i + 2] = 0x40;                                        // R
                pixels[i + 3] = 0xFF;                                        // A
            }
        }

        var bmp = BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    private static void ClearClipboard()
    {
        try
        {
            System.Windows.Clipboard.Clear();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[自检] 清空剪贴板失败（不致命）：{ex.Message}");
        }

        Thread.Sleep(80);
    }

    /// <summary>剪贴板被别的进程占用（CLIPBRD_E_CANT_OPEN）是常态，必须重试。</summary>
    private static void SetWithRetry(Action action)
    {
        var delay = 60;
        Exception? last = null;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(delay);
                delay *= 2;
            }
        }

        throw new InvalidOperationException($"写入剪贴板失败（重试 4 次）：{last?.Message}", last);
    }

    /// <summary>
    /// 等到第一条满足条件的抓取记录。注意事件可能在 WaitFor 之前就到了，
    /// 所以先查一次 LastEntry 再进等待循环——否则会漏掉快速事件。
    /// </summary>
    private static (ProbeEntry? Entry, long WaitedMs) WaitFor(Func<ProbeEntry, bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var previous = Program.LastEntry;

        while (sw.Elapsed < timeout)
        {
            ClipboardEvent.Wait(60);
            ClipboardEvent.Reset();

            var current = Program.LastEntry;
            if (current is not null && !ReferenceEquals(current, previous) && predicate(current))
            {
                return (current, sw.ElapsedMilliseconds);
            }

            if (current is not null && predicate(current) && ReferenceEquals(current, previous))
            {
                // 事件在进入等待前就已经处理完了
                return (current, sw.ElapsedMilliseconds);
            }
        }

        return (null, sw.ElapsedMilliseconds);
    }

    private sealed class ClipboardBackup
    {
        public string? Text { get; init; }
        public BitmapSource? Image { get; init; }
        public string[]? Files { get; init; }
        public bool HasAnything => !string.IsNullOrEmpty(Text) || Image is not null || Files is { Length: > 0 };
    }

    private static ClipboardBackup SnapshotClipboard()
    {
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null)
            {
                return new ClipboardBackup();
            }

            string? text = null;
            BitmapSource? image = null;
            string[]? files = null;

            try
            {
                var list = System.Windows.Clipboard.GetFileDropList();
                if (list is { Count: > 0 })
                {
                    files = list.Cast<string>().Where(s => !string.IsNullOrEmpty(s)).ToArray();
                }
            }
            catch
            {
                // 忽略
            }

            try
            {
                image = System.Windows.Clipboard.GetImage();
            }
            catch
            {
                // 忽略
            }

            try
            {
                text = System.Windows.Clipboard.GetText();
            }
            catch
            {
                // 忽略
            }

            return new ClipboardBackup { Text = text, Image = image, Files = files };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[自检] 备份剪贴板失败（不致命）：{ex.Message}");
            return new ClipboardBackup();
        }
    }

    private static void RestoreClipboard(ClipboardBackup backup)
    {
        try
        {
            System.Windows.Clipboard.Clear();
            Thread.Sleep(60);

            if (!backup.HasAnything)
            {
                Console.WriteLine("[自检] 原剪贴板是空的，已恢复为空。");
                return;
            }

            // 只恢复可还原的部分：文件列表 > 图片 > 文本（按保真度从高到低）
            if (backup.Files is { Length: > 0 })
            {
                var sc = new System.Collections.Specialized.StringCollection();
                sc.AddRange(backup.Files);
                SetWithRetry(() => System.Windows.Clipboard.SetFileDropList(sc));
                Console.WriteLine($"[自检] 已恢复原剪贴板：{backup.Files.Length} 个文件");
                return;
            }

            if (backup.Image is not null)
            {
                SetWithRetry(() => System.Windows.Clipboard.SetImage(backup.Image));
                Console.WriteLine("[自检] 已恢复原剪贴板：图片");
                return;
            }

            if (!string.IsNullOrEmpty(backup.Text))
            {
                SetWithRetry(() => System.Windows.Clipboard.SetText(backup.Text));
                Console.WriteLine($"[自检] 已恢复原剪贴板：文本 {backup.Text.Length} 字");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[自检] 恢复剪贴板失败（不致命，剪贴板内容已丢）：{ex.Message}");
        }
    }
}
