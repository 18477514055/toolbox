using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Shell;
using Path = System.IO.Path;
using File = System.IO.File;
using WClipboard = System.Windows.Clipboard;

namespace Toolbox.Tools.ImageCrop;

/// <summary>
/// 图片裁剪窗口。
///
/// 两条容易踩的坑，这里都正面处理了（交接文档 §五·工具2）：
///   1. **DPI**：裁剪必须按**像素**算，不能按显示尺寸算。96 与 144 DPI 下
///      PixelWidth 和显示宽度不一样，按显示尺寸裁会差一截。
///      所以选区内部一律用图片像素坐标，显示时才乘缩放系数。
///   2. **超大图**：一张 8000×6000 的图 ≈ 192 MB 内存。这里不做分块，
///      但也不复制多份——预览直接用原 BitmapSource，交给 WPF 缩放。
/// </summary>
internal sealed partial class ImageCropWindow : Window
{
    private readonly ToolboxContext _ctx;

    private BitmapSource? _original;
    private BitmapSource? _current;

    private double _scale = 1;
    private double _offX;
    private double _offY;

    private Int32Rect _sel;
    private bool _hasSel;

    private enum DragMode
    {
        None,
        Create,
        Move,
        ResizeTL,
        ResizeTR,
        ResizeBL,
        ResizeBR,
    }

    private DragMode _drag = DragMode.None;
    private Point _dragStartStage;
    private Int32Rect _dragStartSel;

    public ImageCropWindow(ToolboxContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        Stage.SizeChanged += (_, _) => Relayout();
        Stage.MouseLeftButtonDown += OnStageMouseDown;
        Stage.MouseMove += OnStageMouseMove;
        Stage.MouseLeftButtonUp += OnStageMouseUp;

        FromClipboardBtn.Click += (_, _) => LoadFromClipboard();
        OpenFileBtn.Click += (_, _) => OpenFile();
        RevertBtn.Click += (_, _) => Revert();
        CropBtn.Click += (_, _) => DoCrop();
        SelectAllBtn.Click += (_, _) => SelectAll();
        ResizeBtn.Click += (_, _) => DoResize();
        SaveBtn.Click += (_, _) => Save();

        WidthBox.TextChanged += (_, _) => SyncAspectFromWidth();
        HeightBox.TextChanged += (_, _) => SyncAspectFromHeight();

        JpegQuality.ValueChanged += (_, _) =>
            JpegQualityLabel.Text = ((int)JpegQuality.Value).ToString();

        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        };
    }

    // ---------------------------------------------------------------- 载入

    public void LoadImage(BitmapSource image, string label)
    {
        try
        {
            // 冻结：跨线程用、以及避免 WPF 延迟解码导致的"用着用着文件被锁"
            if (image.CanFreeze)
            {
                image.Freeze();
            }

            _original = image;
            _current = image;
            _hasSel = false;

            SourceInfo.Text = $"{label}　{image.PixelWidth}×{image.PixelHeight}";

            UpdateSizeText();
            UpdateAspectBoxes();
            Relayout();

            StatusText.Text = "已载入。在图上拖动鼠标框选区域，或直接在右侧输入目标像素尺寸。";
        }
        catch (Exception ex)
        {
            Log.Exception("载入图片失败", ex);
            StatusText.Text = $"载入失败：{ex.Message}";
        }
    }

    private void LoadFromClipboard()
    {
        try
        {
            if (!WClipboard.ContainsImage())
            {
                StatusText.Text = "剪贴板里现在没有图片。先截图（Win+Shift+S）或复制一张图，再点这里。";
                return;
            }

            var image = WClipboard.GetImage();
            if (image is null)
            {
                StatusText.Text = "剪贴板里读不到图片数据。";
                return;
            }

            LoadImage(image, "剪贴板图片");
        }
        catch (Exception ex)
        {
            // 坑 2：Clipboard.GetImage 在某些来源上会直接抛 NRE
            Log.Exception("从剪贴板取图失败", ex);
            StatusText.Text = $"从剪贴板取图失败：{ex.Message}";
        }
    }

    private void OpenFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
            InitialDirectory = _ctx.Settings.LastImageDir ?? "",
        };

        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        LoadFromPath(dlg.FileName);
    }

    private void LoadFromPath(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);

            var decoder = BitmapDecoder.Create(ms,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();

            _ctx.Settings.LastImageDir = Path.GetDirectoryName(path);
            _ctx.SaveSettings();

            LoadImage(frame, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Exception($"打开图片失败：{path}", ex);
            StatusText.Text = $"打不开这个文件：{ex.Message}";
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                LoadFromPath(files[0]);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("拖拽载入图片失败", ex);
        }
    }

    private void Revert()
    {
        if (_original is null)
        {
            return;
        }

        _current = _original;
        _hasSel = false;
        UpdateSizeText();
        UpdateAspectBoxes();
        Relayout();
        StatusText.Text = "已还原成最初载入的图。";
    }

    // ---------------------------------------------------------------- 布局

    private void Relayout()
    {
        if (_current is null)
        {
            return;
        }

        var cw = Stage.ActualWidth;
        var ch = Stage.ActualHeight;
        if (cw <= 1 || ch <= 1)
        {
            return;
        }

        // 等比缩放到能完整显示
        _scale = Math.Min(cw / _current.PixelWidth, ch / _current.PixelHeight);
        if (_scale <= 0 || double.IsInfinity(_scale))
        {
            _scale = 1;
        }

        var dw = _current.PixelWidth * _scale;
        var dh = _current.PixelHeight * _scale;

        _offX = (cw - dw) / 2;
        _offY = (ch - dh) / 2;

        Preview.Source = _current;
        Preview.Width = dw;
        Preview.Height = dh;
        Canvas.SetLeft(Preview, _offX);
        Canvas.SetTop(Preview, _offY);

        UpdateSelectionVisual();
    }

    private void UpdateSelectionVisual()
    {
        if (!_hasSel || _sel.Width < 1 || _sel.Height < 1)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            // 变量名用 hl 而不是 h：本方法后面还有个 h（选区高度），
            // C# 的声明空间是整个块，foreach 里再叫 h 会 CS0136。
            foreach (var hl in Handles())
            {
                hl.Visibility = Visibility.Collapsed;
            }

            SelectionInfo.Text = "在左边的图上按住鼠标拖动，框出要保留的区域。";
            return;
        }

        var x = _offX + _sel.X * _scale;
        var y = _offY + _sel.Y * _scale;
        var w = _sel.Width * _scale;
        var h = _sel.Height * _scale;

        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;

        PlaceHandle(HandleTL, x, y);
        PlaceHandle(HandleTR, x + w, y);
        PlaceHandle(HandleBL, x, y + h);
        PlaceHandle(HandleBR, x + w, y + h);

        SelectionInfo.Text = $"选区：{_sel.X}, {_sel.Y}　{_sel.Width} × {_sel.Height} px";
    }

    private IEnumerable<System.Windows.Shapes.Rectangle> Handles()
    {
        yield return HandleTL;
        yield return HandleTR;
        yield return HandleBL;
        yield return HandleBR;
    }

    private static void PlaceHandle(System.Windows.Shapes.Rectangle handle, double x, double y)
    {
        handle.Visibility = Visibility.Visible;
        Canvas.SetLeft(handle, x - handle.Width / 2);
        Canvas.SetTop(handle, y - handle.Height / 2);
    }

    // ---------------------------------------------------------------- 鼠标交互

    private (double X, double Y) ToImage(Point stagePoint)
        => ((stagePoint.X - _offX) / _scale, (stagePoint.Y - _offY) / _scale);

    private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        var p = e.GetPosition(Stage);
        _dragStartStage = p;
        _dragStartSel = _sel;

        _drag = HitTestMode(p);
        if (_drag == DragMode.Create)
        {
            var (ix, iy) = ToImage(p);
            _sel = new Int32Rect(Clamp(ix, _current.PixelWidth), Clamp(iy, _current.PixelHeight), 0, 0);
            _hasSel = false;
        }

        Stage.CaptureMouse();
        e.Handled = true;
    }

    private DragMode HitTestMode(Point p)
    {
        if (!_hasSel)
        {
            return DragMode.Create;
        }

        var x = _offX + _sel.X * _scale;
        var y = _offY + _sel.Y * _scale;
        var w = _sel.Width * _scale;
        var h = _sel.Height * _scale;

        const double grab = 12;

        if (Near(p, x, y, grab))
        {
            return DragMode.ResizeTL;
        }

        if (Near(p, x + w, y, grab))
        {
            return DragMode.ResizeTR;
        }

        if (Near(p, x, y + h, grab))
        {
            return DragMode.ResizeBL;
        }

        if (Near(p, x + w, y + h, grab))
        {
            return DragMode.ResizeBR;
        }

        if (p.X >= x && p.X <= x + w && p.Y >= y && p.Y <= y + h)
        {
            return DragMode.Move;
        }

        return DragMode.Create;
    }

    private static bool Near(Point p, double x, double y, double radius)
        => Math.Abs(p.X - x) <= radius && Math.Abs(p.Y - y) <= radius;

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        if (_current is null || _drag == DragMode.None)
        {
            return;
        }

        var p = e.GetPosition(Stage);
        var (ix, iy) = ToImage(p);

        var imgW = _current.PixelWidth;
        var imgH = _current.PixelHeight;

        switch (_drag)
        {
            case DragMode.Create:
            {
                var (sx, sy) = ToImage(_dragStartStage);
                // 显式转成 double 走 Clamp(double, double) 这个重载：
                // 不转的话会命中 Clamp(double, int)（返回 int），
                // 而 Math.Round(int) 在 decimal / double 两个重载之间是二义的。
                var x0 = Clamp(Math.Min(sx, ix), (double)imgW);
                var y0 = Clamp(Math.Min(sy, iy), (double)imgH);
                var x1 = Clamp(Math.Max(sx, ix), (double)imgW);
                var y1 = Clamp(Math.Max(sy, iy), (double)imgH);

                var rect = new Int32Rect((int)Math.Round(x0), (int)Math.Round(y0),
                    (int)Math.Round(x1 - x0), (int)Math.Round(y1 - y0));

                _sel = ApplyRatio(rect, anchorTopLeft: true);
                _hasSel = _sel.Width >= 1 && _sel.Height >= 1;
                break;
            }

            case DragMode.Move:
            {
                var dx = ix - ToImage(_dragStartStage).X;
                var dy = iy - ToImage(_dragStartStage).Y;

                var nx = Clamp(_dragStartSel.X + dx, (double)(imgW - _dragStartSel.Width));
                var ny = Clamp(_dragStartSel.Y + dy, (double)(imgH - _dragStartSel.Height));

                _sel = new Int32Rect((int)Math.Round(nx), (int)Math.Round(ny),
                    _dragStartSel.Width, _dragStartSel.Height);
                break;
            }

            default:
            {
                // 四个角的缩放：固定对角，移动当前角
                double left = _dragStartSel.X;
                double top = _dragStartSel.Y;
                double right = _dragStartSel.X + _dragStartSel.Width;
                double bottom = _dragStartSel.Y + _dragStartSel.Height;

                var cx = Clamp(ix, imgW);
                var cy = Clamp(iy, imgH);

                switch (_drag)
                {
                    case DragMode.ResizeTL:
                        left = cx;
                        top = cy;
                        break;
                    case DragMode.ResizeTR:
                        right = cx;
                        top = cy;
                        break;
                    case DragMode.ResizeBL:
                        left = cx;
                        bottom = cy;
                        break;
                    case DragMode.ResizeBR:
                        right = cx;
                        bottom = cy;
                        break;
                }

                var x0 = Math.Min(left, right);
                var y0 = Math.Min(top, bottom);

                var rect = new Int32Rect(
                    (int)Math.Round(Math.Max(0, x0)),
                    (int)Math.Round(Math.Max(0, y0)),
                    (int)Math.Round(Math.Abs(right - left)),
                    (int)Math.Round(Math.Abs(bottom - top)));

                _sel = ApplyRatio(rect, anchorTopLeft: _drag is DragMode.ResizeTL or DragMode.ResizeTR);
                _hasSel = _sel.Width >= 1 && _sel.Height >= 1;
                break;
            }
        }

        UpdateSelectionVisual();
    }

    private void OnStageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            return;
        }

        _drag = DragMode.None;
        Stage.ReleaseMouseCapture();
        e.Handled = true;
    }

    private static double Clamp(double v, double max) => Math.Max(0, Math.Min(v, max));

    private int Clamp(double v, int max) => (int)Math.Round(Clamp(v, (double)max));

    /// <summary>固定比例：只调高度，宽度为准（保持用户拖出来的宽度最直观）。</summary>
    private Int32Rect ApplyRatio(Int32Rect rect, bool anchorTopLeft)
    {
        var ratio = CurrentRatio();
        if (ratio is null || rect.Width < 1)
        {
            return rect;
        }

        var h = (int)Math.Round(rect.Width / ratio.Value);
        if (h < 1)
        {
            h = 1;
        }

        if (_current is not null && rect.Y + h > _current.PixelHeight)
        {
            h = Math.Max(1, _current.PixelHeight - rect.Y);
        }

        return anchorTopLeft
            ? new Int32Rect(rect.X, rect.Y, rect.Width, h)
            : new Int32Rect(rect.X, rect.Y + rect.Height - h, rect.Width, h);
    }

    private double? CurrentRatio() => RatioBox.SelectedIndex switch
    {
        1 => 1.0,
        2 => 16.0 / 9.0,
        3 => 9.0 / 16.0,
        4 => 4.0 / 3.0,
        5 => 3.0 / 4.0,
        _ => null,
    };

    private void SelectAll()
    {
        if (_current is null)
        {
            return;
        }

        _sel = new Int32Rect(0, 0, _current.PixelWidth, _current.PixelHeight);
        _hasSel = true;
        UpdateSelectionVisual();
    }

    // ---------------------------------------------------------------- 裁剪

    private void DoCrop()
    {
        if (_current is null)
        {
            StatusText.Text = "先载入一张图片。";
            return;
        }

        if (!_hasSel || _sel.Width < 1 || _sel.Height < 1)
        {
            StatusText.Text = "还没框选区域。在图上按住鼠标拖一下，或者点「全选」。";
            return;
        }

        try
        {
            var cropped = ImageOps.Crop(_current, _sel);

            _current = cropped;
            _hasSel = false;

            UpdateSizeText();
            UpdateAspectBoxes();
            Relayout();

            StatusText.Text = $"已裁出 {cropped.PixelWidth} × {cropped.PixelHeight} px。可以继续裁，或直接另存。";
        }
        catch (Exception ex)
        {
            Log.Exception("裁剪失败", ex);
            StatusText.Text = $"裁剪失败：{ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 改尺寸

    private void SyncAspectFromWidth()
    {
        if (LockRatioBox.IsChecked != true || _current is null || !WidthBox.IsFocused)
        {
            return;
        }

        if (int.TryParse(WidthBox.Text.Trim(), out var w) && w > 0)
        {
            var h = (int)Math.Round(w * (double)_current.PixelHeight / _current.PixelWidth);
            HeightBox.Text = h.ToString();
        }
    }

    private void SyncAspectFromHeight()
    {
        if (LockRatioBox.IsChecked != true || _current is null || !HeightBox.IsFocused)
        {
            return;
        }

        if (int.TryParse(HeightBox.Text.Trim(), out var h) && h > 0)
        {
            var w = (int)Math.Round(h * (double)_current.PixelWidth / _current.PixelHeight);
            WidthBox.Text = w.ToString();
        }
    }

    private void UpdateAspectBoxes()
    {
        if (_current is null)
        {
            return;
        }

        WidthBox.Text = _current.PixelWidth.ToString();
        HeightBox.Text = _current.PixelHeight.ToString();
    }

    private void DoResize()
    {
        if (_current is null)
        {
            StatusText.Text = "先载入一张图片。";
            return;
        }

        if (!int.TryParse(WidthBox.Text.Trim(), out var w) || !int.TryParse(HeightBox.Text.Trim(), out var h)
            || w <= 0 || h <= 0)
        {
            StatusText.Text = "宽和高都要填正整数。";
            return;
        }

        if (PercentBox.IsChecked == true)
        {
            w = Math.Max(1, (int)Math.Round(_current.PixelWidth * w / 100.0));
            h = Math.Max(1, (int)Math.Round(_current.PixelHeight * h / 100.0));
        }

        if (w > 20000 || h > 20000)
        {
            StatusText.Text = "目标尺寸太大了（上限 20000 px）。";
            return;
        }

        try
        {
            var highQuality = QualityBox.SelectedIndex != 1;
            _current = ImageOps.Resize(_current, w, h, highQuality);
            _hasSel = false;

            UpdateSizeText();
            UpdateAspectBoxes();
            Relayout();

            StatusText.Text = $"尺寸已改为 {w} × {h} px（实际输出就是这个像素数）。";
        }
        catch (Exception ex)
        {
            Log.Exception("改变图片尺寸失败", ex);
            StatusText.Text = $"改变尺寸失败：{ex.Message}";
        }
    }

    private void UpdateSizeText()
    {
        if (_current is null)
        {
            SizeText.Text = "—";
            return;
        }

        var original = _original is null
            ? ""
            : $"　（原图 {_original.PixelWidth}×{_original.PixelHeight}）";

        SizeText.Text = $"当前：{_current.PixelWidth} × {_current.PixelHeight} px{original}";
    }

    // ---------------------------------------------------------------- 另存

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JpegQualityPanel is not null)
        {
            JpegQualityPanel.Visibility = FormatBox.SelectedIndex == 1
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void Save()
    {
        if (_current is null)
        {
            StatusText.Text = "还没有图片可以保存。";
            return;
        }

        try
        {
            var (ext, filter, defaultExt) = FormatBox.SelectedIndex switch
            {
                1 => (".jpg", "JPG 图片|*.jpg", "jpg"),
                2 => (".bmp", "BMP 图片|*.bmp", "bmp"),
                _ => (".png", "PNG 图片|*.png", "png"),
            };

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "另存为",
                Filter = filter,
                DefaultExt = defaultExt,
                FileName = $"裁剪_{DateTime.Now:yyyyMMdd_HHmmss}{ext}",
                InitialDirectory = _ctx.Settings.LastImageDir ?? "",
            };

            if (dlg.ShowDialog(this) != true)
            {
                return;
            }

            // ★ 永远另存，绝不覆盖：目标已存在就自动加 (1)
            var finalPath = EnsureUnique(dlg.FileName);

            BitmapEncoder encoder = FormatBox.SelectedIndex switch
            {
                1 => new JpegBitmapEncoder { QualityLevel = (int)JpegQuality.Value },
                2 => new BmpBitmapEncoder(),
                _ => new PngBitmapEncoder(),
            };

            encoder.Frames.Add(BitmapFrame.Create(_current));

            using (var fs = File.Create(finalPath))
            {
                encoder.Save(fs);
            }

            _ctx.Settings.LastImageDir = Path.GetDirectoryName(finalPath);
            _ctx.SaveSettings();

            StatusText.Text = $"已保存：{finalPath}";
            _ctx.Notify("图片裁剪", $"已保存 {Path.GetFileName(finalPath)}");
        }
        catch (Exception ex)
        {
            Log.Exception("保存图片失败", ex);
            StatusText.Text = $"保存失败：{ex.Message}";
        }
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
