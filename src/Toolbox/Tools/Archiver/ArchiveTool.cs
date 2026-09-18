using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.Archiver;

/// <summary>
/// 工具 12：批量打包。
///
/// 交互：按热键 → 拖入（或从剪贴板载入）一批文件 → 选 ZIP/RAR → 选放哪 → 打包。
///
/// 复用情况（按要求，不重写已有模块）：
///   · 工具注册 / 热键 / 悬浮窗 / 托盘：<see cref="IToolboxTool"/> 这套现成机制；
///   · 设置存储：<see cref="Settings.ArchiveOutputDir"/>（沿用 LastConvertDir 那套写法）；
///   · 目录管理：<see cref="AppPaths"/>；日志：<see cref="Log"/>；托盘通知：<c>Notify</c>；
///   · 不覆盖同名文件：复用了 <c>Convert.FileNaming.UniquePath</c>。
/// </summary>
internal sealed class ArchiveTool : IToolboxTool
{
    private ToolboxContext? _ctx;
    private ArchiveWindow? _window;

    public string Id => "archive";

    public string Name => "批量打包";

    public string Glyph => "🗜";

    /// <summary>Z = Zip。本机 Win+Alt+Z 已作为多个降级链的备选，这里改用 Win+Alt+G（空闲）。</summary>
    public string DefaultHotKey => "Win+Alt+G";

    public string Description => "把选中的文件打包成 zip / rar";

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

        _window = new ArchiveWindow(_ctx);
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
