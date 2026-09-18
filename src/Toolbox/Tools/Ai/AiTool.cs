using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.Ai;

/// <summary>
/// 工具 4：快捷 AI（本地千问 2.5 7b）。
///
/// 用户明确要求的三条（都是硬需求）：
///   1. 页面读取**两种方式都保留**（整窗口 + 选中）；
///   2. 翻译**三个入口**（当前页面 / 选中文本 / 剪贴板内容）；
///   3. 语言对固定中英双向、自动判方向。
///
/// 以及一条全局约束：**不许污染剪贴板**。所以读取全程走 UI Automation，一个字都不写剪贴板。
///
/// 关于热键：除了主热键「打开面板」，还单独给了「翻译选中」一个热键（Win+Shift+T）——
/// 这是最高频的动作，值得一键直达：选中 → 按 → 直接出译文，全程零污染。
/// </summary>
internal sealed class AiTool : IToolboxTool
{
    private ToolboxContext? _ctx;
    private AiPanel? _panel;

    public string Id => "ai";

    public string Name => "快捷 AI";

    public string Glyph => "✨";

    public string DefaultHotKey => "Win+Shift+A";

    public string Description => "读页面 / 总结 / 翻译（本地模型）";

    public bool OpensBigWindow => true;

    public IReadOnlyDictionary<string, string> ExtraHotKeys => Extra;

    private static readonly Dictionary<string, string> Extra = new(StringComparer.OrdinalIgnoreCase)
    {
        ["translateSelection"] = "Win+Shift+T",
    };

    public void Start(ToolboxContext ctx) => _ctx = ctx;

    public void Invoke()
    {
        if (_ctx is null)
        {
            return;
        }

        // ★ 关键时机：在**面板拿到焦点之前**把选区读出来。
        // 一旦面板显示出来，UIA 的"焦点元素"就变成我们自己的输入框了，选区再也读不到。
        var selection = CaptureSelectionBlocking();

        var panel = EnsurePanel();
        panel.SetCachedSelection(selection);

        if (!panel.IsVisible)
        {
            panel.Show();
        }

        panel.Activate();

        // 把"将要读取哪个窗口"显示出来（这个提示原来永远是空白）。
        panel.RefreshTargetInfo();

        // 面板显示之后，自动补一次「整窗口」读取。
        //
        // 界面文案承诺的是「整窗口（按热键就读当前页面）」，所以这里必须真读，
        // 否则用户按完热键看到的是空框、还得再点一下「读取」——文案就成了空头支票。
        //
        // 不 await（fire-and-forget）：读整窗口要几百毫秒到几秒，同步等会把热键响应拖慢；
        // 面板已经显示出来了，正文稍后填进去即可。
        _ = AutoGrabQuietlyAsync(panel);
    }

    /// <summary>
    /// 自动读整窗口，异常只记日志。
    /// 它是热键主流程之外的补充动作，不该把"打开面板"这件事带崩。
    /// </summary>
    private static async Task AutoGrabQuietlyAsync(AiPanel panel)
    {
        try
        {
            await panel.AutoGrabWindowAsync();
        }
        catch (Exception ex)
        {
            Log.Exception("自动读取当前窗口失败", ex);
        }
    }

    public void InvokeExtra(string hotKeyName)
    {
        if (!string.Equals(hotKeyName, "translateSelection", StringComparison.OrdinalIgnoreCase))
        {
            Invoke();
            return;
        }

        _ = TranslateSelectionAsync();
    }

    /// <summary>
    /// 一键直达：读选区 → 出译文。
    /// 全程不碰剪贴板，也不模拟按键——这是"选中即按即得"的正解。
    /// </summary>
    private async Task TranslateSelectionAsync()
    {
        if (_ctx is null)
        {
            return;
        }

        var grab = await TextGrabber.GrabSelectionAsync();

        var panel = EnsurePanel();
        panel.Show();
        panel.Activate();

        if (!grab.Success)
        {
            panel.SetInput("", "");
            // 明确限定 System.Windows.MessageBox：本工程同时引了 WinForms，
            // 不加限定名会撞上 System.Windows.Forms.MessageBox（参数表还不一样）。
            System.Windows.MessageBox.Show(panel, grab.Error ?? "没读到选中的文本。", "快捷 AI · 翻译选中",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var cleaned = TextCleaner.Clean(grab.Text);
        panel.SetInput(cleaned, grab.Source);
        await panel.StartTranslateAsync();
    }

    private AiPanel EnsurePanel()
    {
        if (_panel is { IsLoaded: true })
        {
            return _panel;
        }

        _panel = new AiPanel(_ctx!);
        _panel.Closed += (_, _) => _panel = null;
        return _panel;
    }

    /// <summary>
    /// 有界阻塞地读一次选区（最多等 2 秒）。
    ///
    /// 为什么要"有界阻塞"而不是纯异步：选区必须在面板显示前读，而"显示面板"这个动作
    /// 是同步的。等 2 秒是可以接受的（正常选区读取是毫秒级），
    /// 而如果完全不等，用户按 Win+Shift+A 时会发现"选中"模式永远是空的。
    /// </summary>
    private string? CaptureSelectionBlocking()
    {
        try
        {
            var task = TextGrabber.GrabSelectionAsync(TimeSpan.FromSeconds(2));
            if (!task.Wait(TimeSpan.FromSeconds(2)))
            {
                Log.Line("读取选区超时（不影响面板打开，只是「选中」模式会是空的）。");
                return null;
            }

            var result = task.Result;
            return result.Success ? TextCleaner.Clean(result.Text) : null;
        }
        catch (Exception ex)
        {
            Log.Exception("预读选区失败", ex);
            return null;
        }
    }

    public void Stop()
    {
        _panel?.Close();
        _panel = null;
    }
}
