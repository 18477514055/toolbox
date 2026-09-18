using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Shell;
using Toolbox.Tools.Screenshot;

namespace Toolbox.Tools.Ocr;

/// <summary>
/// 工具 6：OCR 截图识字。
///
/// 交互：按热键 → 整屏冻结 → 拖拽框选 → 识别文字 → **自动复制到剪贴板** + 弹出结果窗口
/// （结果可编辑，改完再复制）。
///
/// 复用情况（按要求，不重写已有模块）：
///   · 截屏：<see cref="ScreenCapture.CaptureFullScreen"/>（快捷截图用的同一个 GDI 抓屏）；
///   · 坐标换算：<see cref="ScreenCoordinateMapper"/>（按显示器真实 DPI）；
///   · 写剪贴板：<see cref="ClipboardWriter"/>（带自污染标记 ⇒ 不进剪贴板历史）；
///   · 热键 / 设置 / 悬浮窗：全部走 <see cref="IToolboxTool"/> 这套现成机制。
///
/// OCR 引擎用 <see cref="OcrEngineService"/>（Windows 自带，零依赖、离线）。
/// </summary>
internal sealed class OcrTool : IToolboxTool
{
    private ToolboxContext? _ctx;
    private OcrRegionWindow? _region;
    private OcrResultWindow? _result;

    public string Id => "ocr";

    public string Name => "OCR 截图识字";

    public string Glyph => "字";

    /// <summary>默认键选 Alt+T（T = Text）。Win+Shift+&lt;字母&gt; 族本机已占用 10 个，尽量避开。</summary>
    public string DefaultHotKey => "Win+Alt+T";

    public string Description => "框选屏幕区域，识别文字并复制";

    /// <summary>要开全屏遮罩 ⇒ 让悬浮窗躲开，免得被框进去。</summary>
    public bool OpensBigWindow => true;

    public void Start(ToolboxContext ctx)
    {
        _ctx = ctx;

        // 启动时探测一次，把结果记进日志 —— 出问题时一眼能看出是"没装语言包"还是别的
        if (OcrEngineService.IsAvailable(out var reason))
        {
            Log.Line($"OCR 工具已就绪，可用语言：{string.Join("、", OcrEngineService.AvailableLanguages())}");
        }
        else
        {
            Log.Line($"OCR 工具暂不可用：{reason}");
        }
    }

    public void Invoke()
    {
        if (_ctx is null)
        {
            return;
        }

        // 已经在框选 → 忽略，不叠加第二个遮罩（与快捷截图一致）
        if (_region is { IsVisible: true })
        {
            return;
        }

        // 引擎不可用就**明确说清楚**，别让用户框完一片区域才等到一句失败
        if (!OcrEngineService.IsAvailable(out var reason))
        {
            System.Windows.MessageBox.Show(
                reason ?? "OCR 引擎不可用。",
                "OCR 截图识字",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var image = ScreenCapture.CaptureFullScreen();
        if (image is null)
        {
            _ctx.Notify("OCR 截图识字", "截屏失败：无法捕获屏幕。");
            return;
        }

        _region = new OcrRegionWindow(_ctx, image, OnRegionSelectedAsync);
        _region.Closed += (_, _) => _region = null;
        _region.Show();
        _region.Activate();
    }

    /// <summary>框选完成后的回调：识别 → 显示结果 → 复制。</summary>
    private async Task OnRegionSelectedAsync(BitmapSource region)
    {
        var ctx = _ctx;
        if (ctx is null)
        {
            return;
        }

        _result ??= new OcrResultWindow(ctx);
        if (!_result.IsVisible)
        {
            // 先显示一个"正在识别"的状态，别让用户对着空气等
            _result.ShowResult(new OcrResult(false, "", Array.Empty<string>(), "正在识别…"));
        }

        var result = await OcrEngineService.RecognizeAsync(region);
        _result.ShowResult(result);

        if (result.Success)
        {
            ctx.Notify("OCR 截图识字",
                $"识别到 {result.Text.Length} 字，已复制到剪贴板。");
        }
        else
        {
            ctx.Notify("OCR 截图识字", "没有识别到文字。");
        }
    }

    public void Stop()
    {
        _region?.Close();
        _region = null;

        _result?.Close();
        _result = null;
    }
}
