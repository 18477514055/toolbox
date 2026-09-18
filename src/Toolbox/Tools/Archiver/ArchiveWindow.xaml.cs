using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Toolbox.Core;
using Toolbox.Shell;
using File = System.IO.File;
using Directory = System.IO.Directory;
using Path = System.IO.Path;

namespace Toolbox.Tools.Archiver;

/// <summary>
/// 批量打包窗口。
///
/// 交互要点：
///   · 可以直接把文件/文件夹**拖进来**（最顺手）；
///   · 也可以「从剪贴板载入」—— 用户在资源管理器里框选一批文件 Ctrl+C 之后，
///     到这儿点一下就行（这正是用户描述的"批量选择文件之后"那个流程）；
///   · 格式与输出位置都是**选项**（用户明确要求）。
/// </summary>
internal sealed partial class ArchiveWindow : Window
{
    private readonly ToolboxContext _ctx;
    private readonly ObservableCollection<string> _sources = new();
    private bool _ready;
    private bool _running;
    private string _lastOutput = "";

    public ArchiveWindow(ToolboxContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        SourceList.ItemsSource = _sources;

        AddFilesBtn.Click += (_, _) => AddFiles();
        AddFolderBtn.Click += (_, _) => AddFolder();
        FromClipboardBtn.Click += (_, _) => LoadFromClipboard();
        RemoveBtn.Click += (_, _) => RemoveSelected();
        ClearBtn.Click += (_, _) => { _sources.Clear(); UpdateStatus(); };
        BrowseDirBtn.Click += (_, _) => BrowseOutputDir();
        SaveDirBtn.Click += (_, _) => SaveDefaultDir();
        PackBtn.Click += async (_, _) => await PackAsync();
        OpenResultBtn.Click += (_, _) => OpenResult();
        CloseBtn.Click += (_, _) => Close();

        // 拖拽支持
        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        // 恢复设置里记的输出目录
        var saved = _ctx.Settings.ArchiveOutputDir;
        if (!string.IsNullOrWhiteSpace(saved))
        {
            OutputDirBox.Text = saved!;
        }

        _ready = true;
        OnFormatChanged(null!, null!);
        UpdateStatus();
    }

    // ---------------------------------------------------------------- 添加来源

    private void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (paths is null)
            {
                return;
            }

            AddSources(paths);
        }
        catch (Exception ex)
        {
            Log.Exception("拖拽文件失败", ex);
        }
    }

    private void AddFiles()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要打包的文件",
                Filter = "所有文件|*.*",
                Multiselect = true,
                CheckFileExists = true,
            };

            if (dlg.ShowDialog(this) == true)
            {
                AddSources(dlg.FileNames);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("选择文件失败", ex);
        }
    }

    private void AddFolder()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择要打包的文件夹",
            };

            if (dlg.ShowDialog(this) == true)
            {
                AddSources(new[] { dlg.FolderName });
            }
        }
        catch (Exception ex)
        {
            Log.Exception("选择文件夹失败", ex);
        }
    }

    /// <summary>
    /// 从剪贴板载入 —— 这是用户描述的流程："批量选择文件之后点按钮"。
    ///
    /// ⚠️ 这里**只读**剪贴板，不写、不改动（符合"剪贴板是通道"那条硬约束）。
    /// </summary>
    private void LoadFromClipboard()
    {
        try
        {
            var files = System.Windows.Clipboard.GetFileDropList();

            if (files.Count == 0)
            {
                SetStatus("剪贴板里没有文件。\n"
                          + "先在资源管理器里框选文件、Ctrl+C，再回来点这个按钮。", isError: true);
                return;
            }

            var paths = new List<string>();
            foreach (string? f in files)
            {
                if (!string.IsNullOrWhiteSpace(f))
                {
                    paths.Add(f);
                }
            }

            AddSources(paths);
        }
        catch (Exception ex)
        {
            Log.Exception("从剪贴板载入失败", ex);
            SetStatus($"读剪贴板失败：{ex.Message}", isError: true);
        }
    }

    private void AddSources(IEnumerable<string> paths)
    {
        var added = 0;

        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p))
            {
                continue;
            }

            // 去重（大小写不敏感 —— Windows 路径就是大小写不敏感的）
            if (_sources.Any(s => string.Equals(s, p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _sources.Add(p);
            added++;
        }

        UpdateStatus(added);
    }

    private void RemoveSelected()
    {
        var selected = SourceList.SelectedItems.Cast<string>().ToList();

        foreach (var s in selected)
        {
            _sources.Remove(s);
        }

        UpdateStatus();
    }

    private void UpdateStatus(int justAdded = 0)
    {
        var prefix = justAdded > 0 ? $"已添加 {justAdded} 项。" : "";

        if (_sources.Count == 0)
        {
            SetStatus(prefix + "添加要打包的文件，然后点「开始打包」。", isError: false);
            return;
        }

        // 算一下总大小，让用户对"要压多久"有个预期
        long total = 0;
        var dirCount = 0;

        foreach (var s in _sources)
        {
            try
            {
                if (Directory.Exists(s))
                {
                    dirCount++;
                    total += new DirectoryInfo(s).EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(f => f.Length);
                }
                else if (File.Exists(s))
                {
                    total += new FileInfo(s).Length;
                }
            }
            catch
            {
                // 算不出来就不算，不影响主流程
            }
        }

        var sizeText = total > 0 ? $"，共 {ArchiveService.FormatSize(total)}" : "";
        var dirText = dirCount > 0 ? $"，含 {dirCount} 个文件夹" : "";

        SetStatus($"{prefix}待打包 {_sources.Count} 项{dirText}{sizeText}。", isError: false);
    }

    // ---------------------------------------------------------------- 选项

    private ArchiveFormat SelectedFormat()
        => RarRadio.IsChecked == true ? ArchiveFormat.Rar : ArchiveFormat.Zip;

    private ArchiveDestination SelectedDestination()
        => ConfiguredFolderRadio.IsChecked == true
            ? ArchiveDestination.ConfiguredFolder
            : ArchiveDestination.SameFolder;

    private void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var format = SelectedFormat();

        // 格式能不能用，当场就说清楚（别等到点打包才报错）
        if (ArchiveService.IsAvailable(format, out var reason))
        {
            var rar = format == ArchiveFormat.Rar ? ArchiveService.FindRar() : null;

            FormatHint.Text = format == ArchiveFormat.Zip
                ? "ZIP 用系统自带能力，不需要装任何东西。"
                : $"RAR 会用你装的 WinRAR 来建：\n{rar}";
            FormatHint.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x8A, 0x94, 0xA6));
        }
        else
        {
            // 不可用：**在选项旁边就把原因写清楚**，而不是等用户点了才弹错误
            FormatHint.Text = "⚠ " + (reason ?? "这种格式现在不可用。");
            FormatHint.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xD9, 0x7A, 0x06));
        }
    }

    private void OnDestinationChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var useConfigured = SelectedDestination() == ArchiveDestination.ConfiguredFolder;
        OutputDirBox.IsEnabled = useConfigured;
        BrowseDirBtn.IsEnabled = useConfigured;
        SaveDirBtn.IsEnabled = useConfigured;
    }

    private void BrowseOutputDir()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择压缩包存放目录",
                InitialDirectory = Directory.Exists(OutputDirBox.Text.Trim()) ? OutputDirBox.Text.Trim() : "",
            };

            if (dlg.ShowDialog(this) == true)
            {
                OutputDirBox.Text = dlg.FolderName;
            }
        }
        catch (Exception ex)
        {
            Log.Exception("选择输出目录失败", ex);
        }
    }

    private void SaveDefaultDir()
    {
        var dir = OutputDirBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(dir))
        {
            SetStatus("目录是空的，没有保存。", isError: true);
            return;
        }

        _ctx.Settings.ArchiveOutputDir = dir;
        _ctx.SaveSettings();
        SetStatus($"已记下默认输出目录：{dir}", isError: false);
    }

    // ---------------------------------------------------------------- 打包

    private async Task PackAsync()
    {
        if (_running)
        {
            return;
        }

        if (_sources.Count == 0)
        {
            SetStatus("还没有添加要打包的东西。", isError: true);
            return;
        }

        var format = SelectedFormat();
        var destination = SelectedDestination();

        if (!ArchiveService.IsAvailable(format, out var reason))
        {
            MessageBox.Show(this, reason ?? "这种格式不可用。", "批量打包",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 用设置里的目录时，目录必须有效（否则会静默落到别处，用户找不到产物）
        string? configuredDir = null;
        if (destination == ArchiveDestination.ConfiguredFolder)
        {
            configuredDir = OutputDirBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(configuredDir))
            {
                SetStatus("选了「放到设置里指定的目录」，但目录是空的。先选一个目录。", isError: true);
                return;
            }

            try
            {
                Directory.CreateDirectory(configuredDir);
            }
            catch (Exception ex)
            {
                SetStatus($"这个目录不可用：{ex.Message}", isError: true);
                return;
            }
        }

        _running = true;
        PackBtn.IsEnabled = false;
        OpenResultBtn.IsEnabled = false;
        SetStatus("正在打包…", isError: false);

        try
        {
            var output = ArchiveService.BuildOutputPath(
                _sources.ToList(), format, destination, configuredDir, DateTime.Now);

            var result = await ArchiveService.CreateAsync(
                _sources.ToList(), format, output, CancellationToken.None);

            if (!result.Success)
            {
                SetStatus(result.Error ?? "打包失败。", isError: true);
                _ctx.Notify("批量打包", "打包失败。");
                return;
            }

            _lastOutput = result.OutputPath;
            OpenResultBtn.IsEnabled = true;

            SetStatus(
                $"打包完成：{Path.GetFileName(result.OutputPath)}\n"
                + $"{ArchiveService.FormatSize(result.Bytes)}　耗时 {result.ElapsedMs} ms\n"
                + $"{result.OutputPath}",
                isError: false);

            _ctx.Notify("批量打包",
                $"已生成 {Path.GetFileName(result.OutputPath)}（{ArchiveService.FormatSize(result.Bytes)}）");

            Log.Line($"批量打包成功：{result.OutputPath}");
        }
        catch (Exception ex)
        {
            Log.Exception("打包流程失败", ex);
            SetStatus($"出错：{ex.Message}", isError: true);
        }
        finally
        {
            _running = false;
            PackBtn.IsEnabled = true;
        }
    }

    private void OpenResult()
    {
        if (string.IsNullOrWhiteSpace(_lastOutput) || !File.Exists(_lastOutput))
        {
            return;
        }

        try
        {
            // /select 直接在资源管理器里选中那个压缩包
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"/select,\"{_lastOutput}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Exception("打开压缩包位置失败", ex);
        }
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(isError
            ? System.Windows.Media.Color.FromRgb(0xE0, 0x3B, 0x3B)
            : System.Windows.Media.Color.FromRgb(0x1F, 0x23, 0x29));
    }
}
