using System.Windows;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Shell;
using Toolbox.Tools.Screenshot;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Toolbox.Tools.QrCode;

/// <summary>
/// 二维码工具窗口：两个标签页 —— 生成 / 扫描。
///
/// 复用情况（按要求，不重写已有模块）：
///   · 截屏：<see cref="ScreenCapture.CaptureFullScreen"/>（与截图、OCR 同一个 GDI 抓屏）；
///   · 坐标换算：<see cref="ScreenCoordinateMapper"/>（按显示器真实 DPI）；
///   · 写剪贴板：<see cref="ClipboardWriter"/>（带自污染标记 ⇒ 不进剪贴板历史）；
///   · 保存目录：<see cref="AppPaths.ScreenshotDir"/> 的兄弟目录（默认落数据目录下 qrcodes\）；
///   · 设置存储：<see cref="Settings.QrOutputDir"/> / <see cref="Settings.QrSize"/>；
///   · 日志：<see cref="Log"/>。
/// </summary>
internal sealed partial class QrWindow : Window
{
    private readonly ToolboxContext _ctx;

    private QrMatrix? _current;
    private BitmapSource? _currentImage;

    public QrWindow(ToolboxContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        // 恢复上次的尺寸设置
        var saved = ctx.Settings.QrSize;
        SizeBox.Text = (saved >= 128 && saved <= 4096 ? saved : 512).ToString();

        GenerateBtn.Click += (_, _) => Generate();
        SaveBtn.Click += (_, _) => SavePng();
        CopyImageBtn.Click += (_, _) => CopyImage();
        ScanScreenBtn.Click += (_, _) => ScanFromScreen();
        ScanFileBtn.Click += (_, _) => ScanFromFile();
        CopyResultBtn.Click += (_, _) => CopyResult();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    // ================================================================ 生成

    private QrEcc SelectedEcc() => EccCombo.SelectedIndex switch
    {
        1 => QrEcc.L,
        2 => QrEcc.Q,
        3 => QrEcc.H,
        _ => QrEcc.M,
    };

    private void Generate()
    {
        var text = ContentBox.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            SetGenStatus("先输入要编码的内容。", isError: true);
            return;
        }

        var ecc = SelectedEcc();
        var matrix = QrEncoder.Encode(text, ecc);

        if (matrix is null)
        {
            // 容量不足要说清楚**怎么办**，不是只说"失败"
            var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
            SetGenStatus(
                $"内容太长了，装不下（当前 {bytes} 字节）。\n"
                + "可以：① 把纠错等级降到 L；② 缩短内容；③ 改用短链接。",
                isError: true);
            return;
        }

        _current = matrix;

        var size = 512;
        if (int.TryParse(SizeBox.Text.Trim(), out var parsed) && parsed >= 128 && parsed <= 4096)
        {
            size = parsed;
        }

        _currentImage = QrRenderer.Render(matrix, size);

        // 显示在内容框下方（用一个简单的弹窗预览不方便，直接把图贴到状态区）
        ShowPreview(_currentImage);

        _ctx.Settings.QrSize = size;
        _ctx.SaveSettings();

        var bytesCount = System.Text.Encoding.UTF8.GetByteCount(text);
        SetGenStatus(
            $"生成成功：版本 {matrix.Version}（{matrix.Size}×{matrix.Size} 模块）、"
            + $"纠错 {ecc}、掩码 {matrix.Mask}、{bytesCount} 字节。\n"
            + "可以保存为 PNG 或复制到剪贴板。",
            isError: false);

        SaveBtn.IsEnabled = true;
        CopyImageBtn.IsEnabled = true;

        Log.Line($"二维码已生成：版本 {matrix.Version}，纠错 {ecc}，{bytesCount} 字节。");
    }

    /// <summary>
    /// 预览。
    ///
    /// 这里刻意**用独立窗口**而不是在主窗口里塞一块 Image：
    ///   主窗口的布局已经排满，硬塞会把两个标签页都挤变形；
    ///   而且预览窗口可以缩放看细节，比嵌在角落里实用。
    /// </summary>
    private void ShowPreview(BitmapSource image)
    {
        var win = new Window
        {
            Title = "二维码预览",
            Width = 420,
            Height = 480,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White,
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };

        var img = new System.Windows.Controls.Image
        {
            Source = image,
            Stretch = System.Windows.Media.Stretch.Uniform,
            MaxWidth = 380,
            MaxHeight = 380,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var tip = new System.Windows.Controls.TextBlock
        {
            Text = "把鼠标移到图上，然后「保存为 PNG」或「复制图片到剪贴板」。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x8A, 0x94, 0xA6)),
        };

        panel.Children.Add(img);
        panel.Children.Add(tip);
        win.Content = panel;
        win.Show();
    }

    private void SavePng()
    {
        if (_currentImage is null)
        {
            return;
        }

        try
        {
            var dir = string.IsNullOrWhiteSpace(_ctx.Settings.QrOutputDir)
                ? AppPaths.Ensure(Path.Combine(AppPaths.Root, "qrcodes"))
                : _ctx.Settings.QrOutputDir!;

            System.IO.Directory.CreateDirectory(dir);

            var name = $"二维码_{DateTime.Now:yyyyMMdd_HHmmss}.png";

            // 复用「格式转换」工具里已有的 FileNaming.UniquePath，不再自己写一份
            var path = Toolbox.Tools.Convert.FileNaming.UniquePath(Path.Combine(dir, name));

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(_currentImage));
            using (var fs = File.Create(path))
            {
                enc.Save(fs);
            }

            SetGenStatus($"已保存：{path}", isError: false);
            _ctx.Notify("二维码工具", $"已保存到 {Path.GetFileName(path)}");
            Log.Line($"二维码已保存：{path}");
        }
        catch (Exception ex)
        {
            Log.Exception("保存二维码 PNG 失败", ex);
            SetGenStatus($"保存失败：{ex.Message}", isError: true);
        }
    }

    private void CopyImage()
    {
        if (_currentImage is null)
        {
            return;
        }

        // 走 ClipboardWriter（带自污染标记）⇒ 不进剪贴板历史
        if (ClipboardWriter.SetImage(_currentImage, out var err))
        {
            SetGenStatus("图片已复制到剪贴板，可以直接粘贴。", isError: false);
        }
        else
        {
            SetGenStatus($"复制失败：{err}", isError: true);
        }
    }

    private void SetGenStatus(string text, bool isError)
    {
        GenStatus.Text = text;
        GenStatus.Foreground = new System.Windows.Media.SolidColorBrush(isError
            ? System.Windows.Media.Color.FromRgb(0xE0, 0x3B, 0x3B)
            : System.Windows.Media.Color.FromRgb(0x12, 0xA1, 0x50));
    }

    // ================================================================ 扫描

    private void ScanFromScreen()
    {
        var full = ScreenCapture.CaptureFullScreen();
        if (full is null)
        {
            SetScanStatus("截屏失败：无法捕获屏幕。", isError: true);
            return;
        }

        // 复用 OCR 那套框选层（纯选区，回调里自己决定做什么）
        var picker = new Ocr.OcrRegionWindow(_ctx, full, async region =>
        {
            await Task.Yield();
            DoScan(region);
        });

        picker.Show();
        picker.Activate();
    }

    private void ScanFromFile()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择包含二维码的图片",
                Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
                CheckFileExists = true,
            };

            if (dlg.ShowDialog(this) != true)
            {
                return;
            }

            using var fs = File.OpenRead(dlg.FileName);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            DoScan(dec.Frames[0]);
        }
        catch (Exception ex)
        {
            Log.Exception("读取二维码图片失败", ex);
            SetScanStatus($"读图失败：{ex.Message}", isError: true);
        }
    }

    private void DoScan(BitmapSource image)
    {
        var result = QrScanner.Scan(image);

        if (result.Success)
        {
            ScanResultBox.Text = result.Text;
            SetScanStatus($"识别成功 —— {result.Version}，模块约 {result.ModuleSize}px。"
                          + "内容已填在下面，可点「复制结果」。", isError: false);

            // 顺手复制，多数人就是要"扫完直接粘"
            if (ClipboardWriter.SetText(result.Text, out _))
            {
                _ctx.Notify("二维码工具", "已识别并复制到剪贴板。");
            }
        }
        else
        {
            ScanResultBox.Text = "";
            SetScanStatus(result.Error, isError: true);
        }
    }

    private void CopyResult()
    {
        var text = ScanResultBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (ClipboardWriter.SetText(text, out var err))
        {
            SetScanStatus("结果已复制到剪贴板。", isError: false);
        }
        else
        {
            SetScanStatus($"复制失败：{err}", isError: true);
        }
    }

    private void SetScanStatus(string text, bool isError)
    {
        ScanStatus.Text = text;
        ScanStatus.Foreground = new System.Windows.Media.SolidColorBrush(isError
            ? System.Windows.Media.Color.FromRgb(0xE0, 0x3B, 0x3B)
            : System.Windows.Media.Color.FromRgb(0x12, 0xA1, 0x50));
    }
}
