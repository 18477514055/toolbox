using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.QrCode;

/// <summary>
/// 工具 9：二维码工具（生成 + 扫描）。
///
/// 交互：按热键 → 「生成」标签页输入内容出码；「扫描」标签页框选屏幕或选图片，读出内容。
///
/// 复用情况（按要求，不重写已有模块）：
///   · 工具注册 / 热键 / 悬浮窗 / 托盘：<see cref="IToolboxTool"/> 这套现成机制；
///   · 截屏：<see cref="Screenshot.ScreenCapture"/>；框选层：<see cref="Ocr.OcrRegionWindow"/>；
///   · 写剪贴板：<see cref="ClipboardWriter"/>（带自污染标记）；
///   · 设置存储：<see cref="Settings.QrOutputDir"/> / <see cref="Settings.QrSize"/>；
///   · 目录管理：<see cref="AppPaths.Root"/>；日志：<see cref="Log"/>。
/// </summary>
internal sealed class QrTool : IToolboxTool
{
    private ToolboxContext? _ctx;
    private QrWindow? _window;

    public string Id => "qrcode";

    public string Name => "二维码工具";

    public string Glyph => "▩";

    /// <summary>Q = QR。本机 Win+Alt+Q 空闲。</summary>
    public string DefaultHotKey => "Win+Alt+Q";

    public string Description => "生成二维码 / 扫描屏幕二维码";

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

        _window = new QrWindow(_ctx);
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
