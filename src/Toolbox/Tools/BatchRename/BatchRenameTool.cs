using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.BatchRename;

/// <summary>
/// 工具 7：批量文件重命名。
///
/// 交互：按热键 → 选目录 → 调规则（边调边看预览）→ 确认执行 → 可一键撤销。
///
/// 复用情况（按要求，不重写已有模块）：
///   · 工具注册 / 热键 / 悬浮窗 / 托盘：<see cref="IToolboxTool"/> 这套现成机制；
///   · 设置存储：<see cref="Settings.LastRenameDir"/>（沿用 LastConvertDir 那套写法）；
///   · 目录管理：<see cref="AppPaths.Root"/>（改名账本落在数据目录）；
///   · 日志：<see cref="Log"/>；
///   · 目录选择对话框：与「格式转换」用的是同一个 `Microsoft.Win32.OpenFolderDialog`。
/// </summary>
internal sealed class BatchRenameTool : IToolboxTool
{
    private ToolboxContext? _ctx;
    private BatchRenameWindow? _window;

    public string Id => "rename";

    public string Name => "批量重命名";

    public string Glyph => "改";

    /// <summary>R = Rename。本机 Win+Alt+R 空闲。</summary>
    public string DefaultHotKey => "Win+Alt+R";

    public string Description => "按规则批量改名（可预览、可撤销）";

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

        _window = new BatchRenameWindow(_ctx);
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
