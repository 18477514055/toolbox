using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.Screenshot;

/// <summary>
/// 工具 5：快捷截图。
///
/// 交互：按热键（默认 Win+Alt+S）或点悬浮窗 → 整屏冻结成背景 → 鼠标拖拽框选区域
/// → 松手即保存 PNG 到设置里指定的目录（并复制到剪贴板）；右键或 Esc 取消。
///
/// 设计取舍：
///   · 复制截图到剪贴板时走 <see cref="ClipboardWriter"/> 并打「自污染标记」，
///     所以**不会**被当成用户复制的内容塞进剪贴板历史（规则③）。想截完直接粘贴很自然，
///     但又不能污染档案。
///   · OpensBigWindow=true：打开时让悬浮窗暂时躲开（避免截到自己）。
///   · 已在框选中时再次触发 → 直接忽略，不叠加第二个遮罩。
/// </summary>
internal sealed class ScreenshotTool : IToolboxTool
{
    private ToolboxContext? _ctx;
    private ScreenshotWindow? _window;

    public string Id => "screenshot";

    public string Name => "快捷截图";

    public string Glyph => "▣";

    public string DefaultHotKey => "Win+Alt+S";

    public string Description => "拖拽框选区域截图";

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
            // 已经在框选了，别叠加第二个遮罩
            return;
        }

        var image = ScreenCapture.CaptureFullScreen();
        if (image is null)
        {
            _ctx.Notify("快捷截图", "截图失败：无法捕获屏幕。");
            return;
        }

        _window = new ScreenshotWindow(_ctx, image);
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
