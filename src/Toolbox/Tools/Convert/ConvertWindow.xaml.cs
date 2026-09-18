using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Toolbox.Core;
using Toolbox.Shell;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace Toolbox.Tools.Convert;

/// <summary>
/// 格式转换窗口。核心是「可插拔后端」：图片走内置编解码器，
/// 文档走 LibreOffice，PDF→Word 走 Office，谁不行就换另一条，不把用户堵死。
/// </summary>
internal sealed partial class ConvertWindow : Window
{
    private readonly ToolboxContext _ctx;
    private readonly ObservableCollection<ConvertJob> _jobs = new();

    private readonly ImageConverter _imageConverter = new();
    private readonly LibreOfficeConverter _libreOffice;
    private readonly OfficeComConverter _officeConverter = new();

    private CancellationTokenSource? _cts;
    private bool _running;

    public ConvertWindow(ToolboxContext ctx)
    {
        _ctx = ctx;
        _libreOffice = new LibreOfficeConverter(() => SofficeLocator.Find(_ctx.Settings.LibreOfficePath));

        InitializeComponent();

        Jobs.ItemsSource = _jobs;

        AddFilesBtn.Click += (_, _) => AddFiles();
        AddFolderBtn.Click += (_, _) => AddFolder();
        ClearBtn.Click += (_, _) => { _jobs.Clear(); UpdateSummary(); };
        PickOutputBtn.Click += (_, _) => PickOutputDir();
        OpenOutputBtn.Click += (_, _) => OpenPath(OutputDirBox.Text);
        OpenResultBtn.Click += (_, _) => OpenPath(OutputDirBox.Text);
        StartBtn.Click += async (_, _) => await StartAsync();
        CancelBtn.Click += (_, _) => _cts?.Cancel();

        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        };

        Loaded += (_, _) => RefreshBackendInfo();

        OutputDirBox.Text = _ctx.Settings.LastConvertDir
                            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "转换输出");
    }

    // ---------------------------------------------------------------- 后端状态

    private void RefreshBackendInfo()
    {
        var libreOk = _libreOffice.IsAvailable(out var libreReason);
        var officeOk = _officeConverter.IsAvailable(out var officeReason);

        var lines = new List<string>
        {
            "图片互转：内置（零依赖，最快）",
            libreOk
                ? "文档转换：LibreOffice 已就绪"
                : "文档转换：不可用 —— " + libreReason,
            officeOk
                ? "PDF → Word：Office 已就绪"
                : "PDF → Word：不可用 —— " + officeReason,
        };

        BackendInfo.Text = string.Join("　|　", lines);
    }

    private IConverter? PickConverter(ConvertJob job)
    {
        if (_imageConverter.CanConvert(job.SourcePath, job.SelectedTarget))
        {
            return _imageConverter;
        }

        if (_libreOffice.CanConvert(job.SourcePath, job.SelectedTarget))
        {
            return _libreOffice;
        }

        if (_officeConverter.CanConvert(job.SourcePath, job.SelectedTarget))
        {
            return _officeConverter;
        }

        return null;
    }

    // ---------------------------------------------------------------- 加文件

    private void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要转换的文件",
            Multiselect = true,
            Filter = "支持的文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.pdf;*.doc;*.docx;*.odt;*.rtf;*.txt;*.html;*.htm;*.xls;*.xlsx;*.ods;*.csv;*.ppt;*.pptx;*.odp"
                     + "|图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff"
                     + "|文档 / 表格 / 演示|*.pdf;*.doc;*.docx;*.odt;*.rtf;*.txt;*.html;*.xls;*.xlsx;*.ods;*.csv;*.ppt;*.pptx;*.odp"
                     + "|所有文件|*.*",
            InitialDirectory = _ctx.Settings.LastConvertDir ?? "",
        };

        if (dlg.ShowDialog(this) == true)
        {
            AddPaths(dlg.FileNames);
        }
    }

    private void AddFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择文件夹（只加入这一层里的文件）",
        };

        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var files = Directory.GetFiles(dlg.FolderName)
                .Where(Formats.IsSupported)
                .ToArray();

            AddPaths(files);

            if (files.Length == 0)
            {
                DetailText.Text = "这个文件夹里没有可转换的文件（只扫描了第一层，不会递归）。";
            }
        }
        catch (Exception ex)
        {
            Log.Exception("扫描文件夹失败", ex);
            DetailText.Text = $"扫描文件夹失败：{ex.Message}";
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] items)
            {
                return;
            }

            var files = new List<string>();
            foreach (var item in items)
            {
                if (Directory.Exists(item))
                {
                    files.AddRange(Directory.GetFiles(item).Where(Formats.IsSupported));
                }
                else if (File.Exists(item))
                {
                    files.Add(item);
                }
            }

            AddPaths(files);
        }
        catch (Exception ex)
        {
            Log.Exception("拖拽添加文件失败", ex);
        }
    }

    /// <summary>
    /// 视频 / 音频扩展名。
    ///
    /// 它们和"格式不支持"不是一回事：不是用户拖错了，而是**本版明确不做**。
    /// 所以提示必须分开说，不能混在"跳过 N 个不支持的类型"里一笔带过 ——
    /// 那样用户会以为程序没反应，或者以为是自己拖错了文件。
    /// </summary>
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg",
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma",
    };

    private void AddPaths(IEnumerable<string> paths)
    {
        var existing = _jobs.Select(j => j.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var added = 0;
        var mediaRejected = new List<string>();

        foreach (var path in paths)
        {
            if (!Formats.IsSupported(path))
            {
                skipped++;

                if (MediaExtensions.Contains(Path.GetExtension(path)))
                {
                    mediaRejected.Add(Path.GetFileName(path));
                }

                continue;
            }

            if (!existing.Add(path))
            {
                continue;
            }

            _jobs.Add(new ConvertJob(path));
            added++;
        }

        if (added > 0)
        {
            var first = _jobs.FirstOrDefault();
            if (first is not null && string.IsNullOrWhiteSpace(OutputDirBox.Text))
            {
                OutputDirBox.Text = Path.Combine(first.Folder, "转换输出");
            }

            _ctx.Settings.LastConvertDir = Path.GetDirectoryName(_jobs[0].SourcePath);
            _ctx.SaveSettings();
        }

        // 汇总：把"媒体文件被明确拒绝"单独说出来，而不是只报一个数字
        var parts = new List<string>();
        if (added > 0)
        {
            parts.Add($"已加入 {added} 个文件");
        }

        if (mediaRejected.Count > 0)
        {
            var names = string.Join("、", mediaRejected.Take(3));
            var more = mediaRejected.Count > 3 ? $" 等 {mediaRejected.Count} 个" : "";
            parts.Add($"拒绝 {mediaRejected.Count} 个视频/音频（{names}{more}）"
                      + " —— 本版不做音视频转换（需要 ffmpeg，不在工具箱的依赖范围内）");
        }

        var otherSkipped = skipped - mediaRejected.Count;
        if (otherSkipped > 0)
        {
            parts.Add($"跳过 {otherSkipped} 个不支持的类型");
        }

        DetailText.Text = parts.Count > 0 ? string.Join("；", parts) + "。" : "没有可加入的文件。";

        UpdateSummary();
    }

    private void PickOutputDir()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择输出目录" };
        if (dlg.ShowDialog(this) == true)
        {
            OutputDirBox.Text = dlg.FolderName;
        }
    }

    // ---------------------------------------------------------------- 开始

    private async Task StartAsync()
    {
        if (_running)
        {
            return;
        }

        if (_jobs.Count == 0)
        {
            DetailText.Text = "先加几个文件进来。";
            return;
        }

        var outputDir = OutputDirBox.Text.Trim();
        if (string.IsNullOrEmpty(outputDir))
        {
            DetailText.Text = "还没指定输出目录。";
            return;
        }

        try
        {
            AppPaths.Ensure(outputDir);
        }
        catch (Exception ex)
        {
            DetailText.Text = $"输出目录用不了：{ex.Message}";
            return;
        }

        // 转换前预检：依赖不可用要**当场**说清楚，而不是转半天报个看不懂的错
        var blockers = new List<string>();
        foreach (var job in _jobs)
        {
            var converter = PickConverter(job);
            if (converter is null)
            {
                job.Status = "不支持这种转换";
                continue;
            }

            if (!converter.IsAvailable(out var reason))
            {
                job.Status = "依赖缺失";
                blockers.Add($"· {job.FileName} → {job.SelectedTarget}：{reason}");
            }
            else
            {
                job.Status = "等待";
            }
        }

        if (blockers.Count > 0)
        {
            MessageBox.Show(this,
                "下面这些任务缺少必要的依赖，无法转换：\n\n"
                + string.Join("\n", blockers.Distinct().Take(8))
                + (blockers.Count > 8 ? $"\n… 还有 {blockers.Count - 8} 条" : ""),
                "格式转换 · 依赖缺失",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _ctx.Settings.LastConvertDir = outputDir;
        _ctx.Settings.LastConvertFormat = _jobs[0].SelectedTarget;
        _ctx.SaveSettings();

        _running = true;
        _cts = new CancellationTokenSource();
        StartBtn.IsEnabled = false;
        CancelBtn.IsEnabled = true;

        var ok = 0;
        var failed = 0;
        var index = 0;

        try
        {
            foreach (var job in _jobs.ToList())
            {
                if (_cts.IsCancellationRequested)
                {
                    job.Status = "已取消";
                    continue;
                }

                index++;
                var converter = PickConverter(job);
                if (converter is null)
                {
                    job.Status = "不支持这种转换";
                    failed++;
                    continue;
                }

                if (!converter.IsAvailable(out var reason))
                {
                    job.Status = "依赖缺失";
                    job.OutputPath = null;
                    failed++;
                    DetailText.Text = reason;
                    continue;
                }

                job.IsRunning = true;
                job.Status = $"转换中…（{index}/{_jobs.Count}）";
                SummaryText.Text = $"正在转换 {index} / {_jobs.Count}：{job.FileName}";

                try
                {
                    var result = await converter.ConvertAsync(
                        job.SourcePath, job.SelectedTarget, outputDir, _cts.Token);

                    if (result.Success)
                    {
                        ok++;
                        job.Status = $"完成 · {result.Message}";
                        job.OutputPath = result.OutputPath;
                    }
                    else
                    {
                        failed++;
                        job.Status = result.Message.Replace("\n", " ");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    job.Status = $"失败：{ex.Message}";
                    Log.Exception($"转换任务异常：{job.SourcePath}", ex);
                }
                finally
                {
                    job.IsRunning = false;
                }
            }
        }
        finally
        {
            _running = false;
            _cts?.Dispose();
            _cts = null;
            StartBtn.IsEnabled = true;
            CancelBtn.IsEnabled = false;

            SummaryText.Text = $"完成：成功 {ok} 个，失败 {failed} 个（共 {_jobs.Count} 个）。";
            DetailText.Text = failed == 0
                ? $"输出目录：{outputDir}"
                : "失败的条目在列表右侧有具体原因。";

            if (ok > 0)
            {
                _ctx.Notify("格式转换", $"转换完成：成功 {ok} 个，失败 {failed} 个。");
            }
        }
    }

    private void UpdateSummary()
    {
        SummaryText.Text = _jobs.Count == 0 ? "还没有任务。" : $"队列里 {_jobs.Count} 个文件。";
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            AppPaths.Ensure(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Exception($"打开目录失败：{path}", ex);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_running)
        {
            var answer = MessageBox.Show(this,
                "还有转换任务在跑，现在关掉会中断它。确定要关吗？",
                "格式转换", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _cts?.Cancel();
        }

        base.OnClosing(e);
    }
}
