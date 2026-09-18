using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Toolbox.Core;
using Toolbox.Shell;
using WClipboard = System.Windows.Clipboard;

namespace Toolbox.Tools.Ai;

/// <summary>
/// 快捷 AI 的面板：读取 + 总结 + 翻译三个入口都在这里。
///
/// 关于"零污染"的三条边界（交接文档反复强调，别搞混）：
///   - **读**剪贴板去翻译：不违反。纯读取，一个字都不写回去。
///   - 用户点「复制结果」把译文**写**回剪贴板：不违反（用户主动要的），但要打 Origin=tool 标记、不进档案。
///   - 工具自己**偷偷**写剪贴板搬数据：禁止。
/// </summary>
internal sealed partial class AiPanel : Window
{
    private readonly ToolboxContext _ctx;
    private CancellationTokenSource? _cts;
    private bool _running;

    /// <summary>面板打开那一刻捕获的选区。焦点已经在我们这了，之后再也读不到。</summary>
    private string? _cachedSelection;

    public AiPanel(ToolboxContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        GrabBtn.Click += async (_, _) => await GrabAsync();
        ClearBtn.Click += (_, _) => { Input.Clear(); Output.Clear(); ProgressText.Text = ""; };
        TranslateBtn.Click += async (_, _) => await TranslateAsync();
        SummarizeBtn.Click += async (_, _) => await SummarizeAsync();
        ClipboardBtn.Click += async (_, _) => await TranslateClipboardAsync();
        AskBtn.Click += async (_, _) => await AskAsync();
        CopyBtn.Click += (_, _) => CopyResult();
        CancelBtn.Click += (_, _) => _cts?.Cancel();

        Input.TextChanged += (_, _) => UpdateDirectionInfo();

        MainOnlyBox.IsChecked = _ctx.Settings.AiMainContentOnly;
        MainOnlyBox.Checked += (_, _) => SetMainOnly(true);
        MainOnlyBox.Unchecked += (_, _) => SetMainOnly(false);

        // Ctrl+Enter = 翻译（最常用的动作给个快捷方式）
        Input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await TranslateAsync();
            }
        };

        Closing += (_, _) =>
        {
            // 关窗口就停掉正在跑的生成，别留一个后台任务继续占显存
            _cts?.Cancel();
        };

        UpdateDirectionInfo();
    }

    private void SetMainOnly(bool value)
    {
        _ctx.Settings.AiMainContentOnly = value;
        _ctx.SaveSettings();
    }

    /// <summary>把面板打开那一刻捕获到的选区交给面板（之后读不到了）。</summary>
    public void SetCachedSelection(string? selection)
    {
        _cachedSelection = selection;

        if (!string.IsNullOrWhiteSpace(selection))
        {
            Input.Text = selection;
            ModeSelection.IsChecked = true;
            InputInfo.Text = $"（来自打开面板时捕获的选区，{selection.Length} 字）";
        }
    }

    public void SetInput(string text, string label)
    {
        Input.Text = text;
        InputInfo.Text = $"（{label}，{text.Length} 字）";
    }

    public async Task StartTranslateAsync() => await TranslateAsync();

    /// <summary>
    /// 按热键打开面板时，自动读一次当前窗口。
    ///
    /// 为什么必须有（这是"实现与界面文案不符"的修复）：
    ///   界面上写着「整窗口（按热键就读当前页面）」，但原来按热键后面板是空的，
    ///   用户得再点一下「读取」才填正文 —— 照验收单走会直接判不通过。
    ///   承诺了就得做到：按热键的语义就是"我要处理当前页面"。
    ///
    /// 为什么放在面板显示**之后**再读：
    ///   读整窗口要几百毫秒到几秒（UIA 要遍历整棵文档树）。放在显示之前会让热键"卡住"，
    ///   用户按了没反应。先让面板出现，再异步把正文填进去，这个顺序体感才对。
    /// </summary>
    public async Task AutoGrabWindowAsync()
    {
        // 只有「整窗口」模式才自动读。「选中」模式的文本在打开面板的瞬间就已经捕获好了
        // （那时焦点还在用户程序上），这里再读反而会读到我们自己的控件。
        if (ModeSelection.IsChecked == true)
        {
            return;
        }

        // 输入框里已经有内容就别覆盖 —— 用户可能刚手动改过，或者缓存里已有选区。
        if (!string.IsNullOrWhiteSpace(Input.Text))
        {
            return;
        }

        await GrabAsync();
    }

    /// <summary>
    /// 把"将要读取哪个窗口"显示出来。
    ///
    /// 为什么要有：用户按热键时最需要确认的一件事就是"你到底会读哪个窗口"——
    /// 尤其在多显示器、窗口切来切去的时候。原来这个 TextBlock 从来没被赋值过，
    /// 永远空白；用户只能点完「读取」才知道读的是哪儿，那时已经晚了。
    /// </summary>
    public void RefreshTargetInfo()
    {
        try
        {
            var desc = _ctx.Focus.DescribeTarget();
            TargetInfo.Text = string.IsNullOrWhiteSpace(desc)
                ? "还没记录到外部窗口 —— 先切到你要读的程序，再按热键唤出本面板。"
                : $"将要读取：{desc}";
        }
        catch (Exception ex)
        {
            Log.Exception("更新目标窗口提示失败", ex);
        }
    }

    // ---------------------------------------------------------------- 读取

    private async Task GrabAsync()
    {
        if (_running)
        {
            return;
        }

        if (ModeSelection.IsChecked == true)
        {
            // 焦点已经在我们这边了，只能用手上缓存的那份
            if (!string.IsNullOrWhiteSpace(_cachedSelection))
            {
                SetInput(_cachedSelection, "打开面板时捕获的选区");
                return;
            }

            var fresh = await TextGrabber.GrabSelectionAsync();
            if (fresh.Success)
            {
                SetInput(fresh.Text, fresh.Source);
            }
            else
            {
                StatusText.Text = fresh.Error ?? "读取失败。";
            }

            return;
        }

        // 整窗口：读"唤出前最后一个非本工具的焦点窗口"
        var target = _ctx.Focus.LastForeignWindow;
        if (target == IntPtr.Zero)
        {
            StatusText.Text = "还没记录到外部窗口。先切到你要读的程序，再按热键唤出面板。";
            return;
        }

        StatusText.Text = $"正在读取：{_ctx.Focus.DescribeTarget()} …";
        GrabBtn.IsEnabled = false;

        try
        {
            var result = await TextGrabber.GrabWindowAsync(target, MainOnlyBox.IsChecked == true);

            if (!result.Success)
            {
                StatusText.Text = result.Error ?? "读取失败。";
                return;
            }

            var cleaned = TextCleaner.Clean(result.Text);
            SetInput(cleaned, result.Source);
            StatusText.Text = $"读取成功：{result.Source}，读到 {cleaned.Length} 字。"
                              + "送进模型前请自己扫一眼，把不相关的内容删掉——这比任何自动清洗都可靠。";
        }
        finally
        {
            GrabBtn.IsEnabled = true;
        }
    }

    private async Task TranslateClipboardAsync()
    {
        // ★ 只读剪贴板：一个字都不写回去，不影响用户手上正在粘贴的东西
        try
        {
            var text = WClipboard.ContainsText() ? WClipboard.GetText() : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText.Text = "剪贴板里现在没有文本。";
                return;
            }

            SetInput(text, "剪贴板内容（只读）");
            await TranslateAsync();
        }
        catch (Exception ex)
        {
            Log.Exception("读取剪贴板文本失败", ex);
            StatusText.Text = $"读剪贴板失败：{ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 方向提示

    private void UpdateDirectionInfo()
    {
        var text = Input.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            DirectionInfo.Text = "翻译方向会按输入内容自动判断（中文为主→译成英文；英文为主→译成中文），并明确写进提示词。";
            InputInfo.Text = "";
            return;
        }

        var target = LanguageDetector.TargetLanguageFor(text);
        DirectionInfo.Text = $"当前判断：输入以{(target == "英文" ? "中文" : "英文")}为主 → 将翻译成「{target}」。"
                             + "方向会明确写进提示词，避免 7B 模型自己猜错方向。";
    }

    // ---------------------------------------------------------------- 生成

    private async Task TranslateAsync()
    {
        var text = Input.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "先读取内容，或者直接把要翻译的文字贴进上面的输入框。";
            return;
        }

        var target = LanguageDetector.TargetLanguageFor(text);
        var chunks = TextCleaner.Chunk(text, _ctx.Settings.AiChunkChars);

        await RunAsync($"翻译成{target}", async (ct, append) =>
        {
            if (chunks.Count <= 1)
            {
                await StreamAsync(Prompts.Translate(target, text), ct, append);
                return;
            }

            // ★ 长文必须分块。而且**必须有进度**——否则用户以为死了。
            for (var i = 0; i < chunks.Count; i++)
            {
                ProgressText.Text = $"正在翻译 {i + 1}/{chunks.Count} 段…";

                if (i > 0)
                {
                    append("\n\n");
                }

                await StreamAsync(
                    Prompts.TranslateChunk(target, i + 1, chunks.Count, chunks[i]),
                    ct,
                    append);
            }

            ProgressText.Text = $"完成（共 {chunks.Count} 段）";
        });
    }

    private async Task SummarizeAsync()
    {
        var text = Input.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "先读取内容，或者把要总结的文字贴进上面的输入框。";
            return;
        }

        var chunks = TextCleaner.Chunk(text, _ctx.Settings.AiChunkChars);

        await RunAsync("总结", async (ct, append) =>
        {
            if (chunks.Count <= 1)
            {
                await StreamAsync(Prompts.Summarize(text), ct, append);
                return;
            }

            // map-reduce：先逐块摘要，再汇总。这是 3000 字以上长文的唯一正确做法。
            var partials = new List<string>();
            for (var i = 0; i < chunks.Count; i++)
            {
                ProgressText.Text = $"正在分段摘要 {i + 1}/{chunks.Count} 段…";

                var buffer = new StringBuilder();
                // 注意这里要包一层 lambda：StringBuilder.Append 返回的是它自己（链式调用），
                // 不是 void，直接当 Action<string> 传会编译不过。
                await StreamAsync(Prompts.Summarize(chunks[i]), ct, piece => buffer.Append(piece));
                partials.Add($"【第 {i + 1} 段】\n{buffer.ToString().Trim()}");
            }

            ProgressText.Text = "正在汇总…";
            append("（长文分块摘要后的汇总结果）\n\n");
            await StreamAsync(Prompts.Reduce(string.Join("\n\n", partials)), ct, append);

            ProgressText.Text = $"完成（共 {chunks.Count} 段）";
        });
    }

    private async Task AskAsync()
    {
        var question = QuestionBox.Text.Trim();
        if (string.IsNullOrEmpty(question))
        {
            StatusText.Text = "先在「提问」框里写问题。";
            return;
        }

        var context = Input.Text.Trim();
        await RunAsync("提问", (ct, _) => StreamAsync(Prompts.Ask(question, context), ct, AppendOutput));
    }

    private void AppendOutput(string piece)
    {
        Output.AppendText(piece);
        Output.ScrollToEnd();
    }

    /// <summary>统一的执行外壳：置忙 → 探活 → 流式 → 收尾。</summary>
    private async Task RunAsync(string title, Func<CancellationToken, Action<string>, Task> body)
    {
        if (_running)
        {
            StatusText.Text = "上一件事还没做完，先点「停止」或者等它跑完。";
            return;
        }

        // ★ 守卫必须在第一个 await **之前**就落地。
        //
        //   原来是「if (_running) 判断 → await 探活 → _running = true」——
        //   判断和赋值之间隔着一个 await，中间那段窗口里用户连点两下，
        //   两个调用都会通过判断。结果是两路流式同时往同一个 Output 里写，
        //   输出交错成一团，而且「停止」只能停掉后一个，前一个还在后台烧 GPU。
        //   现在整个方法体包在 try/finally 里，任何一条提前 return 都会把锁放掉。
        _running = true;

        try
        {
            var settings = _ctx.Settings;
            var client = AiClientFactory.Create(settings);

            // 先探活：Ollama 没启动要给明确提示，不是让用户对着超时发呆。
            //
            // ★ 探活必须自带超时。本地 Ollama 那个 HttpClient 是**无限**超时（生成慢是正常的，
            //   不能按固定秒数掐），可探活挂住就一定是出了问题 —— 地址填成黑洞 IP（包被静默
            //   丢弃，不返回 RST）时，无限超时会让界面永远停在"正在检查…"，用户只能强杀进程。
            //   远端要走公网，给宽一点。
            StatusText.Text = $"正在检查 {client.DisplayName} …";
            var probeSeconds = AiClientFactory.IsRemote(settings) ? 20 : 12;

            using (var probe = new CancellationTokenSource(TimeSpan.FromSeconds(probeSeconds)))
            {
                var (ok, message, _) = await client.CheckAsync(probe.Token);
                if (!ok)
                {
                    StatusText.Text = message;
                    MessageBox.Show(this, message, "快捷 AI", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _cts = new CancellationTokenSource();
            CancelBtn.IsEnabled = true;
            TranslateBtn.IsEnabled = false;
            SummarizeBtn.IsEnabled = false;
            AskBtn.IsEnabled = false;

            BusyState.Set("ai", true, title);
            _ctx.RefreshFloating();

            Output.Clear();
            ProgressText.Text = "正在生成…";
            // ★ 后端和数据流向必须写清楚。
            //   用远端 API 时，待处理的文本会**发到那台服务器**，不再是"数据不出这台机器"。
            //   这种事不能让用户在不知情的情况下发生 —— 尤其是他可能正在翻译一份内部文档。
            StatusText.Text = AiClientFactory.IsRemote(settings)
                ? $"后端：{client.DisplayName} —— 注意：待处理的文本会发送到这个远端服务。"
                : $"后端：{client.DisplayName}（本地运行，数据不出这台机器）";

            var sw = Stopwatch.StartNew();

            try
            {
                await body(_cts.Token, AppendOutput);
                StatusText.Text = $"{title}完成，用时 {sw.Elapsed.TotalSeconds:0.0} 秒。结果可以直接选中复制。";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "已停止。";
                ProgressText.Text = "";
            }
            catch (Exception ex)
            {
                Log.Exception($"AI {title} 失败", ex);
                StatusText.Text = $"出错了：{ex.Message}";
                ProgressText.Text = "";
            }
        }
        finally
        {
            _running = false;
            _cts?.Dispose();
            _cts = null;

            CancelBtn.IsEnabled = false;
            TranslateBtn.IsEnabled = true;
            SummarizeBtn.IsEnabled = true;
            AskBtn.IsEnabled = true;

            BusyState.Set("ai", false);
            _ctx.RefreshFloating();
        }
    }

    private async Task StreamAsync(string prompt, CancellationToken ct, Action<string> append)
    {
        var client = AiClientFactory.Create(_ctx.Settings);

        await foreach (var piece in client.GenerateStreamAsync(prompt, null, ct))
        {
            append(piece);
        }
    }

    // ---------------------------------------------------------------- 复制结果

    private void CopyResult()
    {
        var text = Output.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "还没有结果可以复制。";
            return;
        }

        // 这是"用户主动要的写入"，属于规则③允许的范围：
        // 打 Origin=tool 标记，所以它**不会**进剪贴板历史档案。
        if (ClipboardWriter.SetText(text, out var error))
        {
            StatusText.Text = "已复制到剪贴板。注意：这条不会进剪贴板历史（是我们自己写的）。";
        }
        else
        {
            StatusText.Text = error ?? "复制失败。";
        }
    }
}
