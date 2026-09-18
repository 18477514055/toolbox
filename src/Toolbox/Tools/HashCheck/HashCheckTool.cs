using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.HashCheck;

/// <summary>
/// 工具 8：哈希校验。
///
/// 交互：按热键 → 选文件（或拖进来）→ 选算法 → 计算 → 可复制 / 与期望值比对 / 两文件对比。
///
/// 复用情况（按要求，不重写已有模块）：
///   · 工具注册 / 热键 / 悬浮窗 / 托盘：<see cref="IToolboxTool"/> 这套现成机制；
///   · 写剪贴板：<see cref="ClipboardWriter"/>（带自污染标记 ⇒ 不进剪贴板历史）；
///   · 设置存储：<see cref="Settings.LastHashAlgorithm"/>；
///   · 日志：<see cref="Log"/>；
///   · 文件选择对话框：与「格式转换」「图片裁剪」同一套 `Microsoft.Win32.OpenFileDialog`。
/// </summary>
internal sealed class HashCheckTool : IToolboxTool
{
    private ToolboxContext? _ctx;
    private HashCheckWindow? _window;

    public string Id => "hash";

    public string Name => "哈希校验";

    public string Glyph => "#";

    /// <summary>H = Hash。本机 Win+Alt+H 空闲。</summary>
    public string DefaultHotKey => "Win+Alt+H";

    public string Description => "算 MD5 / SHA256，比对文件是否一致";

    public bool OpensBigWindow => true;

    public void Start(ToolboxContext ctx) => _ctx = ctx;

    public void Invoke()
    {
        if (_ctx is null)
        {
            return;
        }

        if (_window is { IsVisible: true })
        {
            _window.Activate();
            return;
        }

        _window = new HashCheckWindow(_ctx);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        _window.Activate();
    }

    public void Stop()
    {
        _window?.Close();
        _window = null;
    }
}
