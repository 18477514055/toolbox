using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.Convert;

/// <summary>
/// 工具 3：格式转换（类格式工厂，但更轻量）。
///
/// 第一版范围（用户已确认）：**图片 + 文档/PDF，不含视频音频**。
/// 本机没有 ffmpeg，做视频要额外下 100-200 MB，不值得。
/// </summary>
internal sealed class ConvertTool : IToolboxTool
{
    private ToolboxContext? _ctx;
    private ConvertWindow? _window;

    public string Id => "convert";

    public string Name => "格式转换";

    public string Glyph => "🔄";

    public string DefaultHotKey => "Win+Shift+C";

    public string Description => "图片 / 文档 / PDF 互转（不含视频音频）";

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

        _window = new ConvertWindow(_ctx);
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
