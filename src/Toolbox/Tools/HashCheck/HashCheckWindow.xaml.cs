using System.Windows;
using System.Windows.Media;
using Toolbox.Core;
using Toolbox.Shell;
using Path = System.IO.Path;
using File = System.IO.File;
// 同 HashService：别名 File/Path 会遮蔽 System.IO 下的其他类型，显式补一行
using System.IO;

namespace Toolbox.Tools.HashCheck;

/// <summary>
/// 哈希校验窗口。支持三种用法：
///   ① 单文件算哈希（MD5 / SHA1 / SHA256 / SHA512）；
///   ② 单文件 + 期望值比对（下载站给的校验值粘进来）；
///   ③ 两个文件比对是否一致。
///
/// 大文件用流式计算 + 进度条 + 可取消（见 <see cref="HashService"/>）。
/// </summary>
internal sealed partial class HashCheckWindow : Window
{
    private readonly ToolboxContext _ctx;
    private CancellationTokenSource? _cts;
    private bool _running;

    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x12, 0xA1, 0x50));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xE0, 0x3B, 0x3B));
    private static readonly SolidColorBrush Grey = new(Color.FromRgb(0x8A, 0x94, 0xA6));

    public HashCheckWindow(ToolboxContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        // 恢复上次用的算法
        AlgoCombo.SelectedIndex = HashService.FromDisplayName(ctx.Settings.LastHashAlgorithm) switch
        {
            HashAlgorithmKind.MD5 => 1,
            HashAlgorithmKind.SHA1 => 2,
            HashAlgorithmKind.SHA512 => 3,
            _ => 0,
        };

        BrowseABtn.Click += (_, _) => BrowseInto(FileABox);
        BrowseBBtn.Click += (_, _) => BrowseInto(FileBBox);
        ClearBBtn.Click += (_, _) => { FileBBox.Text = ""; };
        ComputeBtn.Click += async (_, _) => await ComputeAsync();
        CancelBtn.Click += (_, _) => _cts?.Cancel();
        CopyHashBtn.Click += (_, _) => CopyHash();
        VerifyBtn.Click += (_, _) => VerifyExpected();
        CloseBtn.Click += (_, _) => Close();

        // 拖拽文件进来（第一个拖进来的填 A，第二个填 B）
        AllowDrop = true;
        Drop += OnDrop;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files is null || files.Length == 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(FileABox.Text))
            {
                FileABox.Text = files[0];
            }

            if (files.Length > 1 && string.IsNullOrWhiteSpace(FileBBox.Text))
            {
                FileBBox.Text = files[1];
            }
            else if (files.Length == 1 && !string.IsNullOrWhiteSpace(FileABox.Text))
            {
                FileBBox.Text = files[0];
            }
        }
        catch (Exception ex)
        {
            Log.Exception("拖拽文件失败", ex);
        }
    }

    private void BrowseInto(System.Windows.Controls.TextBox target)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择文件",
                Filter = "所有文件|*.*",
                CheckFileExists = true,
            };

            var current = target.Text.Trim();
            if (File.Exists(current))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(current) ?? "";
            }

            if (dlg.ShowDialog(this) == true)
            {
                target.Text = dlg.FileName;
            }
        }
        catch (Exception ex)
        {
            Log.Exception("选择文件失败", ex);
        }
    }

    // ---------------------------------------------------------------- 计算

    private async Task ComputeAsync()
    {
        // 置忙必须在第一个 await 之前（见 DECISIONS 坑 19）
        if (_running)
        {
            return;
        }

        var pathA = FileABox.Text.Trim();
        var pathB = FileBBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(pathA) || !File.Exists(pathA))
        {
            SetConclusion("请先选择文件 A（文件不存在或还没填）。", Red);
            return;
        }

        _running = true;
        ComputeBtn.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        Progress.Value = 0;

        _cts = new CancellationTokenSource();

        try
        {
            var kind = SelectedKind();
            _ctx.Settings.LastHashAlgorithm = HashService.ToDisplayName(kind);
            _ctx.SaveSettings();

            var progress = new Progress<double>(p => Progress.Value = p);

            // 大文件先提醒一下 —— 用户看到进度条不动会以为卡死
            long sizeA = 0;
            try { sizeA = new FileInfo(pathA).Length; } catch { }

            if (sizeA > HashService.LargeFileThreshold)
            {
                HashInfoText.Text = $"文件较大（{HashService.FormatSize(sizeA)}），正在分块计算…";
            }

            // ---- 两个文件：比对模式 ----
            if (!string.IsNullOrWhiteSpace(pathB))
            {
                if (!File.Exists(pathB))
                {
                    SetConclusion("文件 B 不存在。", Red);
                    return;
                }

                HashInfoText.Text = "正在比较两个文件…";

                var cmp = await HashService.CompareAsync(pathA, pathB, kind, _cts.Token, progress);

                if (!cmp.Ok)
                {
                    SetConclusion(cmp.Message, Red);
                    HashBox.Text = "";
                    return;
                }

                // 结果框显示 A 的哈希
                HashBox.Text = cmp.HashA;
                HashInfoText.Text =
                    $"A：{Path.GetFileName(pathA)}（{HashService.FormatSize(cmp.SizeA)}）\n"
                    + $"B：{Path.GetFileName(pathB)}（{HashService.FormatSize(cmp.SizeB)}）";

                SetConclusion(
                    (cmp.Same == true ? "✅ 一致 —— " : "❌ 不一致 —— ") + cmp.Message,
                    cmp.Same == true ? Green : Red);

                _ctx.Notify("哈希校验", cmp.Same == true ? "两个文件内容相同。" : "两个文件内容不同。");
                return;
            }

            // ---- 单文件 ----
            HashInfoText.Text = "正在计算…";

            var result = await HashService.ComputeAsync(pathA, kind, _cts.Token, progress);

            if (!result.Success)
            {
                SetConclusion(result.Error ?? "计算失败。", Red);
                HashBox.Text = "";
                return;
            }

            HashBox.Text = result.Hash;
            HashInfoText.Text =
                $"{Path.GetFileName(pathA)}　{HashService.FormatSize(result.Bytes)}　"
                + $"{result.Algorithm}　耗时 {result.ElapsedMs} ms";

            SetConclusion(
                $"计算完成：{result.Algorithm}，{result.Bytes} 字节。可以点「复制」，"
                + "或把下载站给的校验值粘到下面比对。",
                Grey);

            _ctx.Notify("哈希校验", $"{result.Algorithm} 计算完成。");
        }
        catch (Exception ex)
        {
            Log.Exception("哈希计算流程失败", ex);
            SetConclusion($"出错了：{ex.Message}", Red);
        }
        finally
        {
            _running = false;
            ComputeBtn.IsEnabled = true;
            CancelBtn.IsEnabled = false;
            Progress.Value = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private HashAlgorithmKind SelectedKind() => AlgoCombo.SelectedIndex switch
    {
        1 => HashAlgorithmKind.MD5,
        2 => HashAlgorithmKind.SHA1,
        3 => HashAlgorithmKind.SHA512,
        _ => HashAlgorithmKind.SHA256,
    };

    // ---------------------------------------------------------------- 复制 / 比对

    private void CopyHash()
    {
        var text = HashBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // 走 ClipboardWriter（带自污染标记）⇒ 不进剪贴板历史
        if (ClipboardWriter.SetText(text, out var err))
        {
            _ctx.Notify("哈希校验", "哈希值已复制到剪贴板。");
        }
        else
        {
            SetConclusion($"复制失败：{err}", Red);
        }
    }

    private void VerifyExpected()
    {
        var actual = HashBox.Text.Trim();
        if (actual.Length == 0)
        {
            SetConclusion("还没有计算结果，先点「计算」。", Red);
            return;
        }

        var (match, message) = HashService.Verify(actual, ExpectedBox.Text);

        SetConclusion((match ? "✅ " : "❌ ") + message, match ? Green : Red);
    }

    private void SetConclusion(string text, Brush color)
    {
        ConclusionText.Text = text;
        ConclusionText.Foreground = color;
    }
}
