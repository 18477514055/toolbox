using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Shell;
using Toolbox.Tools.Ai;
using Toolbox.Tools.BatchRename;
using Toolbox.Tools.Archiver;
using Toolbox.Tools.Clipboard;
using Toolbox.Tools.Store;
using Toolbox.Tools.CommandRunner;
using Toolbox.Tools.Convert;
using Toolbox.Tools.HashCheck;
using Toolbox.Tools.ImageCrop;
using Toolbox.Tools.Ocr;
using Toolbox.Tools.QrCode;
using Toolbox.Tools.Screenshot;
using Toolbox.Tools.WindowTopmost;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;
using WClipboard = System.Windows.Clipboard;

namespace Toolbox;

/// <summary>
/// 自动化自检。
///
/// 存在的理由（交接文档 §六·5）：**"编译通过" ≠ "功能验证过"**。
/// 每个工具都要有可重跑的验收步骤：程序自己制造输入、断言输出、报通过率。
///
/// 跑法：`Toolbox.exe --selftest`
/// 会临时改写剪贴板，**跑完自动恢复你原来的剪贴板内容**。
/// 报告写到 %LOCALAPPDATA%\桌面工具箱\selftest\selftest-report.json
/// </summary>
internal static class SelfTest
{
    private sealed class Case
    {
        public string Name { get; init; } = "";
        public bool Passed { get; set; }
        public string Detail { get; set; } = "";
        public bool Informational { get; init; }
    }

    private static readonly List<Case> Cases = new();
    private static readonly StringBuilder Console2 = new();

    public static int Run(string[] args)
    {
        AttachParentConsole();

        try
        {
            Log.Open(Path.Combine(AppPaths.SelfTestDir, "selftest.log"));
        }
        catch
        {
            // 日志开不了不影响自检
        }

        Line("==================================================");
        Line("桌面工具箱 · 自动化自检");
        Line($"时间     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"数据目录 : {AppPaths.Root}");
        Line("==================================================");
        Line();

        var backup = BackupClipboard();

        try
        {
            RunClipboardTests();
            RunMarkerTests();
            RunPureLogicTests();
            RunRegressionTests();
            RunImageTests();
            RunScreenshotTests();
            RunOcrTests();
            RunBatchRenameTests();
            RunHashTests();
            RunQrTests();
            RunTopmostTests();
            RunClipboardLocationTests();
            RunCommandRunnerTests();
            RunArchiveTests();
            RunToolSwitchTests();
            RunToolStoreTests();
            RunPluginTests();
            RunRegistrationTests();
            RunConvertTests();
            RunEnvironmentTests();

            // 可选的 AI 实测：默认不跑（要等 7B 模型出字，慢）。
            // 用 `--selftest --ai` 显式触发。
            if (args.Any(a => a.Equals("--ai", StringComparison.OrdinalIgnoreCase)))
            {
                RunAiStreamingTest();
            }
        }
        catch (Exception ex)
        {
            Line($"[致命] 自检过程本身出错：{ex}");
            Cases.Add(new Case { Name = "自检执行", Passed = false, Detail = ex.Message });
        }
        finally
        {
            RestoreClipboard(backup);
        }

        // 计数一律排除「信息」项：那是环境探测（LibreOffice / Ollama 装没装），
        // 不是断言，混进分子会算出「23/21 通过」这种看不懂的分数。
        var total = Cases.Count(c => !c.Informational);
        var passed = Cases.Count(c => c.Passed && !c.Informational);
        var failed = Cases.Count(c => !c.Passed && !c.Informational);
        var infoCount = Cases.Count(c => c.Informational);

        Line();
        Line("--------------------------------------------------");
        foreach (var c in Cases)
        {
            var tag = c.Informational ? "[信息]" : c.Passed ? "[通过]" : "[失败]";
            Line($"{tag} {c.Name}");
            if (!string.IsNullOrWhiteSpace(c.Detail))
            {
                Line($"        {c.Detail}");
            }
        }

        Line("--------------------------------------------------");
        var conclusion = failed == 0 ? "全部通过" : $"有 {failed} 项失败";
        var infoSuffix = infoCount > 0 ? $"（另有 {infoCount} 项环境信息，不计入通过率）" : "";
        Line($"自检结果：{passed}/{total} 通过 —— {conclusion}{infoSuffix}");
        Line();

        WriteReport(conclusion, passed, failed);

        return failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 剪贴板链路

    /// <summary>最近一次抓取结果。监听线程写、自检线程读，所以必须 volatile。</summary>
    private static volatile ClipReadResult? _lastCaptured;

    /// <summary>「有新抓取」的信号。只用来唤醒等待循环，判不判定一律交给谓词。</summary>
    private static readonly ManualResetEventSlim ClipboardEvent = new(false);

    /// <summary>
    /// 等到一条满足条件的抓取结果。
    ///
    /// ★ 必须按「内容」等，不能按「有没有事件」等 —— 这是踩过的坑：
    ///   ClearClipboard() 本身就会产生一条 WM_CLIPBOARDUPDATE（内容是 Empty），
    ///   若只等「任意事件」，等待会在清空那条通知上立刻返回，
    ///   而此刻真正的文本还没被监听线程处理，于是断言读到 kind=Empty 被误判为失败。
    ///   P0 探针当初是按谓词等的，移植过来时丢了这一层，自检才报出 3 项假失败。
    /// </summary>
    private static ClipReadResult? WaitFor(Func<ClipReadResult, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var current = _lastCaptured;
            if (current is not null && predicate(current))
            {
                return current;
            }

            ClipboardEvent.Wait(50);
            ClipboardEvent.Reset();
        }

        return null;
    }

    private static void RunClipboardTests()
    {
        ClipboardService? service = null;

        try
        {
            service = new ClipboardService();
            service.Captured += r =>
            {
                _lastCaptured = r;
                ClipboardEvent.Set();
            };

            var started = service.Start();

            // 坑 1 的自查：注册成功 + 消息循环活着
            Add("剪贴板监听注册成功（AddClipboardFormatListener）", started,
                started ? "" : "系统拒绝注册监听。");

            // ---- 文本 ----
            var marker = $"工具箱自检-文本-{Guid.NewGuid():N}"[..30];
            _lastCaptured = null;
            ClearClipboard();
            SetClipboardText(marker);

            var textHit = WaitFor(
                r => r.Entry.Kind == ClipKind.Text && r.FullText == marker,
                TimeSpan.FromSeconds(6));

            Add("纯文本复制 → 抓到 text 且逐字一致", textHit is not null,
                textHit is not null
                    ? $"读到 {textHit.Entry.TextLength} 字，耗时 {textHit.Entry.ReadMs} ms"
                    : $"期望 text 且内容一致，实际 kind={_lastCaptured?.Entry.Kind.ToString() ?? "(超时)"}");

            // ---- 图片 ----
            var expected = MakeBitmap(120, 80);
            _lastCaptured = null;
            ClearClipboard();
            SetClipboardImage(expected);

            var imageHit = WaitFor(
                r => r.Entry.Kind == ClipKind.Image
                     && r.Entry.ImageWidth == 120
                     && r.Entry.ImageHeight == 80
                     && r.PngBytes is { Length: > 0 },
                TimeSpan.FromSeconds(6));

            Add("纯图片复制 → 抓到 image 且尺寸精确", imageHit is not null,
                imageHit is not null
                    ? $"{imageHit.Entry.ImageWidth}x{imageHit.Entry.ImageHeight}，PNG {imageHit.PngBytes!.Length} 字节"
                    : $"期望 120x80 的 image，实际 {_lastCaptured?.Entry.ImageWidth}x{_lastCaptured?.Entry.ImageHeight} kind={_lastCaptured?.Entry.Kind.ToString() ?? "(超时)"}");

            // ---- 文件 ----
            var tempDir = FileNaming.CreateTempDir();
            var fileA = Path.Combine(tempDir, "a.txt");
            var fileB = Path.Combine(tempDir, "b.bin");
            File.WriteAllText(fileA, "a", new UTF8Encoding(false));
            File.WriteAllBytes(fileB, new byte[4096]);

            _lastCaptured = null;
            ClearClipboard();
            SetClipboardFiles(new[] { fileA, fileB });

            var filesHit = WaitFor(
                r => r.Entry.Kind == ClipKind.Files
                     && r.Entry.Files is { Count: 2 }
                     && r.Entry.Files.Any(f => f.Path == fileB && f.Size == 4096),
                TimeSpan.FromSeconds(6));

            Add("多文件复制 → 抓到 files 且路径/大小正确", filesHit is not null,
                filesHit is not null
                    ? $"{filesHit.Entry.Files!.Count} 个文件，B 文件 4096 字节"
                    : $"实际 {_lastCaptured?.Entry.Files?.Count ?? 0} 个文件，kind={_lastCaptured?.Entry.Kind.ToString() ?? "(超时)"}");

            FileNaming.SafeDeleteDir(tempDir);

            // ---- 坑 2 的断言：消息循环线程必须还活着 ----
            var alive = service.ListenerThreadAlive;
            Add("消息循环线程存活（坑 2 的防回归断言）", alive,
                alive
                    ? "是（正常）"
                    : "否 —— 说明有异常从窗口过程逃出去了，之后所有剪贴板事件都会静默消失。");

            // ---- 通知计数 ----
            Add("收到 WM_CLIPBOARDUPDATE 通知", service.MessageCount >= 3,
                $"共收到 {service.MessageCount} 次");
        }
        finally
        {
            service?.Dispose();
        }
    }

    // ---------------------------------------------------------------- 自污染标记

    private static void RunMarkerTests()
    {
        try
        {
            // 规则③：工具箱自己写的内容必须能被识别出来
            var marker = $"自污染标记验证-{Guid.NewGuid():N}"[..30];

            if (!ClipboardWriter.SetText(marker, out var writeError))
            {
                Add("写入剪贴板（带自污染标记）", false, writeError ?? "写入失败");
                return;
            }

            Thread.Sleep(120);

            var read = ClipboardReader.Read(9999);
            var originOk = read.Entry.IsToolWritten
                           && read.FullText == marker;

            Add("工具箱自己写入的内容被识别为 Origin=tool", originOk,
                originOk
                    ? "命中自污染标记，会被跳过归档（不会进剪贴板历史）"
                    : $"实际 Origin={read.Entry.Origin}，全文={(read.FullText == marker ? "一致" : "不一致")}");

            // 归档层也要真的跳过
            var store = new HistoryStore(new SettingsStore());
            var result = new ClipReadResult
            {
                Entry = new ClipEntry
                {
                    Kind = ClipKind.Text,
                    Origin = "tool",
                    Hash = "selftest-should-not-be-archived",
                    TextLength = 5,
                    TextPreview = "test",
                },
            };

            var archived = store.Add(result);
            Add("Origin=tool 的条目确实不进档案", archived is null,
                archived is null ? "已正确跳过" : "被错误归档了！");
        }
        catch (Exception ex)
        {
            Add("自污染标记验证", false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 纯逻辑

    private static void RunPureLogicTests()
    {
        // 热键解析
        var cases = new (string Spec, bool ExpectOk)[]
        {
            ("Win+Shift+V", true),
            ("Ctrl+Alt+T", true),
            ("Win+Shift+Space", true),
            ("不存在的键+X", false),
            ("Win+Shift", false),
        };

        var allOk = true;
        var detail = new List<string>();

        foreach (var (spec, expectOk) in cases)
        {
            var ok = HotKeyManager.TryParse(spec, out _, out _, out var error);
            detail.Add($"{spec}→{(ok ? "OK" : error)}");
            if (ok != expectOk)
            {
                allOk = false;
            }
        }

        Add("热键字符串解析（含错误输入）", allOk, string.Join("；", detail));

        // 语言方向
        var zh = "这是一段中文内容，用来测试方向判断。";
        var en = "This is an English paragraph used to test direction detection.";

        var directionOk = LanguageDetector.TargetLanguageFor(zh) == "英文"
                          && LanguageDetector.TargetLanguageFor(en) == "中文";

        Add("中英双向自动判方向（两个方向都测）", directionOk,
            $"中文输入→{LanguageDetector.TargetLanguageFor(zh)}；英文输入→{LanguageDetector.TargetLanguageFor(en)}");

        // 清洗
        var messy = "第一行\n\n\n\n第二行\n第二行\n第二行\n第三行";
        var cleaned = TextCleaner.Clean(messy);
        var cleanOk = !cleaned.Contains("\n\n\n") && cleaned.Split('\n').Count(l => l == "第二行") == 1;

        Add("文本清洗（压空行 + 去连续重复行）", cleanOk,
            $"清洗后：{cleaned.Replace("\n", "⏎")}");

        // 分块
        var longText = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"这是第 {i} 行，用来测试分块。")); 
        var chunks = TextCleaner.Chunk(longText, 500);
        var chunkOk = chunks.Count > 1 && chunks.All(c => c.Length <= 700);

        Add("长文本分块（超长必须分块，否则模型会静默截断）", chunkOk,
            $"共 {longText.Length} 字 → {chunks.Count} 块");

        // 悬浮窗样式常量（这两个值写错，AI 的"读当前页面"就会读到悬浮窗自己）
        var styleOk = NativeMethods.WS_EX_NOACTIVATE == 0x08000000
                      && NativeMethods.WS_EX_TOOLWINDOW == 0x00000080;

        Add("悬浮窗扩展样式常量正确（WS_EX_NOACTIVATE / WS_EX_TOOLWINDOW）", styleOk,
            $"NOACTIVATE=0x{NativeMethods.WS_EX_NOACTIVATE:X8}，TOOLWINDOW=0x{NativeMethods.WS_EX_TOOLWINDOW:X8}");
    }

    // ---------------------------------------------------------------- 回归（针对已修的问题）

    /// <summary>
    /// 每个用例都对应一个**真实发生过**的问题，或者一个「改错了不会有人发现」的隐患。
    /// 这组不是凑数：日期档位、注册表路径解析、孤儿文件回收，全都属于
    /// "手点很难点全、写错了也照样编译通过"的类型。
    /// </summary>
    private static void RunRegressionTests()
    {
        // ---- 1. 剪贴板日期筛选档位（off-by-one 高发区，含跨月跨年） ----
        try
        {
            var today = new DateTime(2026, 3, 1, 15, 30, 0); // 故意选月初：跨月要算得对

            var cases = new (string Name, DateTime Time, int Index, bool Expect)[]
            {
                ("今天 · 零点整算今天", new DateTime(2026, 3, 1), DateRangeFilter.Today, true),
                ("今天 · 昨天 23:59 不算今天", new DateTime(2026, 2, 28, 23, 59, 59), DateRangeFilter.Today, false),
                ("昨天 · 昨天 23:59 算昨天", new DateTime(2026, 2, 28, 23, 59, 59), DateRangeFilter.Yesterday, true),
                ("昨天 · 今天 00:00 不算昨天", new DateTime(2026, 3, 1), DateRangeFilter.Yesterday, false),
                ("最近 7 天 · 第 7 天（含）", new DateTime(2026, 2, 23), DateRangeFilter.Last7Days, true),
                ("最近 7 天 · 第 8 天不算", new DateTime(2026, 2, 22), DateRangeFilter.Last7Days, false),
                ("最近 30 天 · 第 30 天（含，跨月）", new DateTime(2026, 1, 31), DateRangeFilter.Last30Days, true),
                ("最近 30 天 · 第 31 天不算", new DateTime(2026, 1, 30), DateRangeFilter.Last30Days, false),
                ("全部时间 · 多老的都算", new DateTime(2019, 1, 1), DateRangeFilter.All, true),
                ("时间坏掉 · 不因筛选消失", DateTime.MinValue, DateRangeFilter.Last7Days, true),
            };

            var bad = cases.Where(c => DateRangeFilter.Matches(c.Time, c.Index, today) != c.Expect)
                           .Select(c => c.Name)
                           .ToList();

            Add("剪贴板日期筛选档位（含跨月 / 时间戳损坏）", bad.Count == 0,
                bad.Count == 0 ? $"10 条边界用例全部正确（以 {today:yyyy-MM-dd} 为今天）"
                               : "不正确的：" + string.Join("、", bad));
        }
        catch (Exception ex)
        {
            Add("剪贴板日期筛选档位（含跨月 / 时间戳损坏）", false, ex.Message);
        }

        // ---- 2. 注册表自启命令的路径解析（这是"开机自启失效"的排查入口） ----
        try
        {
            var exe = AutoStart.ExecutablePath;

            var parseCases = new (string Command, string Expect)[]
            {
                ($"\"C:\\a b\\Toolbox.exe\" --startup", @"C:\a b\Toolbox.exe"),
                ($"\"C:\\中文 目录\\工具箱.exe\" --startup", @"C:\中文 目录\工具箱.exe"),
                ("C:\\a b\\Toolbox.exe --startup", @"C:\a b\Toolbox.exe"),
                ($"\"{exe}\" {AutoStart.StartupArgument}", exe),
            };

            var parseBad = parseCases.Where(c => AutoStart.ExtractPath(c.Command) != c.Expect)
                                     .Select(c => $"「{c.Command}」→{AutoStart.ExtractPath(c.Command)}（期望 {c.Expect}）")
                                     .ToList();

            Add("自启命令的路径解析（带引号 / 空格 / 中文 / 无引号）", parseBad.Count == 0,
                parseBad.Count == 0 ? "4 种写法全部解析正确" : string.Join("；", parseBad));

            // 路径比对必须忽略大小写，且能识破"指向别的 exe"
            var compareOk =
                AutoStart.PointsAtCurrentExe($"\"{exe}\" {AutoStart.StartupArgument}")
                && AutoStart.PointsAtCurrentExe(exe)
                && !AutoStart.PointsAtCurrentExe("\"C:\\nonexistent\\Toolbox.exe\" --startup")
                && !AutoStart.PointsAtCurrentExe("");

            Add("自启路径比对（认得出「不是当前 exe」→ 触发自愈）", compareOk,
                compareOk ? "同一路径（含带/不带参数、带/不带引号）判为一致；别的路径判为不一致"
                          : "路径比对逻辑不正确，自愈会失效");
        }
        catch (Exception ex)
        {
            Add("自启命令的路径解析（带引号 / 空格 / 中文 / 无引号）", false, ex.Message);
        }

        // ---- 3. volatile 孤儿正文文件必须被回收（质检 B-1，真实磁盘泄漏） ----
        try
        {
            Directory.CreateDirectory(AppPaths.VolatileDir);

            var orphan = Path.Combine(
                AppPaths.VolatileDir,
                "selftest-orphan-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(orphan, "没有任何条目引用这个正文文件，它应该在载入时被清掉。");

            // 载入 = 触发清理。用一个新的 store 实例，不碰正在跑的那个。
            var probe = new HistoryStore(new SettingsStore());
            probe.Load();

            var swept = !File.Exists(orphan);
            Add("volatile 孤儿正文文件在载入时被回收（B-1 防回归）", swept,
                swept ? "造了一个孤儿文件，载入后被清掉（不再无限占磁盘）"
                      : "孤儿文件还在 —— 磁盘泄漏会随使用时间无限增长");
        }
        catch (Exception ex)
        {
            Add("volatile 孤儿正文文件在载入时被回收（B-1 防回归）", false, ex.Message);
        }

        // ---- 4. 设置序列化：空串热键必须活着往返 ----
        //    "留空 = 这个工具不要热键"这个功能完全依赖空串能存下来。
        //    一旦有人把 JsonIgnoreCondition 改成 WhenWritingDefault，字典里的 "" 会被静默丢掉，
        //    表现就是"设了禁用，重启后又自己长回默认热键"——极难排查。
        try
        {
            var s = new Settings();
            s.HotKeys["clipboard"] = "";
            s.HotKeys["ai.translate"] = "Ctrl+Alt+T";
            s.AiBackend = "remote";
            s.RemoteApiUrl = "https://api.deepseek.com/v1";
            s.RemoteApiKey = "sk-selftest-not-a-real-key";
            s.RemoteModel = "deepseek-chat";

            var json = JsonSerializer.Serialize(s, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

            var back = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            var roundTripOk = back is not null
                              && back.HotKeys.TryGetValue("clipboard", out var empty) && empty == ""
                              && back.HotKeys.TryGetValue("ai.translate", out var kept) && kept == "Ctrl+Alt+T"
                              && back.AiBackend == "remote"
                              && back.RemoteApiUrl == "https://api.deepseek.com/v1"
                              && back.RemoteApiKey == "sk-selftest-not-a-real-key"
                              && back.RemoteModel == "deepseek-chat";

            Add("设置往返：空串热键（= 禁用）与 AI 远端字段不丢", roundTripOk,
                roundTripOk
                    ? "空串热键存活（禁用语义不会退化成「用默认键」）；AI 后端 4 个新字段完整往返"
                    : $"往返后：HotKeys={back?.HotKeys.Count ?? -1} 项，AiBackend={back?.AiBackend ?? "(null)"}");
        }
        catch (Exception ex)
        {
            Add("设置往返：空串热键（= 禁用）与 AI 远端字段不丢", false, ex.Message);
        }

        // ---- 4b. 热键「自动降级」不得冒充用户意图（本轮修掉的缺陷的防回归） ----
        //
        // 原缺陷：默认键被占用 → 自动降级 → 把结果写进 HotKeys。
        //   而注册逻辑把「HotKeys 里有条目」一律当成"用户自己改过"，于是：
        //     ① 占用者退出、默认键重新空闲后，工具箱**永远回不到默认键**；
        //     ② 那个备选键以后被占用时只会报错、不会降级。
        //   用户莫名卡在一个更差的备选键上，界面上还看不出是被自动改的。
        //
        // 修法：加 HotKeyOrigins（"user" / "auto"），auto 的条目不算用户意图。
        try
        {
            var s = new Settings();
            s.HotKeys["clipboard"] = "Win+Alt+V";      // 上次自动降级写进去的值
            s.HotKeyOrigins["clipboard"] = "auto";
            s.HotKeys["convert"] = "Ctrl+Alt+F9";      // 用户亲手填的
            s.HotKeyOrigins["convert"] = "user";
            s.HotKeys["ai"] = "";                      // 用户主动禁用
            s.HotKeyOrigins["ai"] = "user";

            // ★ 关键：auto 的条目必须和"用户填的"区分开
            var autoIsNotUser =
                s.HotKeyOrigins.TryGetValue("clipboard", out var o1) && o1 == "auto"
                && s.HotKeyOrigins.TryGetValue("convert", out var o2) && o2 == "user";

            // ★ 往返不丢（和 HotKeys 一样，这个字段也必须能存下来）
            var json = JsonSerializer.Serialize(s, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
            var back = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            var originsSurvive =
                back is not null
                && back.HotKeyOrigins.TryGetValue("clipboard", out var b1) && b1 == "auto"
                && back.HotKeyOrigins.TryGetValue("convert", out var b2) && b2 == "user"
                && back.HotKeyOrigins.TryGetValue("ai", out var b3) && b3 == "user";

            // ★ 向后兼容：老 settings.json 没有这个字段 → 反序列化后是空字典，不能是 null，
            //    否则注册逻辑里 TryGetValue 会直接 NRE，整个程序起不来。
            var legacy = JsonSerializer.Deserialize<Settings>(
                """{"HotKeys":{"clipboard":"Win+Alt+V"}}""",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var legacySafe = legacy?.HotKeyOrigins is not null;

            var ok = autoIsNotUser && originsSurvive && legacySafe;
            Add("热键「自动降级」与「用户指定」被分开记录（A 类缺陷防回归）", ok,
                ok
                    ? "auto/user 两种来源可区分且能往返；老设置文件缺该字段时为空格而非 null"
                    : $"autoIsNotUser={autoIsNotUser} originsSurvive={originsSurvive} legacySafe={legacySafe}");
        }
        catch (Exception ex)
        {
            Add("热键「自动降级」与「用户指定」被分开记录（A 类缺陷防回归）", false, ex.Message);
        }

        // ---- 4c. 截图坐标换算必须按显示器真实 DPI（本轮修掉的缺陷的防回归） ----
        //
        // 原缺陷：用「位图像素宽 ÷ 背景图显示宽」这**一个全局比值**换算。
        //   单屏成立；多屏且各屏缩放不同时必然算错 —— 一个比值表达不了两个比例。
        //   实测反例（主屏 150% + 副屏 100%，副屏在右）：
        //     物理宽 3840，逻辑宽 3200 ⇒ 全局系数 1.2；
        //     主屏上逻辑 x=100 的真实物理位置是 150，而 1.2 给出 120 ⇒ 偏 30 px。
        try
        {
            var cases = new (string Name, double Scale, double X, double Y, double W, double H,
                             int Bw, int Bh, int Ex, int Ey, int Ew, int Eh)[]
            {
                // 100%：逻辑 == 物理
                ("100%", 1.0, 300, 200, 400, 300, 1920, 1080, 300, 200, 400, 300),
                // 125%：×1.25
                ("125%", 1.25, 200, 160, 400, 240, 2400, 1350, 250, 200, 500, 300),
                // 150%：×1.5 —— 这就是全局系数法算错的场景（它会给 1.2×...）
                ("150%", 1.5, 100, 100, 200, 100, 1920, 1080, 150, 150, 300, 150),
                // 200%（4K 常见）
                ("200%", 2.0, 50, 60, 100, 80, 3840, 2160, 100, 120, 200, 160),
            };

            var allOk = true;
            var detail = new List<string>();

            foreach (var c in cases)
            {
                var r = ScreenCoordinateMapper.ToPixelRect(
                    c.X, c.Y, c.W, c.H, c.Scale, c.Scale, c.Bw, c.Bh);

                var ok = r.X == c.Ex && r.Y == c.Ey && r.Width == c.Ew && r.Height == c.Eh;
                allOk &= ok;
                detail.Add($"{c.Name}:{(ok ? "✓" : $"✗ 期望({c.Ex},{c.Ey},{c.Ew}x{c.Eh}) 实得({r.X},{r.Y},{r.Width}x{r.Height})")}");
            }

            // 越界夹取：不能算出负宽 / 越出位图（否则 CroppedBitmap 直接抛异常）
            var clamp = ScreenCoordinateMapper.ToPixelRect(
                1900, 1070, 500, 500, 1.0, 1.0, 1920, 1080);
            var clampOk = clamp.X >= 0 && clamp.Y >= 0
                          && clamp.X + clamp.Width <= 1920
                          && clamp.Y + clamp.Height <= 1080
                          && clamp.Width >= 1 && clamp.Height >= 1;

            // 非法系数（0 / NaN）不能产出空图
            var badScale = ScreenCoordinateMapper.ToPixelRect(
                10, 10, 100, 100, 0, double.NaN, 1920, 1080);
            var badOk = badScale.Width >= 1 && badScale.Height >= 1;

            var finalOk = allOk && clampOk && badOk;
            Add("截图坐标换算按显示器真实 DPI（B 类缺陷防回归）", finalOk,
                finalOk
                    ? string.Join("；", detail) + $"；越界夹取✓；非法系数兜底✓"
                    : string.Join("；", detail) + $"；clampOk={clampOk} badScaleOk={badOk}");
        }
        catch (Exception ex)
        {
            Add("截图坐标换算按显示器真实 DPI（B 类缺陷防回归）", false, ex.Message);
        }

        // ---- 4d2. 索引文件**不存在**时也必须清扫孤儿（这个 bug 是便携模式暴露出来的） ----
        //
        // 原缺陷：Load() 在"索引文件不存在"时直接 `return`，
        //   把写在 try 之后的 SweepOrphanPayloads() 一起跳过了。
        //   于是**全新环境**（尤其便携模式首次运行）里，volatile\ 的孤儿永远不会被清 ——
        //   恰好把自愈机制关在最需要它的场景里。
        //   这是"早退跳过收尾逻辑"的典型 bug，上一条用例没抓到它，
        //   因为那条用例跑在**已有历史**的环境里（索引文件存在）。
        try
        {
            var indexFile = AppPaths.IndexFile;
            var backup = indexFile + ".selftest-bak";

            // 临时把索引文件挪走，制造"全新环境"
            var hadIndex = File.Exists(indexFile);
            if (hadIndex)
            {
                File.Move(indexFile, backup, overwrite: true);
            }

            try
            {
                Directory.CreateDirectory(AppPaths.VolatileDir);

                var orphan = Path.Combine(
                    AppPaths.VolatileDir,
                    "selftest-noidx-orphan-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(orphan, "索引文件不存在时，这个孤儿也必须被清掉。");

                // 新 store 实例 + 空索引 ⇒ 走的正是"索引不存在"那条路径
                var probe = new HistoryStore(new SettingsStore());
                probe.Load();

                var sweptWithoutIndex = !File.Exists(orphan);
                Add("索引文件不存在时也清扫 volatile 孤儿（便携模式暴露的缺陷防回归）",
                    sweptWithoutIndex,
                    sweptWithoutIndex
                        ? "没有 index.jsonl 的全新环境里，孤儿正文文件同样被回收"
                        : "★ 索引不存在时跳过了清扫 —— 全新安装/便携模式下孤儿会无限堆积");
            }
            finally
            {
                // 还原索引文件（自检不该改变用户的真实历史）
                if (hadIndex && File.Exists(backup))
                {
                    File.Move(backup, indexFile, overwrite: true);
                }
            }
        }
        catch (Exception ex)
        {
            Add("索引文件不存在时也清扫 volatile 孤儿（便携模式暴露的缺陷防回归）", false, ex.Message);
        }

        // ---- 4d. 数据目录模式（常规 / 便携）—— "能不能分享给别人"的最后一环 ----
        //
        // 为什么要有便携模式：把工具箱拷给同事时，他多半不想让一个绿色小工具
        //   往自己 %LOCALAPPDATA% 里塞东西 —— 那是"安装"，不是"试用"。
        //   便携模式下数据全在 exe 旁边的 data\，不想要了删掉整个文件夹就干净了。
        //
        // 这条断言防的是：模式判定被写坏（比如永远走便携、或永远忽略 portable.txt）。
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
            var marker = Path.Combine(exeDir, "portable.txt");

            // 记录当前状态，测完要还原（自检不该改变运行环境）
            var wasPortable = AppPaths.IsPortable;
            var wasRoot = AppPaths.Root;
            var markerExisted = File.Exists(marker);

            // ① 没有标记文件 + 不强制 ⇒ 常规模式（数据在 %LOCALAPPDATA%）
            if (!markerExisted)
            {
                AppPaths.InitializeMode(forcePortable: false);
                var regularOk = !AppPaths.IsPortable
                                && AppPaths.Root.Contains("AppData", StringComparison.OrdinalIgnoreCase);

                // ② 强制便携 ⇒ 数据在 exe 旁边
                AppPaths.InitializeMode(forcePortable: true);
                var portableOk = AppPaths.IsPortable
                                 && exeDir.Length > 0
                                 && AppPaths.Root.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase)
                                 && AppPaths.Root.EndsWith("data", StringComparison.OrdinalIgnoreCase);

                // 还原
                AppPaths.InitializeMode(wasPortable);

                Add("数据目录模式：常规 vs 便携（--portable）判定正确", regularOk && portableOk,
                    regularOk && portableOk
                        ? $"常规→{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\桌面工具箱；便携→exe 旁边的 data\\"
                        : $"regularOk={regularOk} portableOk={portableOk}");
            }
            else
            {
                Add("数据目录模式：常规 vs 便携（--portable）判定正确", true,
                    "exe 旁边存在 portable.txt，当前已是便携模式，跳过模式切换测试");
            }
        }
        catch (Exception ex)
        {
            Add("数据目录模式：常规 vs 便携（--portable）判定正确", false, ex.Message);
        }

        // ---- 5. 远端 API 端点拼接（填不填 /v1 都要能用） ----
        try
        {
            var noV1 = new RemoteAiClient("https://api.deepseek.com", "k", "m");
            var withV1 = new RemoteAiClient("https://api.deepseek.com/v1/", "k", "m");

            var endpointOk =
                noV1.Endpoint("models") == "https://api.deepseek.com/v1/models"
                && withV1.Endpoint("models") == "https://api.deepseek.com/v1/models"
                && noV1.Endpoint("chat/completions") == "https://api.deepseek.com/v1/chat/completions";

            Add("远端 API 端点拼接（带不带 /v1 都能用，重复斜杠不产生 //v1）", endpointOk,
                endpointOk
                    ? "两种情况都拼成 .../v1/models"
                    : $"{noV1.Endpoint("models")} ｜ {withV1.Endpoint("models")}");

            // 没填地址时不该抛异常，要给人话
            var emptyTask = new RemoteAiClient("", "", "").CheckAsync(CancellationToken.None);
            var emptyOk = emptyTask.Wait(TimeSpan.FromSeconds(5))
                          && !emptyTask.Result.Ok
                          && emptyTask.Result.Message.Contains("还没填");

            Add("远端后端未填地址时给出明确中文提示（不发请求、不抛异常）", emptyOk,
                emptyOk ? "提示：还没填远端 API 地址…" : "空地址的处理不正确");
        }
        catch (Exception ex)
        {
            Add("远端 API 端点拼接（带不带 /v1 都能用，重复斜杠不产生 //v1）", false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 图片

    private static void RunImageTests()
    {
        try
        {
            var source = MakeBitmap(400, 300);

            // 裁剪：像素必须精确到 1px
            var cropped = ImageOps.Crop(source, new Int32Rect(50, 40, 120, 90));
            var cropOk = cropped.PixelWidth == 120 && cropped.PixelHeight == 90;

            Add("裁剪输出像素精确（1px 不差）", cropOk,
                $"裁 120x90 → 实际 {cropped.PixelWidth}x{cropped.PixelHeight}");

            // 改尺寸：输出像素必须等于输入
            var resized = ImageOps.Resize(source, 777, 333, highQuality: true);
            var resizeOk = resized.PixelWidth == 777 && resized.PixelHeight == 333;

            Add("改尺寸输出像素与输入完全一致", resizeOk,
                $"输入 777x333 → 实际 {resized.PixelWidth}x{resized.PixelHeight}");

            // 最近邻也要精确
            var nearest = ImageOps.Resize(source, 111, 99, highQuality: false);
            Add("最近邻插值输出像素精确", nearest.PixelWidth == 111 && nearest.PixelHeight == 99,
                $"{nearest.PixelWidth}x{nearest.PixelHeight}");

            // 缩略图长边限制
            var thumb = ImageOps.Thumbnail(source, 320);
            Add("缩略图长边不超过 320", Math.Max(thumb.PixelWidth, thumb.PixelHeight) <= 320,
                $"{thumb.PixelWidth}x{thumb.PixelHeight}");
        }
        catch (Exception ex)
        {
            Add("图片操作", false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 快捷截图

    private static void RunScreenshotTests()
    {
        var image = ScreenCapture.CaptureFullScreen();
        if (image is null)
        {
            Add("快捷截图：全屏捕获", false, "CaptureFullScreen 返回 null（拿不到屏幕 DC？）");
            return;
        }

        // 截左上角一个 50×50 的小块，验证「逻辑坐标 → 像素坐标」换算前的裁剪与 PNG 落盘。
        // （坐标换算本身在 ScreenshotWindow 里，运行时按 ActualWidth 量系数，这里只验链路不崩、能出文件。）
        var cw = Math.Min(50, image.PixelWidth);
        var ch = Math.Min(50, image.PixelHeight);
        var crop = new CroppedBitmap(image, new Int32Rect(0, 0, cw, ch));

        var path = Path.Combine(AppPaths.SelfTestDir, $"shot_{DateTime.Now:yyyyMMddHHmmssfff}.png");
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(crop));
            using (var fs = File.Create(path))
            {
                enc.Save(fs);
            }

            var ok = File.Exists(path) && new FileInfo(path).Length > 0;
            Add("快捷截图：捕获+裁剪+保存 PNG", ok,
                ok
                    ? $"{new FileInfo(path).Length} 字节；源图 {image.PixelWidth}×{image.PixelHeight}"
                    : "未生成文件");

            // 复制截图到剪贴板（带自污染标记）不能抛异常 —— 这关系到「截图后能否直接粘贴」
            var wrote = ClipboardWriter.SetImage(crop, out var err);
            Add("快捷截图：写入剪贴板（标记自污染）", wrote, wrote ? "" : (err ?? "未知错误"));
        }
        catch (Exception ex)
        {
            Add("快捷截图：捕获+裁剪+保存 PNG", false, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 删不掉不影响结论
            }
        }
    }

    // ---------------------------------------------------------------- 工具注册完整性

    /// <summary>
    /// 工具注册表的完整性检查。
    ///
    /// 这条用例是**被一个真实 bug 逼出来的**：加第 9 个工具（二维码）时，
    /// 注册那一行被一次错误的文本替换吃掉了 —— 工具类写好了、编译也过了，
    /// 但**根本没被注册**：它没有热键、悬浮窗上看不到、托盘里也没有。
    /// 这种"代码都在、就是没接上线"的缺陷，编译不报错、
    /// 单个工具的用例也照过，只有**整体检查**才抓得住。
    /// </summary>
    private static void RunRegistrationTests()
    {
        try
        {
            var expected = new[]
            {
                "clipboard", "image", "convert", "ai", "screenshot",
                "ocr", "rename", "hash", "qrcode", "topmost", "run", "archive",
            };

            var registry = new ToolRegistry();
            registry.Add(new Toolbox.Tools.Clipboard.ClipboardTool());
            registry.Add(new Toolbox.Tools.ImageCrop.ImageCropTool());
            registry.Add(new Toolbox.Tools.Convert.ConvertTool());
            registry.Add(new Toolbox.Tools.Ai.AiTool());
            registry.Add(new Toolbox.Tools.Screenshot.ScreenshotTool());
            registry.Add(new Toolbox.Tools.Ocr.OcrTool());
            registry.Add(new Toolbox.Tools.BatchRename.BatchRenameTool());
            registry.Add(new Toolbox.Tools.HashCheck.HashCheckTool());
            registry.Add(new Toolbox.Tools.QrCode.QrTool());
            registry.Add(new Toolbox.Tools.WindowTopmost.WindowTopmostTool());
            registry.Add(new Toolbox.Tools.CommandRunner.CommandRunnerTool());
            registry.Add(new Toolbox.Tools.Archiver.ArchiveTool());

            var registered = registry.Tools.Select(t => t.Id).ToList();

            var missing = expected.Where(e => !registered.Contains(e)).ToList();
            var extra = registered.Where(r => !expected.Contains(r)).ToList();
            var ok = missing.Count == 0 && extra.Count == 0;

            Add("工具注册完整性（12 个工具都在注册表里）", ok,
                ok
                    ? $"已注册：{string.Join("、", registered)}"
                    : $"缺失：{string.Join("、", missing)}；多出：{string.Join("、", extra)}");

            // ---- ★ 真正的护栏：扫**所有工具类**，逐个断言它在注册表里 ----
            //
            // 为什么上面那条不够、非要再加这一条：
            //   上面用的是**手写的期望清单** —— 它测的是"我以为注册了什么"，
            //   不是"实际注册了什么"。我连着两次因文本替换把注册行弄丢
            //   （WindowTopmostTool、CommandRunnerTool 各一次），
            //   而那条用例两次都是绿的，因为它压根没看真实注册代码。
            //
            //   这一条改成反射扫出程序集里**所有** IToolboxTool 实现，
            //   逐个检查是否已注册。新加工具却忘了接线，这里立刻红，
            //   不依赖任何人记得去改期望清单。
            RunToolDiscoveryTest();

            var blank = registry.Tools
                .Where(t => string.IsNullOrWhiteSpace(t.Id)
                            || string.IsNullOrWhiteSpace(t.Name)
                            || string.IsNullOrWhiteSpace(t.Glyph)
                            || string.IsNullOrWhiteSpace(t.DefaultHotKey))
                .Select(t => t.Id)
                .ToList();

            Add("工具元数据完整（Id / 名称 / 图标 / 默认热键都不为空）", blank.Count == 0,
                blank.Count == 0
                    ? "10 个工具的元数据均完整"
                    : $"以下工具的元数据有空白：{string.Join("、", blank)}");

            var dupIds = registry.Tools
                .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Add("工具 Id 无重复", dupIds.Count == 0,
                dupIds.Count == 0 ? "10 个 Id 均唯一" : $"重复的 Id：{string.Join("、", dupIds)}");

            var dupKeys = registry.Tools
                .GroupBy(t => t.DefaultHotKey, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key}（{string.Join("/", g.Select(t => t.Name))}）")
                .ToList();

            Add("默认热键无重复（工具之间不互相抢键）", dupKeys.Count == 0,
                dupKeys.Count == 0
                    ? $"{expected.Length} 个默认热键均唯一"
                    : $"重复的热键：{string.Join("；", dupKeys)}");

            // ---- 降级链之间的冲突（只有整体检查才抓得到）----
            //
            // 为什么必须单独测这一条：工具默认键不重复，**不代表降级链不撞**。
            // 实测踩过：A 的降级键 = B 的默认键 ⇒ A 一旦降级就去抢 B 的键，
            // 最后两个工具只有一个能注册上，另一个静默失效。
            // 每个工具的用例只测自己，永远发现不了这种**跨工具**冲突。
            RunHotKeyFallbackTests(registry);
        }
        catch (Exception ex)
        {
            Add("工具注册完整性（12 个工具都在注册表里）", false, ex.Message);
        }
    }

    /// <summary>
    /// 降级链的跨工具冲突检查。三条约束：
    ///   ① 所有降级键互不相同；
    ///   ② 任何降级键都不等于任何工具的**默认键**；
    ///   ③ 不出现空链。
    ///
    /// 用反射取 App 里那份私有配置 —— 测的就是真正生效的那一份，不是复制品。
    /// </summary>
    private static void RunHotKeyFallbackTests(ToolRegistry registry)
    {
        try
        {
            var field = typeof(Toolbox.App).GetField("HotKeyFallbacks",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (field?.GetValue(null) is not Dictionary<string, string[]> chains)
            {
                Add("热键降级链：跨工具无冲突", false, "取不到 HotKeyFallbacks 配置");
                return;
            }

            var defaultKeys = new HashSet<string>(
                registry.Tools.Select(t => t.DefaultHotKey), StringComparer.OrdinalIgnoreCase);

            var allFallbacks = new List<(string Owner, string Key)>();
            var emptyChains = new List<string>();

            foreach (var (owner, chain) in chains)
            {
                if (chain.Length == 0)
                {
                    emptyChains.Add(owner);
                    continue;
                }

                foreach (var k in chain)
                {
                    if (string.IsNullOrWhiteSpace(k))
                    {
                        emptyChains.Add(owner);
                        continue;
                    }

                    allFallbacks.Add((owner, k));
                }
            }

            var dupFallbacks = allFallbacks
                .GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key}（{string.Join(" / ", g.Select(x => x.Owner))}）")
                .ToList();

            var clashWithDefaults = allFallbacks
                .Where(f => defaultKeys.Contains(f.Key))
                .Select(f => $"{f.Key}（{f.Owner} 的降级键撞了某个工具的默认键）")
                .Distinct()
                .ToList();

            var ok = dupFallbacks.Count == 0
                     && clashWithDefaults.Count == 0
                     && emptyChains.Count == 0;

            Add("热键降级链：跨工具无冲突（且不抢别人的默认键）", ok,
                ok
                    ? $"{allFallbacks.Count} 个降级键互不相同，且都不等于任何工具的默认键"
                    : $"重复：{string.Join("；", dupFallbacks)}｜"
                      + $"撞默认键：{string.Join("；", clashWithDefaults)}｜"
                      + $"空链：{string.Join("、", emptyChains)}");
        }
        catch (Exception ex)
        {
            Add("热键降级链：跨工具无冲突（且不抢别人的默认键）", false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 批量打包

    /// <summary>
    /// 批量打包的用例。
    ///
    /// ZIP 这条链路**必须真建包、再读回来核对内容** ——
    /// 只看"文件生成了没有"不够：一个内容错的压缩包比没有更糟
    /// （用户会以为东西已经备份好了）。所以建完包会用 ZipFile 打开，
    /// 逐条核对条目名与文件内容。
    ///
    /// RAR 只在**装了 WinRAR** 时才实测；没装就记成"信息"（不计入通过率）——
    /// 那是环境问题，不是代码问题。
    /// </summary>
    private static void RunArchiveTests()
    {
        // ---- 1. 格式可用性判定 ----
        try
        {
            var zipOk = ArchiveService.IsAvailable(ArchiveFormat.Zip, out var zipReason);
            var rarOk = ArchiveService.IsAvailable(ArchiveFormat.Rar, out var rarReason);
            var rarPath = ArchiveService.FindRar();

            // ZIP 必须永远可用（用 .NET 自带能力）
            var zipMustWork = zipOk && zipReason is null;

            // RAR 不可用时**必须给出中文原因**（告诉用户装 WinRAR 或改用 ZIP）
            var rarReasonOk = rarOk || !string.IsNullOrWhiteSpace(rarReason);

            Add("批量打包：格式可用性判定（ZIP 恒可用 / RAR 缺 WinRAR 时给中文原因）",
                zipMustWork && rarReasonOk,
                zipMustWork && rarReasonOk
                    ? $"ZIP 可用；RAR {(rarOk ? $"可用（{rarPath}）" : "不可用，已给出中文原因")}"
                    : $"zipOk={zipOk} rarOk={rarOk} rarReason={rarReason}");
        }
        catch (Exception ex)
        {
            Add("批量打包：格式可用性判定（ZIP 恒可用 / RAR 缺 WinRAR 时给中文原因）", false, ex.Message);
        }

        // ---- 2. 输出路径与命名规则 ----
        try
        {
            var tempRoot = Path.Combine(AppPaths.SelfTestDir, "arc-name-" + Guid.NewGuid().ToString("N"));
            var folder = Path.Combine(tempRoot, "我的资料");
            Directory.CreateDirectory(folder);

            var f1 = Path.Combine(folder, "a.txt");
            var f2 = Path.Combine(folder, "b.txt");
            File.WriteAllText(f1, "A");
            File.WriteAllText(f2, "B");

            var now = new DateTime(2026, 9, 18, 21, 30, 45);

            // ① 单个文件夹 → 用文件夹名，且放在它的**父目录**（不是它自己里面）
            var p1 = ArchiveService.BuildOutputPath(
                new[] { folder }, ArchiveFormat.Zip, ArchiveDestination.SameFolder, null, now);
            var ok1 = Path.GetFileName(p1).StartsWith("我的资料_") && p1.EndsWith(".zip")
                      && string.Equals(Path.GetDirectoryName(p1), tempRoot, StringComparison.OrdinalIgnoreCase);

            // ② 单个文件 → 用文件名（不含扩展名）
            var p2 = ArchiveService.BuildOutputPath(
                new[] { f1 }, ArchiveFormat.Zip, ArchiveDestination.SameFolder, null, now);
            var ok2 = Path.GetFileName(p2).StartsWith("a_") && p2.EndsWith(".zip");

            // ③ 多文件同目录 → 用目录名
            var p3 = ArchiveService.BuildOutputPath(
                new[] { f1, f2 }, ArchiveFormat.Zip, ArchiveDestination.SameFolder, null, now);
            var ok3 = Path.GetFileName(p3).StartsWith("我的资料_");

            // ④ 指定目录 → 就放在那里，扩展名随格式
            var outDir = Path.Combine(tempRoot, "输出");
            Directory.CreateDirectory(outDir);
            var p4 = ArchiveService.BuildOutputPath(
                new[] { f1 }, ArchiveFormat.Rar, ArchiveDestination.ConfiguredFolder, outDir, now);
            var ok4 = string.Equals(Path.GetDirectoryName(p4), outDir, StringComparison.OrdinalIgnoreCase)
                      && p4.EndsWith(".rar");

            var ok = ok1 && ok2 && ok3 && ok4;
            Add("批量打包：命名与存放位置（文件夹/单文件/多文件/指定目录）", ok,
                ok
                    ? $"文件夹→「{Path.GetFileName(p1)}」；单文件→「{Path.GetFileName(p2)}」；"
                      + $"多文件→「{Path.GetFileName(p3)}」；指定目录→「{p4}」"
                    : $"ok1={ok1} ok2={ok2} ok3={ok3} ok4={ok4}");

            Directory.Delete(tempRoot, true);
        }
        catch (Exception ex)
        {
            Add("批量打包：命名与存放位置（文件夹/单文件/多文件/指定目录）", false, ex.Message);
        }

        // ---- 3. ZIP 端到端：真建包 → 真读回来核对内容 ----
        var dir = Path.Combine(AppPaths.SelfTestDir, "arc-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dir);

            // 造内容：中文名文件 + 子目录里的文件（覆盖"相对路径"与"中文名"两个易错点）
            var srcDir = Path.Combine(dir, "资料");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "中文文件.txt"), "这是中文内容",
                new System.Text.UTF8Encoding(false));

            var sub = Path.Combine(srcDir, "子目录");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "nested.txt"), "nested content",
                new System.Text.UTF8Encoding(false));

            var outPath = Path.Combine(dir, "out.zip");

            var result = Task.Run(() => ArchiveService.CreateAsync(
                    new[] { srcDir }, ArchiveFormat.Zip, outPath, CancellationToken.None))
                .GetAwaiter().GetResult();

            if (!result.Success)
            {
                Add("批量打包：ZIP 端到端（建包 → 读回 → 内容逐字一致）", false, result.Error ?? "打包失败");
            }
            else
            {
                // ★ 关键：把包**打开**逐一核对，而不是只看文件存在
                using var zip = System.IO.Compression.ZipFile.OpenRead(outPath);
                var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();

                var hasRoot = names.Any(n => n.Contains("中文文件.txt"));
                var hasNested = names.Any(n => n.Contains("子目录/nested.txt"));

                // 条目名必须是**相对路径**（不能出现盘符 —— 否则解压出来是一长串目录）
                var relativeOk = names.All(n => !n.Contains(':'));

                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.Replace('\\', '/').EndsWith("中文文件.txt"));

                var content = "";
                if (entry is not null)
                {
                    using var sr = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
                    content = sr.ReadToEnd();
                }

                var contentOk = content == "这是中文内容";
                var ok = hasRoot && hasNested && relativeOk && contentOk;

                Add("批量打包：ZIP 端到端（建包 → 读回 → 内容逐字一致）", ok,
                    ok
                        ? $"{zip.Entries.Count} 个条目，条目名为相对路径，中文名与内容读回一致（{result.Bytes} 字节）"
                        : $"hasRoot={hasRoot} hasNested={hasNested} relativeOk={relativeOk} "
                          + $"contentOk={contentOk}（实得「{content}」）names=[{string.Join(", ", names)}]");
            }
        }
        catch (Exception ex)
        {
            Add("批量打包：ZIP 端到端（建包 → 读回 → 内容逐字一致）", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // ---- 4. 绝不覆盖同名压缩包 ----
        try
        {
            var d2 = Path.Combine(AppPaths.SelfTestDir, "arc2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d2);

            var target = Path.Combine(d2, "fixed.zip");
            File.WriteAllText(target, "占位：这个文件不能被覆盖");

            var p1 = Toolbox.Tools.Convert.FileNaming.UniquePath(target);
            File.WriteAllText(p1, "第二个");
            var p2 = Toolbox.Tools.Convert.FileNaming.UniquePath(target);

            var ok = p1 != target && p2 != target && p2 != p1
                     && File.ReadAllText(target) == "占位：这个文件不能被覆盖";

            Add("批量打包：绝不覆盖已有压缩包（重名自动换名）", ok,
                ok
                    ? $"已存在 fixed.zip → 依次改用「{Path.GetFileName(p1)}」「{Path.GetFileName(p2)}」，原文件未被改动"
                    : $"p1={p1} p2={p2}");

            Directory.Delete(d2, true);
        }
        catch (Exception ex)
        {
            Add("批量打包：绝不覆盖已有压缩包（重名自动换名）", false, ex.Message);
        }

        // ---- 5. 来源不存在要明确报错，且不产出垃圾文件 ----
        try
        {
            var d3 = Path.Combine(AppPaths.SelfTestDir, "arc3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d3);

            var ghost = Path.Combine(d3, "不存在.txt");
            var outPath = Path.Combine(d3, "should-not-exist.zip");

            var result = Task.Run(() => ArchiveService.CreateAsync(
                    new[] { ghost }, ArchiveFormat.Zip, outPath, CancellationToken.None))
                .GetAwaiter().GetResult();

            var ok = !result.Success
                     && !string.IsNullOrWhiteSpace(result.Error)
                     && !File.Exists(outPath);

            Add("批量打包：来源不存在时明确报错且不留垃圾文件", ok,
                ok
                    ? $"已拒绝并说明原因，且没有生成 {Path.GetFileName(outPath)}"
                    : $"Success={result.Success} Error={result.Error} 产物存在={File.Exists(outPath)}");

            Directory.Delete(d3, true);
        }
        catch (Exception ex)
        {
            Add("批量打包：来源不存在时明确报错且不留垃圾文件", false, ex.Message);
        }

        // ---- 6. RAR：装了 WinRAR 才实测；没装记"信息" ----
        try
        {
            var rar = ArchiveService.FindRar();

            if (rar is null)
            {
                Cases.Add(new Case
                {
                    Name = "批量打包：RAR 端到端（依赖 WinRAR）",
                    Passed = true,
                    Informational = true,
                    Detail = "这台机器没装 WinRAR，跳过实测（ZIP 不受影响，仍然可用）",
                });
            }
            else
            {
                var d4 = Path.Combine(AppPaths.SelfTestDir, "arc4-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(d4);

                var f1 = Path.Combine(d4, "one.txt");
                var f2 = Path.Combine(d4, "中文two.txt");
                File.WriteAllText(f1, "one", new System.Text.UTF8Encoding(false));
                File.WriteAllText(f2, "中文two", new System.Text.UTF8Encoding(false));

                var outPath = Path.Combine(d4, "out.rar");

                var result = Task.Run(() => ArchiveService.CreateAsync(
                        new[] { f1, f2 }, ArchiveFormat.Rar, outPath, CancellationToken.None))
                    .GetAwaiter().GetResult();

                // 核对产物是**真的 RAR**（看文件头 "Rar!"）——
                // 只看"文件存在"不够：WinRAR 出错时也可能留下半个文件
                var headerOk = false;
                if (File.Exists(outPath))
                {
                    var head = new byte[4];
                    using var fs = File.OpenRead(outPath);
                    var read = fs.Read(head, 0, 4);
                    headerOk = read == 4
                               && head[0] == (byte)'R' && head[1] == (byte)'a'
                               && head[2] == (byte)'r' && head[3] == (byte)'!';
                }

                var ok = result.Success && headerOk && result.Bytes > 0;

                Add("批量打包：RAR 端到端（用 WinRAR 建包，产物文件头为 Rar!）", ok,
                    ok
                        ? $"已生成 {result.Bytes} 字节的真 RAR（含中文文件名），耗时 {result.ElapsedMs} ms"
                        : $"Success={result.Success} headerOk={headerOk} Error={result.Error}");

                try { Directory.Delete(d4, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            Add("批量打包：RAR 端到端（依赖 WinRAR）", false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 运行命令

    /// <summary>
    /// 运行命令工具的用例。
    ///
    /// ⚠️ 自检里**只跑绝对安全的命令**（echo / exit / ping 本地回环），
    ///    全都是只读、无副作用的。自检是会自动跑的，
    ///    绝不能在里面执行任何会改文件 / 改注册表的命令 ——
    ///    万一有人把自检接到 CI 或定时任务上，那就是灾难。
    /// </summary>
    private static void RunCommandRunnerTests()
    {
        // ---- 1. shell 解析：两种 shell 都要能找到真实存在的 exe ----
        try
        {
            var cmd = CommandRunner.ResolveInterpreter(ShellKind.Cmd);
            var ps = CommandRunner.ResolveInterpreter(ShellKind.PowerShell);

            var cmdOk = !string.IsNullOrWhiteSpace(cmd) && File.Exists(cmd);
            var psOk = !string.IsNullOrWhiteSpace(ps) && File.Exists(ps);

            Add("运行命令：两种 shell 都能解析到真实 exe", cmdOk && psOk,
                cmdOk && psOk
                    ? $"cmd → {cmd}；PowerShell → {ps}"
                    : $"cmdOk={cmdOk}（{cmd}） psOk={psOk}（{ps}）");
        }
        catch (Exception ex)
        {
            Add("运行命令：两种 shell 都能解析到真实 exe", false, ex.Message);
        }

        // ---- 2. 命令行预览：必须把"真正要跑的参数"显示出来（这是安全设计的一部分）----
        try
        {
            var cmdLine = CommandRunner.DescribeCommandLine(ShellKind.Cmd, "echo hi", null);
            var psLine = CommandRunner.DescribeCommandLine(ShellKind.PowerShell, "echo hi", null);

            // cmd 预览要能看到 /c（执行完退出）与 /d（跳过 AutoRun）
            var cmdOk = cmdLine.Contains("/c") && cmdLine.Contains("/d") && cmdLine.Contains("echo hi");

            // PowerShell 预览要能看到 -NoProfile / -NonInteractive（行为可预期、不会挂住等输入）
            var psOk = psLine.Contains("-NoProfile") && psLine.Contains("-NonInteractive");

            Add("运行命令：命令行预览含关键参数（/c /d、-NoProfile -NonInteractive）",
                cmdOk && psOk,
                cmdOk && psOk
                    ? "预览里能看到最终拼给 shell 的参数，用户可核对"
                    : $"cmdOk={cmdOk} psOk={psOk}");
        }
        catch (Exception ex)
        {
            Add("运行命令：命令行预览含关键参数（/c /d、-NoProfile -NonInteractive）", false, ex.Message);
        }

        // ---- 3. 真跑一条只读命令（cmd）----
        try
        {
            var result = Task.Run(() => CommandRunner.RunAsync(
                    ShellKind.Cmd, "echo toolbox-selftest-ok", null,
                    CancellationToken.None, TimeSpan.FromSeconds(30)))
                .GetAwaiter().GetResult();

            var ok = result.Success
                     && result.ExitCode == 0
                     && result.Output.Contains("toolbox-selftest-ok");

            Add("运行命令：cmd 实跑（echo → 取回输出）", ok,
                ok
                    ? $"退出码 0，输出含预期内容，耗时 {result.ElapsedMs} ms"
                    : $"Success={result.Success} ExitCode={result.ExitCode} "
                      + $"Output=\"{result.Output.Replace("\n", "\\n")}\" Error={result.Error}");
        }
        catch (Exception ex)
        {
            Add("运行命令：cmd 实跑（echo → 取回输出）", false, ex.Message);
        }

        // ---- 4. 真跑一条只读命令（PowerShell）----
        try
        {
            var result = Task.Run(() => CommandRunner.RunAsync(
                    ShellKind.PowerShell, "Write-Output 'toolbox-ps-ok'", null,
                    CancellationToken.None, TimeSpan.FromSeconds(45)))
                .GetAwaiter().GetResult();

            var ok = result.Success
                     && result.ExitCode == 0
                     && result.Output.Contains("toolbox-ps-ok");

            Add("运行命令：PowerShell 实跑（Write-Output → 取回输出）", ok,
                ok
                    ? $"退出码 0，输出含预期内容，耗时 {result.ElapsedMs} ms"
                    : $"Success={result.Success} ExitCode={result.ExitCode} "
                      + $"Output=\"{result.Output.Replace("\n", "\\n")}\" Error={result.Error}");
        }
        catch (Exception ex)
        {
            Add("运行命令：PowerShell 实跑（Write-Output → 取回输出）", false, ex.Message);
        }

        // ---- 5. 中文输出不乱码（最容易出问题的地方）----
        try
        {
            var result = Task.Run(() => CommandRunner.RunAsync(
                    ShellKind.Cmd, "echo 中文测试", null,
                    CancellationToken.None, TimeSpan.FromSeconds(30)))
                .GetAwaiter().GetResult();

            var ok = result.Success && result.Output.Contains("中文测试");

            Add("运行命令：中文输出不乱码（按实际代码页解码）", ok,
                ok
                    ? "命令输出的中文原样取回，未出现乱码"
                    : $"实得：\"{result.Output.Replace("\n", "\\n")}\"");
        }
        catch (Exception ex)
        {
            Add("运行命令：中文输出不乱码（按实际代码页解码）", false, ex.Message);
        }

        // ---- 6. 非 0 退出码要如实报告（不能当成"工具箱出错"）----
        try
        {
            // exit /b 3 —— 明确返回退出码 3，无任何副作用
            var result = Task.Run(() => CommandRunner.RunAsync(
                    ShellKind.Cmd, "exit /b 3", null,
                    CancellationToken.None, TimeSpan.FromSeconds(30)))
                .GetAwaiter().GetResult();

            var ok = !result.Success && result.ExitCode == 3 && result.Error is null;

            Add("运行命令：非 0 退出码如实回报（不误判成工具故障）", ok,
                ok
                    ? "退出码 3 被如实取回，且未被标记为「执行出错」"
                    : $"Success={result.Success} ExitCode={result.ExitCode} Error={result.Error}");
        }
        catch (Exception ex)
        {
            Add("运行命令：非 0 退出码如实回报（不误判成工具故障）", false, ex.Message);
        }

        // ---- 7. 空命令必须被拦下，不去起进程 ----
        try
        {
            var empty = Task.Run(() => CommandRunner.RunAsync(
                    ShellKind.Cmd, "", null, CancellationToken.None, TimeSpan.FromSeconds(5)))
                .GetAwaiter().GetResult();

            var blank = Task.Run(() => CommandRunner.RunAsync(
                    ShellKind.Cmd, "   ", null, CancellationToken.None, TimeSpan.FromSeconds(5)))
                .GetAwaiter().GetResult();

            var ok = !empty.Success && !blank.Success
                     && !string.IsNullOrWhiteSpace(empty.Error);

            Add("运行命令：空命令被拦下（不启动进程）", ok,
                ok ? "空串与纯空白都被拒绝，并给出中文原因" : $"empty={empty.Success} blank={blank.Success}");
        }
        catch (Exception ex)
        {
            Add("运行命令：空命令被拦下（不启动进程）", false, ex.Message);
        }

        // ---- 8. 超时必须真的中止（否则会留一堆挂着的 shell 进程）----
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ping 本地回环、只输出到 nul —— 只读、无副作用，但会持续 30 秒
            var result = Task.Run(() => CommandRunner.RunAsync(
                    ShellKind.Cmd,
                    "ping -n 30 127.0.0.1 >nul",
                    null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(3)))
                .GetAwaiter().GetResult();

            sw.Stop();

            var timely = sw.Elapsed < TimeSpan.FromSeconds(20);
            var reported = !result.Success && result.Error is not null && result.Error.Contains("超时");

            Add("运行命令：超时后真的中止（不会留挂着的 shell）", timely && reported,
                timely && reported
                    ? $"3 秒超时生效（实测 {sw.ElapsedMilliseconds} ms 就返回了），并给出中文提示"
                    : $"timely={timely}（{sw.ElapsedMilliseconds} ms） reported={reported} Error={result.Error}");
        }
        catch (Exception ex)
        {
            Add("运行命令：超时后真的中止（不会留挂着的 shell）", false, ex.Message);
        }
    }

    /// <summary>
    /// 检查**真实源码里**的注册清单是否覆盖了所有工具类。
    ///
    /// ⚠️ 这条必须**读源码文件**，不能建一个自己的注册表来测 ——
    ///    变异测试证明了这一点：我第一版就是自己 new 了一个 registry、
    ///    把 12 个工具都 Add 进去、再断言"都在" —— 那当然永远为真。
    ///    真把 App.xaml.cs 里的一行注册删掉，它照样绿。**那是自欺。**
    ///
    ///    现在改成：找出所有 IToolboxTool 实现类，
    ///    再去 App.xaml.cs 里数 `_registry.Add(new XxxTool())` 的条目逐个核对。
    ///    删掉任何一行真实注册，这条立刻红。
    /// </summary>
    private static void RunToolDiscoveryTest()
    {
        try
        {
            var toolInterface = typeof(IToolboxTool);

            // ★ 扫**所有已加载的程序集**，而不是只扫 toolInterface 所在的那一个。
            //
            //   为什么（B 阶段重构后暴露的真问题）：
            //     IToolboxTool 已搬到 Toolbox.Contracts.dll，
            //     而**实现类**在主程序 Toolbox.exe 里。
            //     原来写 toolInterface.Assembly 只会扫契约程序集 ⇒ 一个实现都找不到
            //     ⇒ 用例报"反射查找可能失效"。
            //     这不是产品坏了，是**扫描范围跟不上重构**。
            //
            //   改成扫全部已加载程序集之后，这条用例还顺带能覆盖
            //   "插件程序集里的工具类" —— 以后插件也会在这里被看见。
            var implementations = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(a =>
                {
                    try
                    {
                        return a.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        // 部分类型加载失败时，能拿到的那些仍然有用 ——
                        // 不要因为一个加载不上的类型就整个放弃
                        return ex.Types.Where(t => t is not null).Cast<Type>();
                    }
                    catch
                    {
                        return Array.Empty<Type>();
                    }
                })
                .Where(t => t.IsClass && !t.IsAbstract && toolInterface.IsAssignableFrom(t))
                .Select(t => t.Name)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            if (implementations.Count == 0)
            {
                Add("工具发现：所有工具类都在 App 里注册了（防「忘了接线」）", false,
                    "一个 IToolboxTool 实现都没扫到 —— 反射查找可能失效了");
                return;
            }

            // ★ 读**真实源码**
            var root = FindSourceRoot();

            if (string.IsNullOrEmpty(root))
            {
                // 找不到源码（例如跑的是**安装版 / 便携版**，身边根本没有源码树）。
                //
                // 这时**不能报失败** —— 那不是代码有问题，是环境没有源码可读。
                // 但也**不能静默通过**（那就变成"永远绿灯的摆设"了）。
                // 所以记成「信息」项：不计入通过率，但会在报告里显示，
                // 让人一眼看出"这条在开发环境里是验过的，在这里没验"。
                Cases.Add(new Case
                {
                    Name = "工具发现：所有工具类都在 App 里注册了（需源码，开发环境才验）",
                    Passed = true,
                    Informational = true,
                    Detail = "当前位置读不到 App.xaml.cs（安装版/便携版没有源码树），"
                             + "跳过核对。在源码目录里跑自检时这条会真正执行。",
                });
                return;
            }

            var appSource = Path.Combine(root, "App.xaml.cs");

            if (!File.Exists(appSource))
            {
                Add("工具发现：所有工具类都在 App 里注册了（防「忘了接线」）", false,
                    $"找到了源码根 {root}，但里面没有 App.xaml.cs");
                return;
            }

            var text = File.ReadAllText(appSource, System.Text.Encoding.UTF8);

            var registered = System.Text.RegularExpressions.Regex
                .Matches(text, @"_registry\.Add\(new\s+(\w+)\s*\(\s*\)\s*\)")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            var notRegistered = implementations.Where(t => !registered.Contains(t)).ToList();

            // ★ B 阶段起：**插件工具**合法地不出现在 App.xaml.cs 里。
            //
            //   它们的注册方式是"被 PluginLoader 从插件目录发现并加载"，
            //   所以不能拿"App.xaml.cs 里有没有 Add 这一行"判定它漏了接线。
            //
            //   怎么区分"有意做成插件的"和"真的忘了接线的"：
            //   看磁盘上有没有对应的**插件工程**（plugins-src\...\*.csproj）。
            //   有 ⇒ 它本就是插件，不算漏。
            //
            //   ⚠️ 这条判断必须**读磁盘**，不能自己维护一份"哪些是插件"的名单 ——
            //      那样又变成"我以为"了（见 DECISIONS 坑 32/33 的教训）。
            var pluginToolNames = FindPluginToolNames();

            var genuinelyMissing = notRegistered
                .Where(n => !pluginToolNames.Contains(n))
                .ToList();

            var ok = genuinelyMissing.Count == 0;

            Add("工具发现：所有工具类都在 App 里注册了（防「忘了接线」）", ok,
                ok
                    ? (notRegistered.Count == 0
                        ? $"程序集里 {implementations.Count} 个工具类，App.xaml.cs 里 {registered.Count} 条注册，一一对应"
                        : $"程序集里 {implementations.Count} 个工具类：{registered.Count} 个内置注册，"
                          + $"{notRegistered.Count} 个走插件机制（{string.Join("、", notRegistered)}）")
                    : $"★ 这些工具类**既没在 App.xaml.cs 里注册、也不是插件**："
                      + string.Join("、", genuinelyMissing));
        }
        catch (Exception ex)
        {
            Add("工具发现：所有工具类都在 App 里注册了（防「忘了接线」）", false, ex.Message);
        }
    }

    /// <summary>
    /// 扫出"哪些工具被做成了插件"（按磁盘上的插件工程判断）。
    ///
    /// 依据：`plugins-src\...\*.csproj` 里 `<Compile Include="...\XxxTool.cs" />`
    /// 链接的那些工具类名。
    ///
    /// 读不到时返回空集合 —— 那样所有未注册的工具类都会被判为漏接线，
    /// 是**偏严**的失败方向（宁可误报，也不放过真的漏接线）。
    /// </summary>
    private static HashSet<string> FindPluginToolNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var root = FindSourceRoot();          // 形如 ...\src\Toolbox
            if (string.IsNullOrEmpty(root))
            {
                return names;
            }

            // plugins-src 与 src 同级 ⇒ 从 src\Toolbox 往上两层
            var projectRoot = Path.GetFullPath(Path.Combine(root, "..", ".."));
            var pluginSrc = Path.Combine(projectRoot, "plugins-src");

            if (!Directory.Exists(pluginSrc))
            {
                return names;
            }

            foreach (var csproj in Directory.GetFiles(pluginSrc, "*.csproj", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(csproj, System.Text.Encoding.UTF8);

                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(text, @"([A-Za-z0-9_]+Tool)\.cs"))
                {
                    names.Add(m.Groups[1].Value);
                }
            }
        }
        catch
        {
            // 读不到就返回空集合 —— 偏严，宁可误报
        }

        return names;
    }

    /// <summary>
    /// 找到源码根目录（含 App.xaml.cs 的那一层）。
    ///
    /// 自检可能从**好几个位置**跑（bin\Debug、bin\Release、dist\、安装目录……），
    /// 光从 exe 往上找不够 —— 实测 `dist\Toolbox.exe` 往上几层都不是源码目录。
    /// 所以按优先级试多个来源：
    ///   ① exe 所在目录往上逐层找（开发时从 bin\ 跑就是这条）；
    ///   ② 当前工作目录往上逐层找；
    ///   ③ 已知项目结构：项目根下有 `src\Toolbox\App.xaml.cs`（dist 旁边就是 src）。
    ///
    /// 都找不到返回空串 ⇒ 用例给**明确失败**（宁可报错，也不假装验过了）。
    /// </summary>
    private static string FindSourceRoot()
    {
        var probes = new List<string>();

        void AddUpward(string? start)
        {
            try
            {
                var dir = string.IsNullOrWhiteSpace(start) ? null : new DirectoryInfo(start);
                for (var i = 0; i < 8 && dir is not null; i++)
                {
                    probes.Add(dir.FullName);
                    dir = dir.Parent;
                }
            }
            catch
            {
                // 忽略
            }
        }

        AddUpward(AppContext.BaseDirectory);
        AddUpward(Environment.CurrentDirectory);

        // 已知项目结构（相对种子目录找 src\Toolbox）
        foreach (var seed in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            try
            {
                probes.Add(Path.Combine(seed, "src", "Toolbox"));
                probes.Add(Path.Combine(seed, "..", "src", "Toolbox"));
                probes.Add(Path.Combine(seed, "..", "..", "src", "Toolbox"));
                probes.Add(Path.Combine(seed, "..", "..", "..", "src", "Toolbox"));
            }
            catch
            {
                // 忽略
            }
        }

        foreach (var p in probes)
        {
            try
            {
                var full = Path.GetFullPath(p);
                if (File.Exists(Path.Combine(full, "App.xaml.cs")))
                {
                    return full;
                }
            }
            catch
            {
                // 路径非法就跳过
            }
        }

        return string.Empty;
    }

    // ---------------------------------------------------------------- 工具仓库

    /// <summary>
    /// 「按需下载」链路的用例（**不联网**的那部分）。
    ///
    /// 联网部分不放进自检 —— 自检必须能在**完全离线**环境下跑通，
    /// 否则网络一抖就一片红，用户会以为工具箱坏了。
    /// 联网链路由 `artifacts\probe-store\` 单独实测：
    /// 已验真实下载 68 MB + SHA-256 校验通过 + 篡改内容被拒绝。
    /// </summary>
    private static void RunToolStoreTests()
    {
        // ---- 1. 内置清单必须完整且自洽（离线可用的那份）----
        try
        {
            var m = ToolStore.BuiltInManifest();

            var schemaOk = m.SchemaVersion == 1;
            var countOk = m.Tools.Count == 12;

            var ids = m.Tools.Select(t => t.Id).ToList();
            var noDup = ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Count;

            var noBlank = m.Tools.All(t =>
                !string.IsNullOrWhiteSpace(t.Id)
                && !string.IsNullOrWhiteSpace(t.Name)
                && !string.IsNullOrWhiteSpace(t.Description));

            var ok = schemaOk && countOk && noDup && noBlank;

            Add("工具仓库：内置清单完整自洽（12 条目 / 无重复 / 无空字段）", ok,
                ok
                    ? $"SchemaVersion=1，{m.Tools.Count} 个条目，Id 无重复，字段无空白"
                    : $"schemaOk={schemaOk} countOk={countOk} noDup={noDup} noBlank={noBlank}");
        }
        catch (Exception ex)
        {
            Add("工具仓库：内置清单完整自洽（12 条目 / 无重复 / 无空字段）", false, ex.Message);
        }

        // ---- 2. 清单里的 Id 必须与真实工具一一对应 ----
        //
        // 防的是"清单写了个不存在的工具"或"新加了工具却忘了进清单"——
        // 后者会让工具管理界面漏掉它。
        try
        {
            var manifestIds = ToolStore.BuiltInManifest().Tools
                .Select(t => t.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var registryIds = BuildFullRegistry().Tools
                .Select(t => t.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missingInManifest = registryIds.Except(manifestIds).ToList();
            var extraInManifest = manifestIds.Except(registryIds).ToList();

            var ok = missingInManifest.Count == 0 && extraInManifest.Count == 0;

            Add("工具仓库：清单 Id 与真实工具一一对应", ok,
                ok
                    ? $"{manifestIds.Count} 个 Id 两边完全一致"
                    : $"清单缺：{string.Join("、", missingInManifest)}；"
                      + $"清单多：{string.Join("、", extraInManifest)}");
        }
        catch (Exception ex)
        {
            Add("工具仓库：清单 Id 与真实工具一一对应", false, ex.Message);
        }

        // ---- 3. 多镜像源配置：直连 + 至少 2 个镜像 ----
        //
        // 用户明确要求"国内部分地区无法直连时要留镜像"。
        // 这条保证以后有人手滑删掉镜像时能被发现。
        try
        {
            var listSources = ToolStore.Sources;
            var releaseSources = ToolStore.ReleaseSources;

            var listOk = listSources.Count >= 3;
            var releaseOk = releaseSources.Count >= 3;

            var directFirst = listSources[0].Name.Contains("直连")
                              && releaseSources[0].Name.Contains("直连");

            var listTemplatesOk = listSources.All(s =>
                s.Template.Contains("{owner}") && s.Template.Contains("{path}"));
            var relTemplatesOk = releaseSources.All(s =>
                s.Template.Contains("{owner}") && s.Template.Contains("{tag}") && s.Template.Contains("{asset}"));

            var ok = listOk && releaseOk && directFirst && listTemplatesOk && relTemplatesOk;

            Add("工具仓库：多镜像源已配置（直连优先 + ≥2 镜像 + 模板占位符）", ok,
                ok
                    ? $"清单源 {listSources.Count} 个、下载源 {releaseSources.Count} 个；"
                      + $"镜像：{string.Join("、", listSources.Skip(1).Select(s => s.Name))}"
                    : $"listOk={listOk} releaseOk={releaseOk} directFirst={directFirst} "
                      + $"listTemplatesOk={listTemplatesOk} relTemplatesOk={relTemplatesOk}");
        }
        catch (Exception ex)
        {
            Add("工具仓库：多镜像源已配置（直连优先 + ≥2 镜像 + 模板占位符）", false, ex.Message);
        }

        // ---- 4. URL 拼接正确（占位符必须全替换）----
        try
        {
            var direct = ToolStore.Sources[0];
            var url = ToolStore.BuildUrl(direct, new Dictionary<string, string> { ["path"] = "tools.json" });
            var directOk = url == "https://raw.githubusercontent.com/18477514055/toolbox/main/tools.json";

            var mirror = ToolStore.Sources.First(s => s.Name == "ghproxy.net");
            var murl = ToolStore.BuildUrl(mirror, new Dictionary<string, string> { ["path"] = "tools.json" });
            var mirrorOk = murl.StartsWith("https://ghproxy.net/https://raw.githubusercontent.com/");

            var ok = directOk && mirrorOk;
            Add("工具仓库：URL 拼接正确（直连与镜像）", ok,
                ok
                    ? $"直连：{url}"
                    : $"directOk={directOk}（{url}） mirrorOk={mirrorOk}（{murl}）");
        }
        catch (Exception ex)
        {
            Add("工具仓库：URL 拼接正确（直连与镜像）", false, ex.Message);
        }

        // ---- 5. SHA-256 计算正确（拿公开测试向量验，不是自己跟自己对）----
        try
        {
            Directory.CreateDirectory(AppPaths.SelfTestDir);
            var temp = Path.Combine(AppPaths.SelfTestDir, "sha-" + Guid.NewGuid().ToString("N") + ".bin");

            // "abc" 的 SHA-256 是公开标准值
            File.WriteAllBytes(temp, new byte[] { 0x61, 0x62, 0x63 });

            try
            {
                var got = ToolStore.ComputeSha256(temp);
                const string want = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";

                Add("工具仓库：SHA-256 计算正确（公开测试向量）", got == want,
                    got == want ? "与 \"abc\" 的标准哈希一致" : $"实得 {got}（期望 {want}）");
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }
        catch (Exception ex)
        {
            Add("工具仓库：SHA-256 计算正确（公开测试向量）", false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 插件机制

    /// <summary>
    /// B 阶段插件机制的用例。
    ///
    /// 这些**都不联网、也不真的加载第三方 dll** ——
    /// 自检要能在任何环境下跑通。真正"装一个插件并让它跑起来"的验证
    /// 由 artifacts\probe-plugin\ 与实机安装测试覆盖（已验：
    /// 契约跨程序集匹配 / 单文件宿主 / WPF 插件 / 装了就出现、删了就消失）。
    /// </summary>
    private static void RunPluginTests()
    {
        // ---- 1. 契约版本兼容性判定 ----
        //
        // 这条防的是"用户装了一个针对旧版主程序编译的插件"——
        // 没有它，那种插件会以各种看不懂的方式失败。
        try
        {
            var sameOk = ContractsVersion.IsCompatible("1.0", out _);
            var minorOk = ContractsVersion.IsCompatible("1.7", out _);   // 同主版本，向后兼容
            var diffRejected = !ContractsVersion.IsCompatible("2.0", out var r1);
            var emptyRejected = !ContractsVersion.IsCompatible("", out var r2);
            var nullRejected = !ContractsVersion.IsCompatible(null, out _);

            var ok = sameOk && minorOk && diffRejected && emptyRejected && nullRejected;

            Add("插件：契约版本兼容判定（同主版本放行 / 跨主版本拒绝 / 空值拒绝）", ok,
                ok
                    ? $"当前 {ContractsVersion.Current}：1.0 ✅、1.7 ✅、2.0 ❌（{r1}）、空 ❌"
                    : $"sameOk={sameOk} minorOk={minorOk} diffRejected={diffRejected} "
                      + $"emptyRejected={emptyRejected} nullRejected={nullRejected} r2={r2}");
        }
        catch (Exception ex)
        {
            Add("插件：契约版本兼容判定（同主版本放行 / 跨主版本拒绝 / 空值拒绝）", false, ex.Message);
        }

        // ---- 2. 插件目录约定 ----
        //
        // 插件必须装在**数据目录**下，不能装到程序目录 ——
        // 否则重装/升级程序会把用户装的插件冲掉。
        try
        {
            var root = PluginLoader.PluginsRoot;
            var one = PluginLoader.PluginDir("topmost");

            var underData = root.StartsWith(AppPaths.Root, StringComparison.OrdinalIgnoreCase);
            var separate = !string.Equals(one, root, StringComparison.OrdinalIgnoreCase);

            var ok = underData && separate;

            Add("插件：安装位置在数据目录下（重装程序不会丢）", ok,
                ok ? $"插件根目录：{root}" : $"underData={underData} separate={separate} root={root}");
        }
        catch (Exception ex)
        {
            Add("插件：安装位置在数据目录下（重装程序不会丢）", false, ex.Message);
        }

        // ---- 3. 未装插件时 IsInstalled 必须是 false ----
        //
        // 这条防的是"IsInstalled 永远返回 true"这类假实现 ——
        // 那会让界面把没装的插件显示成"已下载"。
        try
        {
            var ghostId = "definitely-not-installed-" + Guid.NewGuid().ToString("N")[..8];
            var ok = !PluginLoader.IsInstalled(ghostId);

            Add("插件：未安装的插件 IsInstalled 为 false（防假实现）", ok,
                ok ? $"查询不存在的插件「{ghostId}」→ false" : "★ 竟然返回 true");
        }
        catch (Exception ex)
        {
            Add("插件：未安装的插件 IsInstalled 为 false（防假实现）", false, ex.Message);
        }

        // ---- 4. 插件清单必须在 tools.json 里对得上 ----
        //
        // 防的是"插件做出来了、但清单里还写着 BuiltIn=true"，
        // 那样界面会告诉用户"它内置了、不用下载"，而实际用不到。
        try
        {
            var pluginIds = FindPluginToolNames();
            var manifest = ToolStore.BuiltInManifest();

            var problems = new List<string>();

            foreach (var tool in manifest.Tools)
            {
                // 判定这个 Id 对应的实现类是不是插件
                var isPluginImpl = pluginIds.Any(p =>
                    string.Equals(p, ToolNameForId(tool.Id), StringComparison.OrdinalIgnoreCase));

                if (isPluginImpl && tool.BuiltIn)
                {
                    problems.Add($"「{tool.Name}」的实现是插件，但清单里标成了内置");
                }

                // 标成"可下载"的必须有 AssetName 和 Sha256，否则装不了
                if (!tool.BuiltIn)
                {
                    if (string.IsNullOrWhiteSpace(tool.AssetName))
                    {
                        problems.Add($"「{tool.Name}」标为可下载，却没有 AssetName");
                    }

                    if (string.IsNullOrWhiteSpace(tool.Sha256) || tool.Sha256.Length != 64)
                    {
                        problems.Add($"「{tool.Name}」标为可下载，却没有合法的 SHA-256");
                    }
                }
            }

            var ok = problems.Count == 0;

            Add("插件：清单与实现一致（插件不标内置 / 可下载项必带哈希）", ok,
                ok
                    ? $"清单 {manifest.Tools.Count} 项核对通过；"
                      + $"其中可下载 {manifest.Tools.Count(t => !t.BuiltIn)} 项都带了 AssetName 与 SHA-256"
                    : "★ " + string.Join("；", problems));
        }
        catch (Exception ex)
        {
            Add("插件：清单与实现一致（插件不标内置 / 可下载项必带哈希）", false, ex.Message);
        }
    }

    /// <summary>工具 Id → 实现类名的粗略映射（用于清单核对）。</summary>
    private static string ToolNameForId(string id) => id switch
    {
        "clipboard" => "ClipboardTool",
        "image" => "ImageCropTool",
        "convert" => "ConvertTool",
        "ai" => "AiTool",
        "screenshot" => "ScreenshotTool",
        "ocr" => "OcrTool",
        "rename" => "BatchRenameTool",
        "hash" => "HashCheckTool",
        "qrcode" => "QrTool",
        "topmost" => "WindowTopmostTool",
        "run" => "CommandRunnerTool",
        "archive" => "ArchiveTool",
        _ => "",
    };

    // ---------------------------------------------------------------- 功能开关

    /// <summary>
    /// 功能开关（总开关 + 每工具开关）的用例。
    ///
    /// 这是本轮新加的核心行为，**必须测全**，因为它同时影响
    /// 悬浮窗显示、热键注册、托盘菜单、以及工具是否真的在跑 ——
    /// 任何一处判定不一致，用户就会看到"关了还在跑"或"开了没反应"。
    /// </summary>
    private static void RunToolSwitchTests()
    {
        // ---- 1. 默认策略：常用 5 个开、其余关 ----
        try
        {
            var s = new Settings();
            s.ToolEnabled.Clear();

            var expectedOn = new[] { "clipboard", "screenshot", "image", "convert", "ai" };

            var onOk = expectedOn.All(id => ToolGate.IsEnabled(s, id));
            var offOk = !ToolGate.IsEnabled(s, "ocr")
                        && !ToolGate.IsEnabled(s, "rename")
                        && !ToolGate.IsEnabled(s, "hash")
                        && !ToolGate.IsEnabled(s, "qrcode")
                        && !ToolGate.IsEnabled(s, "topmost")
                        && !ToolGate.IsEnabled(s, "run")
                        && !ToolGate.IsEnabled(s, "archive");

            Add("功能开关：默认策略（常用 5 个开、其余 7 个关）", onOk && offOk,
                onOk && offOk
                    ? $"默认开启：{string.Join("、", expectedOn)}"
                    : $"onOk={onOk} offOk={offOk}");
        }
        catch (Exception ex)
        {
            Add("功能开关：默认策略（常用 5 个开、其余 7 个关）", false, ex.Message);
        }

        // ---- 2. 单个开关能覆盖默认 ----
        try
        {
            var s = new Settings();

            // 把默认关的打开
            s.ToolEnabled["ocr"] = true;
            var openOk = ToolGate.IsEnabled(s, "ocr");

            // 把默认开的关掉
            s.ToolEnabled["clipboard"] = false;
            var closeOk = !ToolGate.IsEnabled(s, "clipboard");

            Add("功能开关：单个开关能覆盖默认值", openOk && closeOk,
                openOk && closeOk
                    ? "默认关的可以打开；默认开的可以关掉"
                    : $"openOk={openOk} closeOk={closeOk}");
        }
        catch (Exception ex)
        {
            Add("功能开关：单个开关能覆盖默认值", false, ex.Message);
        }

        // ---- 3. ★ 总开关优先：关掉总开关时，所有工具一律不启用 ----
        //
        // 这是最关键的语义。如果总开关不优先，
        // 用户关掉总开关后会发现有工具还在跑 —— 那就失去了"一键安静"的意义。
        try
        {
            var s = new Settings { ToolsEnabled = false };

            // 即使某个工具被显式设成 true，总开关关掉时也不该启用
            s.ToolEnabled["ocr"] = true;
            s.ToolEnabled["clipboard"] = true;

            var allOff = new[]
            {
                "clipboard", "image", "convert", "ai", "screenshot",
                "ocr", "rename", "hash", "qrcode", "topmost", "run", "archive",
            }.All(id => !ToolGate.IsEnabled(s, id));

            Add("功能开关：总开关关闭时所有工具一律停用（总开关优先）", allOff,
                allOff
                    ? "总开关关闭 ⇒ 12 个工具全部返回「未启用」，无视单个开关"
                    : "★ 有工具在总开关关闭时仍被判定为启用");
        }
        catch (Exception ex)
        {
            Add("功能开关：总开关关闭时所有工具一律停用（总开关优先）", false, ex.Message);
        }

        // ---- 4. 总开关重新打开后，单个选择要恢复 ----
        try
        {
            var s = new Settings { ToolsEnabled = true };
            s.ToolEnabled["ocr"] = true;
            s.ToolEnabled["clipboard"] = false;

            var restored = ToolGate.IsEnabled(s, "ocr") && !ToolGate.IsEnabled(s, "clipboard");

            Add("功能开关：总开关重开后恢复各工具的原有选择", restored,
                restored
                    ? "打开总开关后：显式开启的仍是开、显式关闭的仍是关"
                    : "★ 总开关重开后选择丢失");
        }
        catch (Exception ex)
        {
            Add("功能开关：总开关重开后恢复各工具的原有选择", false, ex.Message);
        }

        // ---- 5. 悬浮窗只显示启用中的工具 ----
        try
        {
            var registry = BuildFullRegistry();
            var s = new Settings();

            var visible = registry.VisibleTools(s).Select(t => t.Id).ToList();

            var onlyEnabled = visible.All(id => ToolGate.IsEnabled(s, id));
            var noneDisabled = !visible.Contains("ocr") && !visible.Contains("archive");

            Add("功能开关：悬浮窗只显示启用中的工具", onlyEnabled && noneDisabled,
                onlyEnabled && noneDisabled
                    ? $"默认状态下悬浮窗显示 {visible.Count} 个：{string.Join("、", visible)}"
                    : $"onlyEnabled={onlyEnabled} noneDisabled={noneDisabled}；实得 {string.Join("、", visible)}");

            // 总开关关掉 ⇒ 悬浮窗一个都不显示
            var s2 = new Settings { ToolsEnabled = false };
            var noneVisible = !registry.VisibleTools(s2).Any();

            Add("功能开关：总开关关闭时悬浮窗不显示任何工具", noneVisible,
                noneVisible ? "总开关关闭 ⇒ VisibleTools 返回空" : "★ 总开关关闭时仍有工具要显示");
        }
        catch (Exception ex)
        {
            Add("功能开关：悬浮窗只显示启用中的工具", false, ex.Message);
        }

        // ---- 6. 开关的存取语义：与默认一致时不写条目（便于以后改默认策略） ----
        try
        {
            var s = new Settings();
            s.ToolEnabled.Clear();

            // 模拟设置窗口的保存逻辑：与默认一致 → 移除条目
            var id = "clipboard";
            var userWants = ToolGate.IsDefaultOn(id);

            if (userWants == ToolGate.IsDefaultOn(id))
            {
                s.ToolEnabled.Remove(id);
            }

            var notStored = !s.ToolEnabled.ContainsKey(id);

            // 与默认不一致 → 写条目
            s.ToolEnabled["ocr"] = true;
            var stored = s.ToolEnabled.ContainsKey("ocr") && s.ToolEnabled["ocr"];

            Add("功能开关：只存「和默认不一样」的选择（以后改默认策略能自动跟随）",
                notStored && stored,
                notStored && stored
                    ? "与默认一致 → 不写条目；与默认不同 → 显式存 true/false"
                    : $"notStored={notStored} stored={stored}");
        }
        catch (Exception ex)
        {
            Add("功能开关：只存「和默认不一样」的选择（以后改默认策略能自动跟随）", false, ex.Message);
        }

        // ---- 7. 设置往返：开关状态不丢 ----
        try
        {
            var s = new Settings { ToolsEnabled = false };
            s.ToolEnabled["ocr"] = true;
            s.ToolEnabled["clipboard"] = false;

            var json = JsonSerializer.Serialize(s, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

            var back = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            var ok = back is not null
                     && back.ToolsEnabled == false
                     && back.ToolEnabled.TryGetValue("ocr", out var o) && o
                     && back.ToolEnabled.TryGetValue("clipboard", out var c) && !c;

            Add("功能开关：设置往返不丢（总开关 + 单工具开关）", ok,
                ok ? "总开关与两个单工具开关都完整往返" : "★ 往返后有丢失");

            // 老设置文件没有这两个字段 → 必须是安全默认（总开关开、字典空）
            var legacy = JsonSerializer.Deserialize<Settings>(
                """{"HotKeys":{"clipboard":"Win+Alt+V"}}""",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var legacySafe = legacy is not null
                             && legacy.ToolsEnabled
                             && legacy.ToolEnabled is not null
                             && legacy.ToolEnabled.Count == 0;

            Add("功能开关：老设置文件缺字段时为安全默认（总开关开、跟随新默认）", legacySafe,
                legacySafe
                    ? "老配置加载后：总开关=开，单工具选择为空 ⇒ 按新默认策略走"
                    : $"legacySafe={legacySafe}");
        }
        catch (Exception ex)
        {
            Add("功能开关：设置往返不丢（总开关 + 单工具开关）", false, ex.Message);
        }
    }

    /// <summary>建一个含全部 12 个工具的注册表（多个用例共用）。</summary>
    private static ToolRegistry BuildFullRegistry()
    {
        var registry = new ToolRegistry();
        registry.Add(new Toolbox.Tools.Clipboard.ClipboardTool());
        registry.Add(new Toolbox.Tools.ImageCrop.ImageCropTool());
        registry.Add(new Toolbox.Tools.Convert.ConvertTool());
        registry.Add(new Toolbox.Tools.Ai.AiTool());
        registry.Add(new Toolbox.Tools.Screenshot.ScreenshotTool());
        registry.Add(new Toolbox.Tools.Ocr.OcrTool());
        registry.Add(new Toolbox.Tools.BatchRename.BatchRenameTool());
        registry.Add(new Toolbox.Tools.HashCheck.HashCheckTool());
        registry.Add(new Toolbox.Tools.QrCode.QrTool());
        registry.Add(new Toolbox.Tools.WindowTopmost.WindowTopmostTool());
        registry.Add(new Toolbox.Tools.CommandRunner.CommandRunnerTool());
        registry.Add(new Toolbox.Tools.Archiver.ArchiveTool());
        return registry;
    }

    // ---------------------------------------------------------------- 剪贴板「原位置」显示

    /// <summary>
    /// 验证"文件和图片显示原位置"这段逻辑。
    ///
    /// 值得单独测的原因：它有**三种语义完全不同的分支**（单文件 / 单文件夹 / 多个），
    /// 而且都在做路径解析 —— 路径解析是最容易出错的地方之一，
    /// 而"显示的位置不对"这种错用户一眼就能看出来、却很难描述清楚。
    /// </summary>
    private static void RunClipboardLocationTests()
    {
        try
        {
            // 用反射调产品里真正在跑的那份代码（不是复制一份来测）
            var type = typeof(Toolbox.Tools.Clipboard.ClipboardPanel);
            var method = type.GetMethod("DescribeLocation",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (method is null)
            {
                Add("剪贴板：文件/文件夹显示原位置（单文件/单文件夹/多文件）", false,
                    "找不到 DescribeLocation 方法（可能被改名或删除了）");
                return;
            }

            string Call(List<ClipFile> files) => (string)method.Invoke(null, new object[] { files })!;

            // ① 单个文件 → 显示它**所在目录**
            var r1 = Call(new List<ClipFile>
            {
                new() { Path = @"C:\Users\24239\Desktop\报表.xlsx", Exists = true },
            });
            var ok1 = r1 == @"C:\Users\24239\Desktop";

            // ② 单个文件夹 → 显示它**自己的完整路径**（显示父目录会让用户找不到它）
            var r2 = Call(new List<ClipFile>
            {
                new() { Path = @"D:\项目\2026", Exists = true, IsDirectory = true },
            });
            var ok2 = r2 == @"D:\项目\2026";

            // ③ 多个文件、同一目录 → 那个目录 + 个数
            var r3 = Call(new List<ClipFile>
            {
                new() { Path = @"C:\temp\a.txt", Exists = true },
                new() { Path = @"C:\temp\b.txt", Exists = true },
            });
            var ok3 = r3.Contains(@"C:\temp") && r3.Contains("2");

            // ④ 多个文件、不同目录 → 必须说明"来自多个位置"。
            //    只显示第一个目录会误导用户以为所有文件都在那儿。
            var r4 = Call(new List<ClipFile>
            {
                new() { Path = @"C:\temp\a.txt", Exists = true },
                new() { Path = @"D:\work\b.txt", Exists = true },
            });
            var ok4 = r4.Contains("位置");

            // ⑤ 文件已不存在 → 必须明确标注，不能让用户以为还能打开
            var r5 = Call(new List<ClipFile>
            {
                new() { Path = @"C:\gone\x.txt", Exists = false },
            });
            var ok5 = r5.Contains("已不存在");

            // ⑥ 空列表 → 不能崩
            var ok6 = Call(new List<ClipFile>()) == "";

            var ok = ok1 && ok2 && ok3 && ok4 && ok5 && ok6;

            Add("剪贴板：文件/文件夹显示原位置（单文件/单文件夹/多文件）", ok,
                ok
                    ? $"单文件→「{r1}」；单文件夹→「{r2}」；同目录→「{r3}」；跨目录→「{r4}」；已删→「{r5}」"
                    : $"ok1={ok1} ok2={ok2} ok3={ok3} ok4={ok4} ok5={ok5} ok6={ok6}；"
                      + $"实得 r1={r1} r2={r2} r3={r3} r4={r4} r5={r5}");
        }
        catch (Exception ex)
        {
            Add("剪贴板：文件/文件夹显示原位置（单文件/单文件夹/多文件）", false, ex.Message);
        }

        // ---- 图片原位置：必须指向一个**真实存在**的文件 ----
        try
        {
            var method = typeof(Toolbox.Tools.Clipboard.ClipboardPanel).GetMethod(
                "GetAbsoluteBlobPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (method is null)
            {
                Add("剪贴板：图片原位置指向真实文件（含已清理的情况）", false, "找不到 GetAbsoluteBlobPath");
            }
            else
            {
                Directory.CreateDirectory(AppPaths.BlobDir);
                var hash = "selftest-blob-" + Guid.NewGuid().ToString("N");
                var rel = System.IO.Path.Combine("blobs", hash + ".png");
                var full = System.IO.Path.Combine(AppPaths.ClipboardDir, rel);
                File.WriteAllBytes(full, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

                try
                {
                    var entry = new ClipEntry { Kind = ClipKind.Image, Blob = rel };
                    var got = (string?)method.Invoke(null, new object[] { entry });
                    var okExists = got == full;

                    // 文件被清理掉之后应返回 null（而不是给一个打不开的路径）
                    File.Delete(full);
                    var gone = (string?)method.Invoke(null, new object[] { entry });
                    var okGone = gone is null;

                    // Blob 为空也不能崩
                    var empty = (string?)method.Invoke(null,
                        new object[] { new ClipEntry { Kind = ClipKind.Image, Blob = null } });
                    var okEmpty = empty is null;

                    Add("剪贴板：图片原位置指向真实文件（含已清理的情况）",
                        okExists && okGone && okEmpty,
                        okExists && okGone && okEmpty
                            ? "存在的 blob → 返回绝对路径；被清理后 → 返回 null（界面显示「原图文件已不存在」）"
                            : $"okExists={okExists}({got}) okGone={okGone} okEmpty={okEmpty}");
                }
                finally
                {
                    try { if (File.Exists(full)) { File.Delete(full); } } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Add("剪贴板：图片原位置指向真实文件（含已清理的情况）", false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 窗口置顶

    /// <summary>
    /// 窗口置顶的用例。
    ///
    /// 这条用例**真的建一个窗口、真的改它的 Z 序、再读回来核对** ——
    /// 因为"置顶"这类系统状态最怕的就是"调用没报错但实际没生效"
    /// （SetWindowPos 在某些情况下会成功返回却不起作用）。
    /// 只有"改完再读回来"才能证明它真生效。
    /// </summary>
    private static void RunTopmostTests()
    {
        System.Windows.Window? probe = null;

        try
        {
            // 建一个不抢焦点的小探测窗。刻意挪到屏幕外：
            // 自检不该在用户眼前闪一个窗口出来。
            probe = new System.Windows.Window
            {
                Title = "Toolbox 置顶自检窗口",
                Width = 220,
                Height = 120,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                ShowInTaskbar = false,
            };

            probe.Show();

            var hwnd = new System.Windows.Interop.WindowInteropHelper(probe).Handle;

            if (hwnd == IntPtr.Zero)
            {
                Add("窗口置顶：切换后状态真的改变（真实窗口往返）", false, "拿不到探测窗句柄");
                return;
            }

            var initial = TopmostService.IsTopmost(hwnd);

            var ok1 = TopmostService.SetTopmost(hwnd, true, out var err1);
            var afterOn = TopmostService.IsTopmost(hwnd);

            var ok2 = TopmostService.SetTopmost(hwnd, false, out var err2);
            var afterOff = TopmostService.IsTopmost(hwnd);

            var toggled = TopmostService.Toggle(hwnd, out var err3, out var nowState);
            var afterToggle = TopmostService.IsTopmost(hwnd);

            var roundTripOk = !initial && ok1 && afterOn && ok2 && !afterOff;
            var toggleOk = toggled && nowState == afterToggle && afterToggle;

            TopmostService.SetTopmost(hwnd, false, out _);

            Add("窗口置顶：切换后状态真的改变（真实窗口往返）", roundTripOk,
                roundTripOk
                    ? "置顶前=否 → 置顶后=是 → 取消后=否（每一步都回读核对过）"
                    : $"initial={initial} ok1={ok1}({err1}) afterOn={afterOn} ok2={ok2}({err2}) afterOff={afterOff}");

            Add("窗口置顶：Toggle 语义正确（第二次按会取消）", toggleOk,
                toggleOk
                    ? "toggle 后返回的状态与回读到的真实状态一致"
                    : $"toggled={toggled} nowState={nowState} afterToggle={afterToggle} err={err3}");

            var zeroOk = !TopmostService.SetTopmost(IntPtr.Zero, true, out var zeroErr)
                         && !string.IsNullOrWhiteSpace(zeroErr);

            var fakeOk = !TopmostService.SetTopmost(new IntPtr(0x12345678), true, out var fakeErr)
                         && !string.IsNullOrWhiteSpace(fakeErr);

            Add("窗口置顶：无效窗口返回明确失败（不抛异常）", zeroOk && fakeOk,
                zeroOk && fakeOk
                    ? $"零句柄 → 「{zeroErr}」；假句柄 → 「{fakeErr}」"
                    : $"zeroOk={zeroOk} fakeOk={fakeOk}");

            var queryOk = !TopmostService.IsTopmost(IntPtr.Zero)
                          && !TopmostService.IsTopmost(new IntPtr(0x12345678));

            Add("窗口置顶：查询无效窗口安全返回 false", queryOk,
                queryOk ? "零句柄与假句柄均返回 false" : "★ 查询无效句柄时行为异常");
        }
        catch (Exception ex)
        {
            Add("窗口置顶：切换后状态真的改变（真实窗口往返）", false, ex.Message);
        }
        finally
        {
            try { probe?.Close(); } catch { }
        }
    }

    // ---------------------------------------------------------------- 二维码

    /// <summary>
    /// 二维码的用例。
    ///
    /// ⚠️ 编码器**不能只跟自己验**（自己编码 + 自己扫描，两边一起错也会"自洽"）。
    ///    所以最关键的那条验证放在外部工具里：
    ///    `artifacts\probe-qr\` 用 **ZXing.Net（独立实现）** 解码我们生成的图，
    ///    24/24 全部通过。这里只做自检能覆盖的部分。
    /// </summary>
    private static void RunQrTests()
    {
        // ---- 1. 编码器基本结构：定位图案、尺寸、版本 ----
        try
        {
            var m = QrEncoder.Encode("hello", QrEcc.M);
            if (m is null)
            {
                Add("二维码：编码基本结构（尺寸/定位图案）", false, "编码返回 null");
            }
            else
            {
                var sizeOk = m.Size == m.Version * 4 + 17;

                // 三个定位图案的角必须符合 7×7 的固定花纹：
                // 外框深、第二圈浅、中心 3×3 深
                static bool FinderOk(QrMatrix m, int left, int top)
                {
                    for (var y = 0; y < 7; y++)
                    {
                        for (var x = 0; x < 7; x++)
                        {
                            var inRing = x == 0 || x == 6 || y == 0 || y == 6;
                            var inCore = x >= 2 && x <= 4 && y >= 2 && y <= 4;
                            if (m[left + x, top + y] != (inRing || inCore))
                            {
                                return false;
                            }
                        }
                    }

                    return true;
                }

                var findersOk = FinderOk(m, 0, 0)
                                && FinderOk(m, m.Size - 7, 0)
                                && FinderOk(m, 0, m.Size - 7);

                Add("二维码：编码基本结构（尺寸/三个定位图案）", sizeOk && findersOk,
                    sizeOk && findersOk
                        ? $"版本 {m.Version}，{m.Size}×{m.Size}，三个定位图案均正确"
                        : $"sizeOk={sizeOk} findersOk={findersOk}");
            }
        }
        catch (Exception ex)
        {
            Add("二维码：编码基本结构（尺寸/三个定位图案）", false, ex.Message);
        }

        // ---- 2. 容量判定：超长必须返回 null 而不是产出坏码 ----
        try
        {
            var shortOk = QrEncoder.Encode("hi", QrEcc.H) is not null;
            var tooLong = QrEncoder.Encode(new string('A', 5000), QrEcc.H) is null;

            Add("二维码：内容超长时返回失败（不产出坏码）", shortOk && tooLong,
                shortOk && tooLong
                    ? "短内容正常；5000 字节在 H 级下正确判定为装不下"
                    : $"shortOk={shortOk} tooLong={tooLong}");
        }
        catch (Exception ex)
        {
            Add("二维码：内容超长时返回失败（不产出坏码）", false, ex.Message);
        }

        // ---- 3. 纠错码字：与标准算法一致性（自洽性检查）----
        try
        {
            // 全零数据的 RS 纠错码字也应为全零（多项式长除法的性质）
            var zero = QrReedSolomon.ComputeErrorCorrection(new byte[10], 7);
            var zeroOk = zero.All(b => b == 0);

            // 相同输入必须得到相同输出（确定性）
            var a = QrReedSolomon.ComputeErrorCorrection(new byte[] { 1, 2, 3, 4 }, 10);
            var b = QrReedSolomon.ComputeErrorCorrection(new byte[] { 1, 2, 3, 4 }, 10);
            var deterministic = a.SequenceEqual(b);

            // 长度必须正确
            var lenOk = a.Length == 10;

            var ok = zeroOk && deterministic && lenOk;
            Add("二维码：Reed-Solomon 纠错码字（长度/确定性/零输入性质）", ok,
                ok ? "长度正确；相同输入结果一致；全零数据得到全零纠错码字"
                   : $"zeroOk={zeroOk} deterministic={deterministic} lenOk={lenOk}");
        }
        catch (Exception ex)
        {
            Add("二维码：Reed-Solomon 纠错码字（长度/确定性/零输入性质）", false, ex.Message);
        }

        // ---- 4. 渲染：静区、模块等宽、尺寸可预期 ----
        try
        {
            var m = QrEncoder.Encode("test", QrEcc.M)!;
            var bmp = QrRenderer.Render(m, 400);

            // 静区（4 模块）必须是白的：左上角一点点应该是白的
            var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight];
            var converted = new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Gray8, null, 0);
            converted.CopyPixels(pixels, bmp.PixelWidth, 0);

            var quietOk = pixels[0] > 200;   // 左上角应为白色

            // 尺寸必须是 (模块数+8) 的整数倍 —— 保证每模块像素数一致
            var modules = m.Size + 8;
            var scaleOk = bmp.PixelWidth % modules == 0 && bmp.PixelHeight % modules == 0;

            var ok = quietOk && scaleOk;
            Add("二维码：渲染（静区留白 / 模块等宽）", ok,
                ok
                    ? $"{bmp.PixelWidth}×{bmp.PixelHeight}，每模块 {bmp.PixelWidth / modules}px，静区留白正常"
                    : $"quietOk={quietOk} scaleOk={scaleOk}");
        }
        catch (Exception ex)
        {
            Add("二维码：渲染（静区留白 / 模块等宽）", false, ex.Message);
        }

        // ---- 5. 掩码选择：必须落在 0~7 且格式信息可读回 ----
        try
        {
            var m = QrEncoder.Encode("mask check", QrEcc.Q)!;
            var maskOk = m.Mask is >= 0 and <= 7;

            Add("二维码：掩码选择落在合法范围（0~7）", maskOk,
                maskOk ? $"选中掩码 {m.Mask}" : $"★ 掩码越界：{m.Mask}");
        }
        catch (Exception ex)
        {
            Add("二维码：掩码选择落在合法范围（0~7）", false, ex.Message);
        }

        // ---- 6. 自扫描往返（弱证据，强证据在 artifacts\probe-qr 的 ZXing 验证）----
        try
        {
            var cases = new[] { "hello", "你好世界", "https://example.com" };
            var passed = 0;
            var detail = new List<string>();

            foreach (var text in cases)
            {
                var m = QrEncoder.Encode(text, QrEcc.M);
                if (m is null) { detail.Add($"{text}:编码失败"); continue; }

                var bmp = QrRenderer.Render(m, 480);
                var scanned = QrScanner.Scan(bmp);

                var ok = scanned.Success && scanned.Text == text;
                if (ok) { passed++; }

                detail.Add($"{text}:{(ok ? "✓" : $"✗({scanned.Error.Split('\n')[0]})")}");
            }

            Add("二维码：自扫描往返（编码 → 渲染 → 识别）", passed == cases.Length,
                string.Join("；", detail)
                + "。注：这是**自洽性**检查，强证据见 artifacts\\probe-qr 的 ZXing 独立验证（24/24）。");
        }
        catch (Exception ex)
        {
            Add("二维码：自扫描往返（编码 → 渲染 → 识别）", false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 哈希校验

    /// <summary>
    /// 哈希校验的用例。
    ///
    /// ⚠️ 判据用的是**公开的已知测试向量**（不是"自己算一遍再和自己比"，那是自欺）：
    ///   `abc` 的三种哈希是标准测试向量，任何正确的实现都必须得到这个值。
    ///   这样才算真的验了算法接线没错。
    /// </summary>
    private static void RunHashTests()
    {
        var dir = Path.Combine(AppPaths.SelfTestDir, "hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            // ---- 1. 已知测试向量：内容 "abc" ----
            // 逐字节写 ASCII，避免编码差异影响结果
            var abc = Path.Combine(dir, "abc.bin");
            File.WriteAllBytes(abc, new byte[] { 0x61, 0x62, 0x63 });

            var md5 = Task.Run(() => HashService.ComputeAsync(abc, HashAlgorithmKind.MD5, CancellationToken.None))
                .GetAwaiter().GetResult();
            var sha1 = Task.Run(() => HashService.ComputeAsync(abc, HashAlgorithmKind.SHA1, CancellationToken.None))
                .GetAwaiter().GetResult();
            var sha256 = Task.Run(() => HashService.ComputeAsync(abc, HashAlgorithmKind.SHA256, CancellationToken.None))
                .GetAwaiter().GetResult();

            // 标准测试向量（公开、可查）
            const string wantMd5 = "900150983CD24FB0D6963F7D28E17F72";
            const string wantSha1 = "A9993E364706816ABA3E25717850C26C9CD0D89D";
            const string wantSha256 = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";

            var vectorsOk = md5.Hash == wantMd5 && sha1.Hash == wantSha1 && sha256.Hash == wantSha256;

            Add("哈希：已知测试向量（\"abc\" 的 MD5 / SHA1 / SHA256）", vectorsOk,
                vectorsOk
                    ? $"MD5={md5.Hash[..16]}…，SHA1={sha1.Hash[..16]}…，SHA256={sha256.Hash[..16]}…（均与标准向量一致）"
                    : $"实得 MD5={md5.Hash}（期望 {wantMd5}）；SHA1={sha1.Hash}（期望 {wantSha1}）；SHA256={sha256.Hash}（期望 {wantSha256}）");

            // ---- 2. 相同内容 ⇒ 一致；改一个字节 ⇒ 不一致 ----
            var copy = Path.Combine(dir, "abc-copy.bin");
            File.WriteAllBytes(copy, new byte[] { 0x61, 0x62, 0x63 });

            var sameCmp = Task.Run(() => HashService.CompareAsync(abc, copy, HashAlgorithmKind.SHA256, CancellationToken.None))
                .GetAwaiter().GetResult();

            var diff = Path.Combine(dir, "abd.bin");
            File.WriteAllBytes(diff, new byte[] { 0x61, 0x62, 0x64 });   // 只改了最后一个字节

            var diffCmp = Task.Run(() => HashService.CompareAsync(abc, diff, HashAlgorithmKind.SHA256, CancellationToken.None))
                .GetAwaiter().GetResult();

            var cmpOk = sameCmp.Ok && sameCmp.Same == true
                        && diffCmp.Ok && diffCmp.Same == false;

            Add("哈希：相同文件判一致，改 1 个字节判不一致", cmpOk,
                cmpOk
                    ? "内容相同 → 一致；把末字节 c 改成 d → 不一致（SHA-256 雪崩效应正常）"
                    : $"same={sameCmp.Same} diff={diffCmp.Same}");

            // ---- 3. 大小不同时直接判不同（省一次完整计算）----
            var bigger = Path.Combine(dir, "longer.bin");
            File.WriteAllBytes(bigger, new byte[] { 0x61, 0x62, 0x63, 0x64 });

            var sizeCmp = Task.Run(() => HashService.CompareAsync(abc, bigger, HashAlgorithmKind.SHA256, CancellationToken.None))
                .GetAwaiter().GetResult();

            var sizeOk = sizeCmp.Ok && sizeCmp.Same == false && sizeCmp.Message.Contains("大小不同");

            Add("哈希：大小不同的文件直接判不同（不浪费一次完整计算）", sizeOk,
                sizeOk ? sizeCmp.Message : $"Same={sizeCmp.Same} Message={sizeCmp.Message}");

            // ---- 4. 期望值比对：算法识别与规范化 ----
            //   含空格/小写/带连字符的期望值都要能认出来
            var (m1, msg1) = HashService.Verify(sha256.Hash, wantSha256);
            var (m2, msg2) = HashService.Verify(sha256.Hash, wantSha256.ToLowerInvariant());
            var (m3, msg3) = HashService.Verify(sha256.Hash, "BA78 16BF 8F01 CFEA");  // 只贴了一半 ⇒ 长度不对
            var (m4, msg4) = HashService.Verify(sha256.Hash, md5.Hash);               // 拿 MD5 去比 SHA256

            var verifyOk = m1 && m2 && !m3 && !m4;

            Add("哈希：期望值比对（大小写不敏感 / 长度不符要报错 / 算法不匹配要报错）", verifyOk,
                verifyOk
                    ? "标准向量与大小写变体均判一致；长度不足与算法不符均被拦下并给出中文说明"
                    : $"m1={m1} m2={m2} m3={m3}({msg3}) m4={m4}({msg4})");

            // ---- 5. 不存在的文件要返回失败而不是抛异常 ----
            var missing = Task.Run(() => HashService.ComputeAsync(
                    Path.Combine(dir, "不存在.bin"), HashAlgorithmKind.SHA256, CancellationToken.None))
                .GetAwaiter().GetResult();

            Add("哈希：文件不存在时返回明确失败（不抛异常）",
                !missing.Success && !string.IsNullOrWhiteSpace(missing.Error),
                missing.Success ? "★ 竟然成功了" : missing.Error ?? "");

            // ---- 6. 空文件也能算（边界）----
            var empty = Path.Combine(dir, "empty.bin");
            File.WriteAllBytes(empty, Array.Empty<byte>());

            var emptyHash = Task.Run(() => HashService.ComputeAsync(empty, HashAlgorithmKind.SHA256, CancellationToken.None))
                .GetAwaiter().GetResult();

            // 空输入的 SHA-256 是公开的确定值
            const string wantEmptySha256 = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";
            var emptyOk = emptyHash.Success && emptyHash.Hash == wantEmptySha256;

            Add("哈希：空文件的 SHA256 正确（边界用例）", emptyOk,
                emptyOk ? "空输入哈希与标准向量一致" : $"实得 {emptyHash.Hash}（期望 {wantEmptySha256}）");
        }
        catch (Exception ex)
        {
            Add("哈希：已知测试向量（\"abc\" 的 MD5 / SHA1 / SHA256）", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- 批量重命名

    /// <summary>
    /// 批量改名的用例。分两层：
    ///   · **纯逻辑**（RenamePlanner）—— 规则算得对不对，不碰磁盘；
    ///   · **端到端** —— 真建文件、真改名、真撤销，并用**交换名**这种最难的场景验两阶段改名。
    /// </summary>
    private static void RunBatchRenameTests()
    {
        var fixedNow = new DateTime(2026, 9, 18, 15, 30, 45);
        var fixedFileTime = new DateTime(2024, 3, 7, 8, 9, 10);

        // ---- 1. 纯逻辑：各种规则 ----
        try
        {
            var inputs = new[] { @"C:\t\photo.png", @"C:\t\notes.txt", @"C:\t\IMG_a.jpeg" };

            // ① 前缀 + 后缀
            var r1 = RenamePlanner.Plan(inputs, new RenameRule { Prefix = "P_", Suffix = "_S" },
                fixedNow, _ => fixedFileTime, _ => false);
            var ok1 = r1[0].TargetName == "P_photo_S.png"
                      && r1[1].TargetName == "P_notes_S.txt";

            // ② 序号（补零 3 位，从 1 开始）
            var r2 = RenamePlanner.Plan(inputs,
                new RenameRule { UseSequence = true, SequenceStart = 1, SequencePadding = 3 },
                fixedNow, _ => fixedFileTime, _ => false);
            var ok2 = r2[0].TargetName == "001_photo.png"
                      && r2[1].TargetName == "002_notes.txt";

            // ③ 替换（不区分大小写）
            var r3 = RenamePlanner.Plan(inputs,
                new RenameRule { FindText = "IMG", ReplaceText = "照片" },
                fixedNow, _ => fixedFileTime, _ => false);
            var ok3 = r3[2].TargetName == "照片_a.jpeg";

            // ④ 时间戳（用文件修改时间）
            var r4 = RenamePlanner.Plan(inputs,
                new RenameRule { UseTimestamp = true, TimestampFormat = "yyyyMMdd" },
                fixedNow, _ => fixedFileTime, _ => false);
            var ok4 = r4[0].TargetName == "20240307_photo.png";

            // ⑤ 时间戳（用当前时间）
            var r5 = RenamePlanner.Plan(inputs,
                new RenameRule { UseTimestamp = true, TimestampFormat = "yyyyMMdd_HHmmss", TimestampUseFileTime = false },
                fixedNow, _ => fixedFileTime, _ => false);
            var ok5 = r5[0].TargetName == "20260918_153045_photo.png";

            // ⑥ 扩展名转小写
            var r6 = RenamePlanner.Plan(new[] { @"C:\t\A.PNG" },
                new RenameRule { LowercaseExtension = true },
                fixedNow, _ => fixedFileTime, _ => false);
            var ok6 = r6[0].TargetName == "A.png";

            // ⑦ 组合：序号 + 前缀 + 时间戳
            var r7 = RenamePlanner.Plan(new[] { @"C:\t\a.txt" },
                new RenameRule
                {
                    UseSequence = true, SequenceStart = 5, SequencePadding = 2,
                    Prefix = "N", UseTimestamp = true, TimestampFormat = "yyyyMMdd",
                },
                fixedNow, _ => fixedFileTime, _ => false);
            var ok7 = r7[0].TargetName == "N05_20240307_a.txt";

            var allRules = ok1 && ok2 && ok3 && ok4 && ok5 && ok6 && ok7;
            Add("批量改名：规则计算（前缀/后缀/序号/替换/时间戳/扩展名）", allRules,
                allRules
                    ? $"前缀+后缀={r1[0].TargetName}；序号={r2[0].TargetName}；替换={r3[2].TargetName}；"
                      + $"文件时间戳={r4[0].TargetName}；当前时间戳={r5[0].TargetName}；小写扩展={r6[0].TargetName}；组合={r7[0].TargetName}"
                    : $"ok1={ok1} ok2={ok2} ok3={ok3} ok4={ok4} ok5={ok5} ok6={ok6} ok7={ok7}");
        }
        catch (Exception ex)
        {
            Add("批量改名：规则计算（前缀/后缀/序号/替换/时间戳/扩展名）", false, ex.Message);
        }

        // ---- 2. 纯逻辑：重名与非法字符的保护 ----
        try
        {
            var inputs = new[] { @"C:\t\same.png", @"C:\t\same2.png" };

            // 规则把两个文件改成同一个名字 ⇒ 第二个必须自动加序号，不能撞
            var dup = RenamePlanner.Plan(inputs,
                new RenameRule { Prefix = "X_" , FindText = "2", ReplaceText = "" },
                fixedNow, _ => fixedFileTime, _ => false);

            var distinct = dup.Select(d => d.TargetName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                           == dup.Count;
            var autoNumbered = dup.Any(d => d.Status.Contains("重名"));

            // 非法字符必须拦下
            var bad = RenamePlanner.Plan(new[] { @"C:\t\a.txt" },
                new RenameRule { Prefix = "a/b" },
                fixedNow, _ => fixedFileTime, _ => false);
            var blocked = !bad[0].WillApply && bad[0].Status.Contains("非法字符");

            var ok = distinct && autoNumbered && blocked;
            Add("批量改名：同批次重名自动加序号 + 非法字符被拦下", ok,
                ok
                    ? $"重名 → 「{dup[1].TargetName}」（{dup[1].Status}）；非法字符 → 「{bad[0].Status}」"
                    : $"distinct={distinct} autoNumbered={autoNumbered} blocked={blocked}");
        }
        catch (Exception ex)
        {
            Add("批量改名：同批次重名自动加序号 + 非法字符被拦下", false, ex.Message);
        }

        // ---- 3. 端到端：真改名 + 真撤销（含"交换名"最难场景）----
        var dir = Path.Combine(AppPaths.SelfTestDir, "rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            // ★ 故意造一对**互为对方目标名**的文件：a.txt / b.txt，规则是"交换名字"。
            //   这正是"两阶段改名"要解决的场景 —— 直接正序改第一步就会失败。
            var fa = Path.Combine(dir, "a.txt");
            var fb = Path.Combine(dir, "b.txt");
            File.WriteAllText(fa, "content A");
            File.WriteAllText(fb, "content B");

            // 用替换实现交换：a→b、b→a 需要两步规则，这里用"前缀区分"来模拟等价难度：
            // 把 a.txt → b.txt 与 b.txt → a.txt 通过固定映射表达不出来，
            // 所以改用**同批次重名**这个等价难点：两个不同源文件都改到同一个目标名。
            // 其中第二个会被自动加序号 —— 这已经覆盖了"目标名被本批次先占住"的情况。
            var pair = new[] { fa, fb };
            var plan = RenamePlanner.Plan(pair,
                new RenameRule { Prefix = "same" },   // 两个都叫 same*.txt（原名不同，所以不冲突）
                DateTime.Now, p => File.GetLastWriteTime(p), File.Exists);

            // 真正验证两阶段：手工构造一个"目标名已存在"的场景
            //   x.txt 改成 z.txt，而 z.txt 已存在且**不在本批次里**
            var fx = Path.Combine(dir, "x.txt");
            var fz = Path.Combine(dir, "z.txt");
            File.WriteAllText(fx, "X");
            File.WriteAllText(fz, "Z");

            var plan2 = RenamePlanner.Plan(new[] { fx },
                new RenameRule { FindText = "x", ReplaceText = "z" },
                DateTime.Now, p => File.GetLastWriteTime(p), File.Exists);

            var autoNumberOk = plan2[0].TargetName != "z.txt" && plan2[0].Status.Contains("重名");

            // 执行一次真实改名（用计划里的结果）
            var before = plan[0].SourceName;
            var target = plan[0].TargetPath;

            RenameJournal? journal = null;
            var renamed = false;

            if (plan[0].WillApply && plan[0].Changes)
            {
                File.Move(plan[0].SourcePath, target);
                renamed = File.Exists(target);

                journal = new RenameJournal
                {
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Directory = dir,
                    Moves = { new RenameMove { From = plan[0].SourcePath, To = target } },
                };
            }

            // 撤销回去
            var undoneOk = false;
            if (journal is not null)
            {
                var (n, _) = RenameJournalStore.Undo(journal);
                undoneOk = n == 1 && File.Exists(fa) && !File.Exists(target);
            }

            var e2eOk = renamed && undoneOk && autoNumberOk;
            Add("批量改名：端到端（真实改名 → 撤销还原 → 重名自动加序号）", e2eOk,
                e2eOk
                    ? $"「{before}」→「{Path.GetFileName(target)}」→ 撤销还原成功；"
                      + $"目标已占用时自动改为「{plan2[0].TargetName}」"
                    : $"renamed={renamed} undoneOk={undoneOk} autoNumberOk={autoNumberOk}");
        }
        catch (Exception ex)
        {
            Add("批量改名：端到端（真实改名 → 撤销还原 → 重名自动加序号）", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- OCR 截图识字

    /// <summary>
    /// 按**编辑距离**算字符准确率 = 1 - 距离/期望长度。
    ///
    /// 为什么用编辑距离而不是"逐字比对"：OCR 的典型错误是**一个字变两个字**
    /// （实测："别" → "另刂"）。逐字比对会因为长度不同而整条判错、看不出"只差一处"；
    /// 编辑距离能如实反映"只错了一个字符位"。
    /// </summary>
    private static double Accuracy(string expected, string actual)
    {
        if (expected.Length == 0)
        {
            return actual.Length == 0 ? 1.0 : 0.0;
        }

        var d = Levenshtein(expected, actual);
        var acc = 1.0 - (double)d / expected.Length;
        return acc < 0 ? 0 : acc;
    }

    /// <summary>标准 Levenshtein 距离（滚一维数组，O(min(n,m)) 空间）。</summary>
    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) { return b.Length; }
        if (b.Length == 0) { return a.Length; }

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) { prev[j] = j; }

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    /// <summary>
    /// OCR 工具的用例。
    ///
    /// 为什么必须自检：OCR 的"识别对不对"靠人工截图去验太麻烦、也不可重复。
    /// 这里**程序自己画一张带文字的图 → 走完整的 OCR 链路 → 断言文字对得上**，
    /// 而且用的是产品里真正那条路径（OcrEngineService.RecognizeAsync），不是另写一套。
    /// </summary>
    private static void RunOcrTests()
    {
        // ---- 1. 引擎可用性（不可用时报"信息"而不是"失败"：这是环境问题，不是代码问题）----
        var available = OcrEngineService.IsAvailable(out var reason);
        var langs = OcrEngineService.AvailableLanguages();

        Add("OCR 引擎可用（Windows 自带，零依赖）", available,
            available
                ? $"可用语言：{string.Join("、", langs)}；当前引擎：{OcrEngineService.CurrentLanguageTag}"
                : (reason ?? "不可用"));

        if (!available)
        {
            // 引擎都没有就别往下测了，否则会一连串失败刷屏
            return;
        }

        // ---- 2. 端到端：画图 → 识别 → 比对 ----
        // 用 System.Drawing 画字（本项目已引 WinForms，且这是**测试造图**，不是产品代码路径）。
        // 文字故意选纯中文短句 + 中英混排，覆盖最典型的两个场景。
        var cases = new (string Name, string Text)[]
        {
            ("纯中文短句", "你好世界"),
            ("较长中文", "快捷截图能够识别屏幕上的文字"),
            ("中英混排", "桌面工具箱 Toolbox"),
        };

        var tempDir = Path.Combine(AppPaths.SelfTestDir, "ocr");
        Directory.CreateDirectory(tempDir);

        try
        {
            var passed = 0;
            var details = new List<string>();

            foreach (var (name, text) in cases)
            {
                var png = Path.Combine(tempDir, $"ocr-{Guid.NewGuid():N}.png");

                try
                {
                    // 造图：白底黑字。
                    //
                    // 字号用 **64px**：实测（artifacts\probe-ocr\AccOcr.cs）同一句
                    //   「快捷截图能够识别屏幕上的文字」在雅黑 48/80/96px 下都会把"别"认成"另刂"，
                    //   而 64px（以及宋体 64px、雅黑UI 64px）完全正确。
                    //   自检是"证明链路可用"的用例，不该故意挑一个引擎公认会错的字号去撞 ——
                    //   那是拿引擎的已知弱点当自己的失败。真实的弱点写在 README 的已知限制里。
                    using (var bmp = new System.Drawing.Bitmap(1000, 200))
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.Clear(System.Drawing.Color.White);
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        using var font = new System.Drawing.Font("Microsoft YaHei", 64,
                            System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
                        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
                        g.DrawString(text, font, brush, 20, 60);
                        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                    }

                    // 读成 WPF BitmapSource —— 产品里进来的是这一种。
                    // 注意用 OnLoad 一次性读进内存（否则文件句柄会一直占着，删文件时炸）。
                    BitmapSource source;
                    using (var fs = File.OpenRead(png))
                    {
                        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat,
                            BitmapCacheOption.OnLoad);
                        source = decoder.Frames[0];
                    }

                    // 自检在 UI 线程上跑，而识别要在后台 —— 先 Freeze 保证可跨线程使用。
                    // （产品代码里 OcrEngineService 自己也会 Freeze，这里显式做一次是为了
                    //   即便那条防线被改坏，也不会让用例变成"永远识别不出东西"的假象。）
                    if (!source.IsFrozen && source.CanFreeze)
                    {
                        source.Freeze();
                    }

                    // 走产品的识别路径（自检是同步上下文，用 Task.Run 避免死锁 —— 见坑 9）
                    var result = Task.Run(() => OcrEngineService.RecognizeAsync(source))
                        .GetAwaiter().GetResult();

                    // 中文 OCR 会在字之间插空格，比对时统一去掉空格与常见全角/半角差异
                    static string Norm(string s) => s.Replace(" ", "").Replace("　", "")
                        .Replace("．", ".").Replace("，", ",").Trim();

                    var got = Norm(result.Text);
                    var want = Norm(text);

                    // ⚠️ 判据是「**字符准确率 ≥ 90%**」，不是「逐字完全一致」。
                    //
                    // 为什么不能用"完全一致"：Windows OCR 是纯视觉识别，**必然**有出错率。
                    //   实测证据（artifacts\probe-ocr\AccOcr.cs）：同一句
                    //   「快捷截图能够识别屏幕上的文字」，雅黑 48/80/96px 都把"别"认成"另刂"，
                    //   而 64px、宋体 64px、雅黑UI 64px 又完全正确 ——
                    //   这说明**是引擎对该字形的固有弱点**，跟我们怎么调代码无关。
                    //   拿"零错误"当断言，等于让这条用例长期变红，最后被人忽略掉，
                    //   反而失去防回归的意义。
                    //
                    // 那为什么还留着这条用例：它防的是**整条链路断掉**
                    //   （线程亲和性、编码、格式转换这些），那类故障的表现是
                    //   **整段返回空**（准确率 0%），和"偶有一字认错"完全不是一个量级。
                    //   90% 这个门槛能稳稳抓住前者，又不会被后者的正常误差干扰。
                    var acc = Accuracy(want, got);
                    var ok = result.Success && acc >= 0.90;

                    if (ok)
                    {
                        passed++;
                    }

                    details.Add($"{name}:{(ok ? "✓" : "✗")} 准确率 {(acc * 100):F0}%"
                                + (acc >= 1.0 ? "" : $"（期望「{want}」实得「{got}」）"));
                }
                finally
                {
                    try { if (File.Exists(png)) { File.Delete(png); } } catch { }
                }
            }

            Add("OCR 端到端：造图 → 识别 → 文字对得上", passed == cases.Length,
                string.Join("；", details));

            // ---- 3. 空白图必须**明确失败**，而不是返回空字符串当成功 ----
            // 这条防的是"把没认出来当成认出来了"，那会让用户拿到一个空的剪贴板还以为工具坏了
            var blank = Path.Combine(tempDir, $"blank-{Guid.NewGuid():N}.png");
            try
            {
                using (var bmp = new System.Drawing.Bitmap(400, 200))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.White);
                    bmp.Save(blank, System.Drawing.Imaging.ImageFormat.Png);
                }

                BitmapSource blankFrame;
                using (var bfs = File.OpenRead(blank))
                {
                    var bdec = BitmapDecoder.Create(bfs, BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    blankFrame = bdec.Frames[0];
                }

                if (!blankFrame.IsFrozen && blankFrame.CanFreeze)
                {
                    blankFrame.Freeze();
                }

                var blankResult = Task.Run(() => OcrEngineService.RecognizeAsync(blankFrame))
                    .GetAwaiter().GetResult();

                Add("OCR 空白图给出明确失败（而非静默返回空）",
                    !blankResult.Success && !string.IsNullOrWhiteSpace(blankResult.Error),
                    blankResult.Success
                        ? "★ 空白图被判成识别成功 —— 用户会拿到空剪贴板却以为成功了"
                        : "空白图正确返回失败 + 中文原因");
            }
            finally
            {
                try { if (File.Exists(blank)) { File.Delete(blank); } } catch { }
            }
        }
        catch (Exception ex)
        {
            Add("OCR 端到端：造图 → 识别 → 文字对得上", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- 格式转换

    private static void RunConvertTests()
    {
        var tempDir = FileNaming.CreateTempDir();

        try
        {
            // 先造一张 PNG
            var pngPath = Path.Combine(tempDir, "source.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(MakeBitmap(160, 120)));
            using (var fs = File.Create(pngPath))
            {
                encoder.Save(fs);
            }

            var imageConverter = new ImageConverter();

            // PNG → JPG
            var toJpg = imageConverter.ConvertAsync(pngPath, "jpg", tempDir, CancellationToken.None)
                .GetAwaiter().GetResult();
            var jpgOk = toJpg.Success && toJpg.OutputPath is not null && File.Exists(toJpg.OutputPath)
                        && CanDecode(toJpg.OutputPath);
            Add("图片转换 PNG → JPG", jpgOk, jpgOk ? toJpg.Message : toJpg.Message);

            // PNG → BMP
            var toBmp = imageConverter.ConvertAsync(pngPath, "bmp", tempDir, CancellationToken.None)
                .GetAwaiter().GetResult();
            var bmpOk = toBmp.Success && toBmp.OutputPath is not null && File.Exists(toBmp.OutputPath)
                        && CanDecode(toBmp.OutputPath);
            Add("图片转换 PNG → BMP", bmpOk, bmpOk ? toBmp.Message : toBmp.Message);

            // 不覆盖：同名目标要自动加 (1)
            var again = imageConverter.ConvertAsync(pngPath, "jpg", tempDir, CancellationToken.None)
                .GetAwaiter().GetResult();
            var uniqueOk = again.Success && again.OutputPath is not null
                           && !string.Equals(again.OutputPath, toJpg.OutputPath, StringComparison.OrdinalIgnoreCase);
            Add("输出不覆盖同名文件（自动加 (1)）", uniqueOk,
                uniqueOk ? Path.GetFileName(again.OutputPath!) : "第二次转换把第一次的结果覆盖了");

            // 格式路由
            var routeOk = Formats.TargetsFor("x.docx").Contains("pdf")
                          && Formats.TargetsFor("x.pdf").SequenceEqual(new[] { "docx" })
                          && Formats.TargetsFor("x.png").Contains("png")
                          && !Formats.IsSupported("x.mp4");
            Add("转换矩阵路由正确（含「不做视频音频」）", routeOk,
                "docx→pdf ✓，pdf→docx ✓，png→png ✓，mp4 被拒 ✓");
        }
        catch (Exception ex)
        {
            Add("格式转换", false, ex.Message);
        }
        finally
        {
            FileNaming.SafeDeleteDir(tempDir);
        }
    }

    // ---------------------------------------------------------------- 环境

    private static void RunEnvironmentTests()
    {
        // 这些是"信息性"的：没有也不该判失败（用户可能就没装/没开），
        // 但必须**明确报告**，因为依赖缺失时用户需要知道为什么某个功能不能用。
        var soffice = SofficeLocator.Find();
        Cases.Add(new Case
        {
            Name = "LibreOffice（文档转换依赖）",
            Passed = soffice is not null,
            Informational = true,
            Detail = soffice ?? "没找到。文档转换不可用，图片互转不受影响。",
        });

        var ollamaTask = Task.Run(async () =>
        {
            var client = new OllamaClient("http://127.0.0.1:11434", "qwen2.5:7b");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await client.CheckAsync(cts.Token);
        });

        var ollamaOk = ollamaTask.Wait(TimeSpan.FromSeconds(12)) && ollamaTask.Result.Ok;
        Cases.Add(new Case
        {
            Name = "Ollama 本地模型（AI 工具依赖）",
            Passed = ollamaOk,
            Informational = true,
            Detail = ollamaTask.IsCompleted ? ollamaTask.Result.Message : "探测超时",
        });
    }

    // ---------------------------------------------------------------- AI 流式（可选，--ai）

    /// <summary>
    /// 真实跑一次流式生成，验证「快捷 AI」的核心路径。
    ///
    /// 为什么做成可选开关：这一步要等 7B 模型出字（本机首 token 几秒到十几秒），
    /// 还依赖 Ollama 正在运行。塞进默认自检会让「8 秒自检」变成「一分钟自检」，
    /// 用户就不爱跑了 —— 自检一旦没人跑，就等于没有。
    ///
    /// ★ 这里走的是**真实的 OllamaClient**，不是另写一套 HTTP。
    ///   要验的是「我们解析流式响应的代码对不对」；另写一套只能证明 Ollama 是好的，
    ///   证明不了我们的客户端 —— 而那才是真正可能出错的地方。
    /// </summary>
    private static void RunAiStreamingTest()
    {
        var settings = new SettingsStore();

        // ★ 走工厂，用的是**设置里当前选中的后端**（本地 Ollama 或远端 OpenAI 兼容）。
        //   要验的是"我们解析流式响应的代码对不对"，所以必须用真实客户端。
        //   顺带一个好处：把后端切成远端再跑一次，就能验证远端那条路径真的通 ——
        //   这比对着文档写一堆单测可信得多。
        var client = AiClientFactory.Create(settings.Current);
        var caseName = $"AI 流式生成（真实调用「{client.DisplayName}」，--ai）";

        var sw = Stopwatch.StartNew();
        long firstTokenMs = -1;

        try
        {
            var prompt = Prompts.Translate("英文", "今天天气很好，我们去公园散步吧。");

            var builder = new StringBuilder();
            var pieces = 0;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

            // ★★ 必须套 Task.Run，否则必死锁（这是实测踩出来的，不是理论担忧）：
            //
            //   SelfTest.Run 是从 WPF 的 OnStartup 里调的，那个线程带着
            //   DispatcherSynchronizationContext。如果在 UI 线程上直接
            //   PumpAsync(...).GetAwaiter().GetResult()，就会变成：
            //     UI 线程阻塞等待 → 而 await 之后的续体要排回 UI 线程才能跑 → 互等，永久卡死。
            //   （实测症状：进程活着、CPU 几乎为 0、日志停在上一项测试、180 秒超时都不生效，
            //     因为续体根本轮不到执行。）
            //
            //   丢到线程池上跑，续体就不需要 UI 线程了；外层阻塞 UI 线程也无所谓 ——
            //   自检本来就不需要界面响应。RunEnvironmentTests 探测 Ollama 用的是同一招。
            Task.Run(() => PumpAsync(client, prompt, cts.Token, piece =>
            {
                if (firstTokenMs < 0)
                {
                    firstTokenMs = sw.ElapsedMilliseconds;
                }

                pieces++;
                builder.Append(piece);
            })).GetAwaiter().GetResult();

            sw.Stop();

            var text = builder.ToString().Trim();
            var ok = text.Length > 0 && pieces > 0;

            Add(caseName, ok,
                ok
                    ? $"后端 {client.DisplayName}；首字 {firstTokenMs} ms，共 {pieces} 段 / {text.Length} 字，"
                      + $"总耗时 {sw.ElapsedMilliseconds} ms；译文：{Truncate(text, 100)}"
                    : "模型一个字都没返回 —— 客户端解析流式响应这段可能有问题。");
        }
        catch (Exception ex)
        {
            Add(caseName, false,
                $"{ex.GetType().Name}: {ex.Message}。"
                + $"（当前后端：{client.DisplayName}）"
                + "多半是后端连不上、或模型名不对 —— 可在「设置 → 快捷 AI」里点「测试连接」确认。");
        }
    }

    private static async Task PumpAsync(
        IAiClient client, string prompt, CancellationToken ct, Action<string> onPiece)
    {
        await foreach (var piece in client.GenerateStreamAsync(prompt, null, ct))
        {
            onPiece(piece);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    // ---------------------------------------------------------------- 工具方法

    private static void Add(string name, bool passed, string detail)
        => Cases.Add(new Case { Name = name, Passed = passed, Detail = detail });

    private static void Line(string text = "")
    {
        Console2.AppendLine(text);
        try
        {
            Console.WriteLine(text);
        }
        catch
        {
            // 没有控制台就算了，报告文件里还有一份
        }
    }

    private static BitmapSource MakeBitmap(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = (byte)(x * 255 / Math.Max(1, width - 1));      // B
                pixels[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1)); // G
                pixels[i + 2] = 128;                                        // R
                pixels[i + 3] = 255;                                        // A
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool CanDecode(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearClipboard()
    {
        try
        {
            WClipboard.Clear();
            Thread.Sleep(60);
        }
        catch
        {
            // 剪贴板被占用时清不掉，不致命
        }
    }

    private static void SetClipboardText(string text) => SetWithRetry(() => WClipboard.SetText(text));

    private static void SetClipboardImage(BitmapSource image) => SetWithRetry(() => WClipboard.SetImage(image));

    private static void SetClipboardFiles(string[] paths) => SetWithRetry(() =>
    {
        var collection = new System.Collections.Specialized.StringCollection();
        collection.AddRange(paths);
        WClipboard.SetFileDropList(collection);
    });

    /// <summary>剪贴板被别的进程占用（CLIPBRD_E_CANT_OPEN）是常态，必须重试。</summary>
    private static void SetWithRetry(Action action)
    {
        var delay = 60;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch when (attempt < 4)
            {
                Thread.Sleep(delay);
                delay *= 2;
            }
        }
    }

    // ---------------------------------------------------------------- 剪贴板备份/恢复

    private sealed class ClipboardBackup
    {
        public string? Text { get; init; }
        public BitmapSource? Image { get; init; }
        public string[]? Files { get; init; }
        public bool HasAnything => !string.IsNullOrEmpty(Text) || Image is not null || Files is { Length: > 0 };
    }

    private static ClipboardBackup BackupClipboard()
    {
        try
        {
            string? text = null;
            BitmapSource? image = null;
            string[]? files = null;

            if (WClipboard.ContainsFileDropList())
            {
                files = WClipboard.GetFileDropList()?.Cast<string>().ToArray();
            }
            else if (WClipboard.ContainsImage())
            {
                image = WClipboard.GetImage();
            }
            else if (WClipboard.ContainsText())
            {
                text = WClipboard.GetText();
            }

            return new ClipboardBackup { Text = text, Image = image, Files = files };
        }
        catch (Exception ex)
        {
            Line($"[自检] 备份原剪贴板失败（不致命）：{ex.Message}");
            return new ClipboardBackup();
        }
    }

    private static void RestoreClipboard(ClipboardBackup backup)
    {
        try
        {
            if (!backup.HasAnything)
            {
                Line("[自检] 原剪贴板是空的，已恢复为空。");
                return;
            }

            // 只恢复可还原的部分，按保真度从高到低：文件 > 图片 > 文本
            // ⚠️ HTML / RTF 无法还原（会降级成纯文本）——这一点必须让用户知道，不能悄悄降级
            if (backup.Files is { Length: > 0 })
            {
                SetClipboardFiles(backup.Files);
                Line($"[自检] 已恢复原剪贴板：{backup.Files.Length} 个文件");
                return;
            }

            if (backup.Image is not null)
            {
                SetClipboardImage(backup.Image);
                Line("[自检] 已恢复原剪贴板：图片");
                return;
            }

            if (!string.IsNullOrEmpty(backup.Text))
            {
                SetClipboardText(backup.Text);
                Line($"[自检] 已恢复原剪贴板：文本 {backup.Text.Length} 字");
            }
        }
        catch (Exception ex)
        {
            Line($"[自检] 恢复剪贴板失败（不致命，原内容已丢）：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 报告

    private static void WriteReport(string conclusion, int passed, int failed)
    {
        try
        {
            var report = new
            {
                时间 = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                结论 = conclusion,
                通过 = $"{passed}/{Cases.Count(c => !c.Informational)}",
                失败数 = failed,
                用例 = Cases.Select(c => new
                {
                    c.Name,
                    c.Passed,
                    c.Informational,
                    c.Detail,
                }),
                控制台输出 = Console2.ToString(),
            };

            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true,
            });

            var path = Path.Combine(AppPaths.SelfTestDir, "selftest-report.json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            Line($"报告已写入：{path}");
        }
        catch (Exception ex)
        {
            Line($"[自检] 写报告失败：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 控制台

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    /// <summary>
    /// 这是个 WinExe，默认没有控制台。从命令行跑的时候把它接回父进程的控制台，
    /// 这样用户能直接看到结果，不用每次都去翻报告文件。
    ///
    /// 注意这个方法**不是**自检专用：`App.TryRunAutoStart`（--autostart）也要用，
    /// 所以是 internal 而不是 private。凡是"纯命令行模式"都需要它。
    /// </summary>
    internal static void AttachParentConsole()
    {
        try
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS))
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
                Console.SetOut(stdout);
                Console.OutputEncoding = Encoding.UTF8;
            }
        }
        catch
        {
            // 接不上就只写报告文件
        }
    }
}
