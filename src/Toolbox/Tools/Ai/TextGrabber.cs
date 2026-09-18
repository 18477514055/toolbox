using System.Windows.Automation;
using Toolbox.Core;

namespace Toolbox.Tools.Ai;

internal sealed record GrabResult(bool Success, string Text, string Source, string? Error)
{
    public static GrabResult Ok(string text, string source) => new(true, text, source, null);

    public static GrabResult Fail(string error, string source) => new(false, "", source, error);
}

/// <summary>
/// 用 UI Automation 读文本。**完全不碰剪贴板**——这是"零污染"的关键。
///
/// 两种读取方式（用户明确要求两个都保留）：
///   - **整窗口**：读指定窗口的文档全文。默认只取主内容区（最大的那个 Document 控件），
///     避免把导航栏、广告、评论区一起读进来。
///   - **选中**：用 `TextPattern.GetSelection()` 直读当前选区。
///     这是"读选区"的正解——不写剪贴板、不需要模拟 Ctrl+C。
///
/// ⚠️ 时机非常重要：**必须在工具箱自己的窗口抢到焦点之前读**。
/// 一旦焦点跑到我们的面板上，UIA 读到的就是面板自己。
/// 所以调用方要在「唤出 UI 之前」就把选区读出来（见 AiTool）。
///
/// UIA 调用可能因为目标程序无响应而卡住，所以一律带超时，跑在后台线程上。
/// </summary>
internal static class TextGrabber
{
    /// <summary>读取超时。UIA 跨进程调用没有超时的话，遇到卡死的程序会把我们拖住。</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    /// <summary>整页模式下最多收集多少个 Text 控件，避免在复杂网页上跑太久。</summary>
    private const int MaxTextElements = 4000;

    // ---------------------------------------------------------------- 选中

    /// <summary>
    /// 读当前焦点元素的选区。
    /// 必须在工具箱自己的窗口拿到焦点**之前**调用，否则读到的是我们自己。
    /// </summary>
    public static Task<GrabResult> GrabSelectionAsync(TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        return RunWithTimeout(() => GrabSelectionCore(), limit, "读取选中文本超时（目标程序可能没响应）。");
    }

    private static GrabResult GrabSelectionCore()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return GrabResult.Fail("当前没有焦点窗口。", "选中");
            }

            // ★ 先确认焦点不在我们自己的窗口上。
            //
            // 边缘但真实的场景：工具箱某个会抢焦点的窗口（剪贴板面板 Activate() 之后
            // 又把焦点给了搜索框）正好是前台时触发「翻译选中」，就会读到我们自己的控件文本，
            // 用户看到的是莫名其妙的一串界面文字。命中就明确说清楚，别给出一份"假译文"。
            if (IsOwnProcess(focused))
            {
                return GrabResult.Fail(
                    "当前焦点在工具箱自己的窗口上，读不到你选中的内容。\n"
                    + "请回到你要翻译的程序里选中文字，再按热键。",
                    "选中");
            }

            // 先直接试 TextPattern
            var text = TryGetSelection(focused);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return GrabResult.Ok(text, "选中（UIA 直读）");
            }

            // 有些程序把可编辑区域放在子元素上，往下找一层
            var editable = focused.FindAll(TreeScope.Descendants,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));

            foreach (AutomationElement element in editable)
            {
                var t = TryGetSelection(element);
                if (!string.IsNullOrWhiteSpace(t))
                {
                    return GrabResult.Ok(t, "选中（UIA 直读）");
                }
            }

            return GrabResult.Fail(
                "没读到选中的文本。可能这个程序没有把选区暴露给系统（自绘界面、部分 Electron 应用）。\n"
                + "可以改用「读当前页面」，或者自己选中后按 Ctrl+C，再用「翻译剪贴板内容」。",
                "选中");
        }
        catch (ElementNotAvailableException)
        {
            return GrabResult.Fail("目标窗口已经关闭了。", "选中");
        }
        catch (Exception ex)
        {
            Log.Exception("读取选中文本失败", ex);
            return GrabResult.Fail($"读取选中文本失败：{ex.Message}", "选中");
        }
    }

    /// <summary>这个 UIA 元素是不是属于工具箱自己（按进程号判断，比列窗口名单可靠）。</summary>
    private static bool IsOwnProcess(AutomationElement element)
    {
        try
        {
            return element.Current.ProcessId == Environment.ProcessId;
        }
        catch
        {
            // 元素已经消失之类：当作"不是自己"，让后续逻辑照常给出它自己的报错
            return false;
        }
    }

    private static string? TryGetSelection(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)
                || pattern is not TextPattern textPattern)
            {
                return null;
            }

            var ranges = textPattern.GetSelection();
            if (ranges is null || ranges.Length == 0)
            {
                return null;
            }

            var parts = ranges
                .Select(r =>
                {
                    try
                    {
                        return r.GetText(-1);
                    }
                    catch
                    {
                        return "";
                    }
                })
                .Where(s => !string.IsNullOrWhiteSpace(s));

            var joined = string.Join("\n", parts);
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 整窗口

    /// <summary>
    /// 读一个窗口的文本。
    /// </summary>
    /// <param name="hwnd">要读的窗口句柄（**不是**工具箱自己的窗口）。</param>
    /// <param name="mainContentOnly">true = 只取主内容区（最大的文档控件）；false = 整页。</param>
    public static Task<GrabResult> GrabWindowAsync(IntPtr hwnd, bool mainContentOnly, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        return RunWithTimeout(
            () => GrabWindowCore(hwnd, mainContentOnly),
            limit,
            "读取窗口内容超时（目标程序可能没响应，或者页面太大了）。");
    }

    private static GrabResult GrabWindowCore(IntPtr hwnd, bool mainContentOnly)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return GrabResult.Fail("目标窗口已经不在了。", "整窗口");
            }

            AutomationElement? root;
            try
            {
                root = AutomationElement.FromHandle(hwnd);
            }
            catch (Exception ex)
            {
                return GrabResult.Fail($"连不上这个窗口的界面树：{ex.Message}", "整窗口");
            }

            if (root is null)
            {
                return GrabResult.Fail("拿不到这个窗口的界面树。", "整窗口");
            }

            // ---- 1) 根元素自己就是文档（记事本、部分编辑器）----
            var direct = TryReadDocument(root);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                if (mainContentOnly)
                {
                    var reduced = TrimNavAndFooter(direct, out var didTrim);
                    if (didTrim)
                    {
                        var saved = direct.Length - reduced.Length;
                        Log.Line($"「只取主内容区」已生效（根文档）：切掉首尾导航/页脚 {saved} 字。");
                        return GrabResult.Ok(reduced, $"整窗口（根文档，已去首尾导航 {saved} 字）");
                    }
                }

                return GrabResult.Ok(direct, "整窗口（根文档）");
            }

            // ---- 2) 找 Document 控件，取最长的那一段 ----
            var documents = SafeFindAll(root,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));

            string best = "";
            AutomationElement? bestElement = null;

            foreach (AutomationElement element in documents)
            {
                var text = TryReadDocument(element);
                if (text is not null && text.Length > best.Length)
                {
                    best = text;
                    bestElement = element;
                }
            }

            if (!string.IsNullOrWhiteSpace(best))
            {
                if (mainContentOnly)
                {
                    // 「只取主内容区」必须真的干点活 —— 原来这里两个分支返回同一份文本，
                    // 勾选框只改了提示字符串，是个空开关（界面上却承诺"少带导航/广告/评论区噪声"）。
                    var reduced = TrimNavAndFooter(best, out var didTrim);
                    if (didTrim)
                    {
                        var saved = best.Length - reduced.Length;
                        Log.Line($"「只取主内容区」已生效：切掉首尾导航/页脚 {saved} 字（{best.Length} → {reduced.Length}）。");
                        return GrabResult.Ok(reduced, $"整窗口（主内容区，已去首尾导航 {saved} 字）");
                    }

                    return GrabResult.Ok(best, "整窗口（主内容区，未发现可切的导航区）");
                }

                // 不勾：老老实实给全文，标签也别冒充"主内容区"
                var label = documents.Count > 1 ? "整窗口（全文·多文档取最长）" : "整窗口（全文）";
                return GrabResult.Ok(best, label);
            }

            // ---- 3) 没有 Document 控件：把全部 Text 控件拼起来（噪声大，但总比没有强）----
            var collected = CollectAllText(root);
            if (!string.IsNullOrWhiteSpace(collected))
            {
                if (mainContentOnly)
                {
                    var reduced = TrimNavAndFooter(collected, out var didTrim);
                    if (didTrim)
                    {
                        var saved = collected.Length - reduced.Length;
                        return GrabResult.Ok(reduced, $"整窗口（逐控件拼接，已去首尾导航 {saved} 字）");
                    }
                }

                return GrabResult.Ok(collected, "整窗口（逐控件拼接，可能带导航/页脚噪声）");
            }

            return GrabResult.Fail(
                "这个程序没把文本暴露给系统（常见于自绘界面、部分 Electron 应用、游戏、远程桌面）。\n"
                + "降级办法：在目标程序里 Ctrl+A、Ctrl+C 全选复制，然后用「翻译剪贴板内容」——\n"
                + "注意这一步是**你自己按的**，工具箱不会替你模拟按键。",
                "整窗口");
        }
        catch (Exception ex)
        {
            Log.Exception("读取窗口文本失败", ex);
            return GrabResult.Fail($"读取窗口文本失败：{ex.Message}", "整窗口");
        }
    }

    /// <summary>
    /// 「只取主内容区」的实际实现：切掉正文首尾的导航区 / 页脚区。
    ///
    /// 背景：原来这个勾选框是个**空开关** —— 勾与不勾返回同一份文本，
    /// mainContentOnly 只改了提示字符串。界面承诺了"少带导航/广告/评论区噪声"，就得真做。
    ///
    /// 做法（刻意保守，因为**误删正文比留着噪声更糟**）：
    ///   网页的导航/页脚有个共同特征 —— 由大量**短行**组成
    ///   （"首页""登录""关于我们""© 2026 某某公司"），而正文段落普遍较长。
    ///   所以扫首尾的**连续短行块**，把它们切掉。
    ///
    /// 保底机制（关键，不可省）：
    ///   如果切完剩下的不到原来的一半，说明判断错了（比如整篇都是短行、或者文档本身就是清单），
    ///   这时**放弃降噪、原样返回**。宁可带噪声，也不能把用户要的内容删掉。
    /// </summary>
    /// <param name="didTrim">是否真的切了东西。</param>
    private static string TrimNavAndFooter(string text, out bool didTrim)
    {
        didTrim = false;

        var lines = text.Split('\n');
        if (lines.Length < 6)
        {
            return text; // 太短，谈不上导航区
        }

        // 短行 = 像"标签"而不是"句子"：短，且不以句末标点收尾
        static bool IsShortLabel(string line)
        {
            var s = line.Trim();
            if (s.Length == 0 || s.Length > 24)
            {
                return false;
            }

            return !s.EndsWith('。') && !s.EndsWith('.') && !s.EndsWith('！')
                   && !s.EndsWith('!') && !s.EndsWith('？') && !s.EndsWith('?');
        }

        var start = 0;
        var end = lines.Length - 1;

        while (start <= end && (IsShortLabel(lines[start]) || lines[start].Trim().Length == 0))
        {
            start++;
        }

        while (end >= start && (IsShortLabel(lines[end]) || lines[end].Trim().Length == 0))
        {
            end--;
        }

        if (start == 0 && end == lines.Length - 1)
        {
            return text; // 首尾都不是短行块，没东西可切
        }

        var result = string.Join('\n', lines[start..(end + 1)]);

        // ★ 保底：切掉太多就认定误判，原样返回
        if (result.Trim().Length < text.Trim().Length * 0.5)
        {
            Log.Line($"「只取主内容区」放弃降噪：切完只剩 {result.Trim().Length}/{text.Trim().Length} 字（不足一半），判定为误判，原样保留。");
            return text;
        }

        didTrim = true;
        return result;
    }

    private static string? TryReadDocument(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)
                || pattern is not TextPattern textPattern)
            {
                return null;
            }

            var text = textPattern.DocumentRange.GetText(-1);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static AutomationElementCollection SafeFindAll(AutomationElement root, Condition condition)
    {
        try
        {
            return root.FindAll(TreeScope.Descendants, condition);
        }
        catch
        {
            return root.FindAll(TreeScope.Children, condition);
        }
    }

    /// <summary>
    /// 兜底：遍历所有 Text 控件把 Name 拼起来。
    /// 这条路噪声很大（导航、页脚、按钮文字都会进来），所以**只在前面两条都失败时才用**，
    /// 而且结果会明确告诉用户"可能带噪声"，让他自己删。
    /// </summary>
    private static string CollectAllText(AutomationElement root)
    {
        try
        {
            var texts = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));

            var builder = new System.Text.StringBuilder();
            var count = 0;

            foreach (AutomationElement element in texts)
            {
                if (++count > MaxTextElements)
                {
                    break;
                }

                try
                {
                    var name = element.Current.Name;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        builder.AppendLine(name.Trim());
                    }
                }
                catch
                {
                    // 元素随时可能消失，跳过
                }
            }

            return builder.ToString();
        }
        catch
        {
            return "";
        }
    }

    // ---------------------------------------------------------------- 超时包装

    /// <summary>
    /// 在后台线程上跑 UIA 查询，并加超时。
    /// UIA 是跨进程调用，目标程序卡住时我们也会跟着卡——**绝不能在 UI 线程上裸调**。
    /// </summary>
    private static async Task<GrabResult> RunWithTimeout(Func<GrabResult> work, TimeSpan timeout, string timeoutMessage)
    {
        var task = Task.Run(work);

        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            return GrabResult.Fail(timeoutMessage, "超时");
        }

        return await task.ConfigureAwait(false);
    }
}
