using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Shell;
using WClipboard = System.Windows.Clipboard;

namespace Toolbox.Tools.ImageCrop;

/// <summary>
/// 工具 2：图片裁剪。
///
/// 零第三方依赖：WPF 自带 CroppedBitmap / TransformedBitmap / RenderTargetBitmap
/// 和五种 BitmapEncoder，够用了（DECISIONS.md §二·五 已实测确认）。
///
/// 默认输入来源刻意选「剪贴板里的图片」——截图（Win+Shift+S）后直接按热键就能裁，
/// 用户零操作成本。这是这个工具好不好用的关键。
/// </summary>
internal sealed class ImageCropTool : IToolboxTool
{
    private ToolboxContext? _ctx;
    private ImageCropWindow? _window;

    public string Id => "image";

    public string Name => "图片裁剪";

    public string Glyph => "✂";

    public string DefaultHotKey => "Win+Shift+X";

    public string Description => "框选裁剪 / 改像素尺寸";

    public bool OpensBigWindow => true;

    public void Start(ToolboxContext ctx) => _ctx = ctx;

    public void Invoke()
    {
        if (_ctx is null)
        {
            return;
        }

        // 优先从剪贴板拿图（最顺手的路径）
        BitmapSource? clipboardImage = null;
        try
        {
            clipboardImage = WClipboard.ContainsImage() ? WClipboard.GetImage() : null;
        }
        catch (Exception ex)
        {
            // 坑 2：GetImage 在某些来源上会直接抛 NRE，必须接住
            Log.Exception("从剪贴板取图失败（已降级为空白窗口）", ex);
        }

        if (_window is { IsVisible: true })
        {
            if (clipboardImage is not null)
            {
                _window.LoadImage(clipboardImage, "剪贴板图片");
            }

            _window.Activate();
            return;
        }

        _window = new ImageCropWindow(_ctx);
        _window.Closed += (_, _) => _window = null;
        _window.Show();

        if (clipboardImage is not null)
        {
            _window.LoadImage(clipboardImage, "剪贴板图片");
        }

        _window.Activate();
    }

    public void Stop()
    {
        _window?.Close();
        _window = null;
    }
}
