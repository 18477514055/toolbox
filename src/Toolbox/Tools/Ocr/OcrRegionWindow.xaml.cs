using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.Ocr;

/// <summary>
/// OCR 的框选层：整屏冻结 → 拖拽框选 → 把那一块交给 OCR。
///
/// 实现要点与「快捷截图」一致（同一个坑，同一套解法）：
///   · 窗口逻辑坐标 → 位图像素坐标的换算用 <see cref="ScreenCoordinateMapper"/>
///     （按显示器真实 DPI，多屏混合 DPI 也不偏）；
///   · 极小选区视为误触，直接忽略，不送去做无意义的 OCR。
/// </summary>
internal sealed partial class OcrRegionWindow : Window
{
    private readonly ToolboxContext _ctx;
    private readonly BitmapSource _full;
    private readonly Func<BitmapSource, Task> _onRegionSelected;

    private bool _dragging;
    private Point _start;
    private Int32Rect _sel;
    private bool _hasSel;

    /// <summary>小于这个尺寸视为误触（OCR 也没意义）。</summary>
    private const int MinSize = 8;

    /// <summary>选完就关，避免重复触发识别。</summary>
    private bool _finished;

    public OcrRegionWindow(ToolboxContext ctx, BitmapSource full, Func<BitmapSource, Task> onRegionSelected)
    {
        _ctx = ctx;
        _full = full;
        _onRegionSelected = onRegionSelected;

        InitializeComponent();

        Backdrop.Source = _full;

        Loaded += OnLoaded;
        Root.MouseLeftButtonDown += OnMouseDown;
        Root.MouseMove += OnMouseMove;
        Root.MouseLeftButtonUp += OnMouseUp;
        Root.MouseRightButtonDown += (_, _) => Cancel();
        Root.SizeChanged += (_, _) => UpdateDimmer();
        PreviewKeyDown += OnKeyDown;
        KeyDown += OnKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        UpdateDimmer();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _start = e.GetPosition(Root);
        _dragging = true;
        Root.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var p = e.GetPosition(Root);
        var x = Math.Min(_start.X, p.X);
        var y = Math.Min(_start.Y, p.Y);
        var w = Math.Abs(p.X - _start.X);
        var h = Math.Abs(p.Y - _start.Y);

        _sel = new Int32Rect((int)Math.Round(x), (int)Math.Round(y),
            (int)Math.Round(w), (int)Math.Round(h));
        _hasSel = w >= MinSize && h >= MinSize;

        if (_hasSel)
        {
            SelRect.Visibility = Visibility.Visible;
            SelRect.Margin = new Thickness(x, y, 0, 0);
            SelRect.Width = w;
            SelRect.Height = h;
        }
        else
        {
            SelRect.Visibility = Visibility.Collapsed;
        }

        UpdateDimmer();
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Root.ReleaseMouseCapture();
        e.Handled = true;

        if (_hasSel && _sel.Width >= MinSize && _sel.Height >= MinSize)
        {
            _ = FinishAsync();
        }
    }

    private void UpdateDimmer()
    {
        var w = Root.ActualWidth;
        var h = Root.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var outer = new RectangleGeometry(new Rect(0, 0, w, h));

        if (_hasSel)
        {
            var inner = new RectangleGeometry(new Rect(_sel.X, _sel.Y, _sel.Width, _sel.Height));
            Dimmer.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        }
        else
        {
            Dimmer.Data = outer;
        }
    }

    private void Cancel() => Close();

    private async Task FinishAsync()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;

        try
        {
            // 逻辑坐标 → 位图像素坐标（系数按显示器真实 DPI，见 ScreenCoordinateMapper）
            var scale = ScreenCoordinateMapper.GetScaleForWindow(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);

            var rect = ScreenCoordinateMapper.ToPixelRect(
                _sel.X, _sel.Y, _sel.Width, _sel.Height,
                scale, scale, _full.PixelWidth, _full.PixelHeight);

            var crop = new CroppedBitmap(_full, rect);
            if (crop.CanFreeze)
            {
                crop.Freeze();
            }

            Log.Line($"OCR 选区：像素 ({rect.X},{rect.Y},{rect.Width}×{rect.Height})，DPI 系数 {scale:0.###}");

            // 先关掉遮罩再做识别 —— 不然识别期间整屏一直暗着，像卡死了
            Close();

            await _onRegionSelected(crop);
        }
        catch (Exception ex)
        {
            Log.Exception("OCR 选区处理失败", ex);
            _ctx.Notify("OCR 截图识字", $"识别失败：{ex.Message}");
            Close();
        }
    }
}
