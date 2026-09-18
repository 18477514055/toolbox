using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.CommandRunner;

/// <summary>
/// 工具 11：运行命令（cmd / PowerShell）。
///
/// 交互：按热键 → 输入框 → 选 cmd 或 PowerShell → **Ctrl+Enter** 执行 → 看输出。
///
/// 复用情况（按要求，不重写已有模块）：
///   · 工具注册 / 热键 / 悬浮窗 / 托盘：<see cref="IToolboxTool"/> 这套现成机制；
///   · 写剪贴板：<see cref="ClipboardWriter"/>（带自污染标记）；
///   · 日志：<see cref="Log"/>；
///   · 托盘通知：<c>ToolboxContext.Notify</c>。
///
/// ⚠️ 安全说明见 <see cref="CommandRunner"/> 的类注释 ——
/// 这是本工具箱里**唯一能执行任意命令**的功能，界面上也如实标注了这一点。
/// </summary>
internal sealed class CommandRunnerTool : IToolboxTool
{
    private ToolboxContext? _ctx;
    private CommandWindow? _window;

    public string Id => "run";

    public string Name => "运行命令";

    public string Glyph => ">_";

    /// <summary>C = Command。本机 Win+Alt+C 已被"格式转换"占用，用 X（本机空闲）。</summary>
    public string DefaultHotKey => "Win+Alt+X";

    public string Description => "输入命令交给 cmd / PowerShell 执行";

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

        _window = new CommandWindow(_ctx);
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
