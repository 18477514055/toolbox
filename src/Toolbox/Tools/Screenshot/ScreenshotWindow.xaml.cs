using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.Screenshot;

/// <summary>
/// 截图遮罩窗口：全屏透明，背景是冻结的整屏，用户拖拽框选区域。
///
/// 坐标约定（关键，否则截出来的图会偏）：
///   拖拽用的坐标全是**窗口逻辑坐标**（WPF 单位）。裁剪时要换算回**位图像素坐标**，
///   乘的系数 = 位图像素宽 / 背景图实际显示宽。这个系数是运行时量出来的，
///   所以不管 DPI 多少、几块屏，都不会算偏（和图片裁剪工具 §五·工具2 是同一个坑）。
/// </summary>
internal sealed partial class ScreenshotWindow : Window
{
    private readonly ToolboxContext _ctx;
    private readonly BitmapSource _full;

    private bool _dragging;
    private Point _start;
    private Int32Rect _sel;
    private bool _hasSel;

    private const int MinSize = 3;

    public ScreenshotWindow(ToolboxContext ctx, BitmapSource full)
    {
        _ctx = ctx;
        _full = full;

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

    // ---------------------------------------------------------------- 布局

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 用虚拟屏幕（含多显示器）的 WPF 坐标把窗口铺满，背景图 Stretch=Fill 跟随铺满。
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        UpdateDimmer();
    }

    // ---------------------------------------------------------------- 键盘

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    // ---------------------------------------------------------------- 鼠标拖拽

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

        _sel = new Int32Rect((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h));
        _hasSel = w >= MinSize && h >= MinSize;

        if (_hasSel)
        {
            SelRect.Visibility = Visibility.Visible;
            // SelRect 在 Grid 的单元格里，用 Margin 定位（Canvas.SetLeft 在 Grid 里不生效）
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
            Finish();
        }
    }

    /// <summary>更新「除选区外都变暗」的蒙版：用 Exclude 在大矩形上挖掉选区那一小块。</summary>
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
            var inner = new RectangleGeometry(
                new Rect(_sel.X, _sel.Y, _sel.Width, _sel.Height));
            Dimmer.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        }
        else
        {
            Dimmer.Data = outer;
        }
    }

    // ---------------------------------------------------------------- 收尾

    private void Cancel()
    {
        Close();
    }

    private void Finish()
    {
        try
        {
            // 窗口逻辑坐标 → 位图像素坐标。
            //
            // ⚠️ 系数取自**本窗口所在显示器的真实 DPI**（dpi/96），不是
            //    「位图像素宽 ÷ 背景图显示宽」那一个全局比值。
            //    全局比值在**多屏且各屏缩放不同**时必然算错：一个比值表达不了两个比例。
            //    详见 Core\ScreenCoordinateMapper.cs 顶部记录的实测反例。
            //
            //    本机当前是单屏 100%（系数 1.0），两种算法结果完全相同 ——
            //    所以这个修复对现有行为零影响，只在用户接上缩放不同的显示器时才体现价值。
            var scale = ScreenCoordinateMapper.GetScaleForWindow(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);

            var rect = ScreenCoordinateMapper.ToPixelRect(
                _sel.X, _sel.Y, _sel.Width, _sel.Height,
                scale, scale,
                _full.PixelWidth, _full.PixelHeight);

            Log.Line($"截图坐标换算：逻辑 ({_sel.X},{_sel.Y},{_sel.Width}×{_sel.Height})"
                     + $" × DPI系数 {scale:0.###} → 像素 ({rect.X},{rect.Y},{rect.Width}×{rect.Height})");

            var crop = new CroppedBitmap(_full, rect);
            if (crop.CanFreeze)
            {
                crop.Freeze();
            }

            var dir = ResolveOutputDir();
            var path = Path.Combine(dir, $"截图_{DateTime.Now:yyyyMMdd-HHmmss}.png");
            path = EnsureUnique(path);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(crop));
            using (var fs = File.Create(path))
            {
                encoder.Save(fs);
            }

            // 顺便复制到剪贴板（走带自污染标记的写入，不会污染剪贴板历史）
            ClipboardWriter.SetImage(crop, out _);

            _ctx.Notify("快捷截图", $"已保存：{Path.GetFileName(path)}（也已复制到剪贴板）");
            Log.Line($"截图已保存：{path}（{rect.Width}×{rect.Height} px）");
        }
        catch (Exception ex)
        {
            Log.Exception("保存截图失败", ex);
            _ctx.Notify("快捷截图", $"保存失败：{ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    private string ResolveOutputDir()
    {
        var cfg = _ctx.Settings.ScreenshotOutputDir;
        var dir = string.IsNullOrWhiteSpace(cfg) ? AppPaths.ScreenshotDir : cfg!;

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // 创建失败就退回默认目录，别让截图整体失败
            dir = AppPaths.ScreenshotDir;
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // 连默认目录都建不出，只能让 File.Create 抛错并走失败提示
            }
        }

        return dir;
    }

    private static string EnsureUnique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }
}
