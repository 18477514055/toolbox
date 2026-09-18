using System.Windows;
using System.Windows.Controls;
using Toolbox.Core;
using Toolbox.Shell;
using File = System.IO.File;
using Directory = System.IO.Directory;
using Path = System.IO.Path;

namespace Toolbox.Tools.BatchRename;

/// <summary>
/// 批量重命名窗口。
///
/// 安全设计（这个工具是**不可逆操作**，必须比别的工具更小心）：
///   1. **先预览再执行** —— 规则一改就重算预览，执行时用的就是预览里那份计划
///      （预览与执行共用 <see cref="RenamePlanner.Plan"/>，不可能不一致）；
///   2. **执行前二次确认**，并把"将改动 N 个文件"写进确认框；
///   3. **执行后落账本**，支持一键撤销（账本落盘，重启后仍可撤）；
///   4. 重名默认**自动加序号**，不覆盖任何已有文件。
/// </summary>
internal sealed partial class BatchRenameWindow : Window
{
    private readonly ToolboxContext _ctx;
    private readonly List<string> _files = new();
    private List<RenamePlanItem> _plan = new();

    /// <summary>防止"改规则 → 重算"在初始化期触发空引用（见 DECISIONS 坑 23）。</summary>
    private bool _ready;

    public BatchRenameWindow(ToolboxContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        BrowseDirBtn.Click += (_, _) => BrowseDirectory();
        LoadDirBtn.Click += (_, _) => LoadDirectory(DirBox.Text.Trim());
        ApplyBtn.Click += (_, _) => Apply();
        UndoBtn.Click += (_, _) => Undo();
        CloseBtn.Click += (_, _) => Close();

        // 规则控件一改就重算预览 —— 让用户"边调边看"，而不是填完一堆字段再猜
        foreach (var box in new[] { PrefixBox, SuffixBox, FindBox, ReplaceBox,
                                    SeqStartBox, SeqPadBox, SeqStepBox, TsFormatBox })
        {
            box.TextChanged += (_, _) => RefreshPlan();
        }

        SeqBox.Checked += (_, _) => RefreshPlan();
        SeqBox.Unchecked += (_, _) => RefreshPlan();
        TsBox.Checked += (_, _) => RefreshPlan();
        TsBox.Unchecked += (_, _) => RefreshPlan();
        MatchCaseBox.Checked += (_, _) => RefreshPlan();
        MatchCaseBox.Unchecked += (_, _) => RefreshPlan();
        LowerExtBox.Checked += (_, _) => RefreshPlan();
        LowerExtBox.Unchecked += (_, _) => RefreshPlan();
        TsSourceCombo.SelectionChanged += (_, _) => RefreshPlan();

        // 预置上次用的目录（Settings 已有 LastConvertDir 这类先例，这里加一个同款字段用途）
        var last = _ctx.Settings.LastRenameDir;
        if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last))
        {
            DirBox.Text = last;
            LoadDirectory(last);
        }
        else
        {
            SummaryText.Text = "选择一个目录开始。";
        }

        _ready = true;
        UpdateUndoButton();
    }

    // ---------------------------------------------------------------- 目录

    private void BrowseDirectory()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择要批量改名的文件夹",
                InitialDirectory = Directory.Exists(DirBox.Text.Trim()) ? DirBox.Text.Trim() : "",
            };

            if (dlg.ShowDialog(this) == true)
            {
                DirBox.Text = dlg.FolderName;
                LoadDirectory(dlg.FolderName);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("选择目录失败", ex);
        }
    }

    private void LoadDirectory(string dir)
    {
        _files.Clear();

        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            SummaryText.Text = "目录不存在。";
            PreviewGrid.ItemsSource = null;
            return;
        }

        try
        {
            // 只取文件，**不递归子目录** —— 递归改名波及面太大，太容易误伤
            _files.AddRange(Directory.EnumerateFiles(dir)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));

            _ctx.Settings.LastRenameDir = dir;
            _ctx.SaveSettings();

            if (_files.Count == 0)
            {
                SummaryText.Text = "这个目录里没有文件。";
                PreviewGrid.ItemsSource = null;
                return;
            }

            SummaryText.Text = $"载入 {_files.Count} 个文件。";
            RefreshPlan();
        }
        catch (Exception ex)
        {
            Log.Exception($"枚举目录失败：{dir}", ex);
            SummaryText.Text = $"读目录失败：{ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 预览

    private RenameRule BuildRule()
    {
        static int ParseInt(TextBox box, int fallback)
            => int.TryParse(box.Text.Trim(), out var v) ? v : fallback;

        return new RenameRule
        {
            Prefix = PrefixBox.Text,
            Suffix = SuffixBox.Text,
            FindText = FindBox.Text,
            ReplaceText = ReplaceBox.Text,
            MatchCase = MatchCaseBox.IsChecked == true,
            UseSequence = SeqBox.IsChecked == true,
            SequenceStart = ParseInt(SeqStartBox, 1),
            SequencePadding = ParseInt(SeqPadBox, 3),
            SequenceStep = ParseInt(SeqStepBox, 1),
            UseTimestamp = TsBox.IsChecked == true,
            TimestampFormat = string.IsNullOrWhiteSpace(TsFormatBox.Text) ? "yyyyMMdd" : TsFormatBox.Text.Trim(),
            TimestampUseFileTime = TsSourceCombo.SelectedIndex == 0,
            LowercaseExtension = LowerExtBox.IsChecked == true,
        };
    }

    private void RefreshPlan()
    {
        if (!_ready)
        {
            return;
        }

        if (_files.Count == 0)
        {
            return;
        }

        var rule = BuildRule();
        var now = DateTime.Now;

        _plan = RenamePlanner.Plan(
            _files,
            rule,
            now,
            fileTime: p =>
            {
                try { return File.GetLastWriteTime(p); }
                catch { return now; }
            },
            exists: File.Exists);

        PreviewGrid.ItemsSource = _plan;

        var willChange = _plan.Count(p => p.WillApply && p.Changes);
        var skipped = _plan.Count(p => !p.WillApply);
        var unchanged = _plan.Count(p => p.WillApply && !p.Changes);

        var parts = new List<string> { $"将改动 {willChange} 个文件" };
        if (unchanged > 0) { parts.Add($"{unchanged} 个名字没变"); }
        if (skipped > 0) { parts.Add($"{skipped} 个被跳过"); }

        SummaryText.Text = string.Join("，", parts) + "。";
        ApplyBtn.IsEnabled = willChange > 0;
    }

    // ---------------------------------------------------------------- 执行

    private void Apply()
    {
        var changes = _plan.Where(p => p.WillApply && p.Changes).ToList();
        if (changes.Count == 0)
        {
            return;
        }

        // 二次确认：这是不可逆操作，且一点就是几十个文件
        var confirm = MessageBox.Show(this,
            $"即将重命名 {changes.Count} 个文件。\n\n"
            + "重名时会自动加序号，不会覆盖已有文件。\n"
            + "执行后可以用「撤销上次改名」还原。\n\n确定继续吗？",
            "批量文件重命名",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var journal = new RenameJournal
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Directory = _files.Count > 0 ? (Path.GetDirectoryName(_files[0]) ?? "") : "",
        };

        var done = 0;
        var failures = new List<string>();

        // ⚠️ 两阶段改名（先改成临时名，再改成目标名）。
        //
        // 为什么必须两阶段：正序直接改会撞车。例如把 a→b、b→a（交换两个文件名）时，
        //   第一步 a→b 会因为 b 已存在而失败。
        //   先全部改成不冲突的临时名，再全部改成目标名，就与顺序无关了。
        var staged = new List<(string Temp, string Target, string Original)>();

        try
        {
            foreach (var item in changes)
            {
                var temp = item.SourcePath + $".renaming-{Guid.NewGuid():N}.tmp";

                try
                {
                    File.Move(item.SourcePath, temp);
                    staged.Add((temp, item.TargetPath, item.SourcePath));
                }
                catch (Exception ex)
                {
                    failures.Add($"{item.SourceName}：{ex.Message}");
                }
            }

            // 第二阶段：临时名 → 目标名
            foreach (var (temp, target, original) in staged)
            {
                try
                {
                    File.Move(temp, target);
                    journal.Moves.Add(new RenameMove { From = original, To = target });
                    done++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(target)}：{ex.Message}");

                    // 改不回去就尽量还原成原名，别把文件卡在 .tmp 状态
                    try { File.Move(temp, original); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Exception("批量改名失败", ex);
            failures.Add(ex.Message);
        }

        if (journal.Count > 0)
        {
            RenameJournalStore.Save(journal);
            Log.Line($"批量改名完成：{done} 个（目录 {journal.Directory}）");
        }

        var msg = $"已重命名 {done} 个文件。";
        if (failures.Count > 0)
        {
            msg += $"\n\n有 {failures.Count} 个失败：\n" + string.Join("\n", failures.Take(5));
        }

        MessageBox.Show(this, msg, "批量文件重命名",
            MessageBoxButton.OK,
            failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        _ctx.Notify("批量重命名", $"已重命名 {done} 个文件。");

        // 重新载入（文件名变了）
        LoadDirectory(DirBox.Text.Trim());
        UpdateUndoButton();
    }

    // ---------------------------------------------------------------- 撤销

    private void Undo()
    {
        var journal = RenameJournalStore.Load();
        if (journal is null || journal.Count == 0)
        {
            MessageBox.Show(this, "没有可撤销的改名记录。", "批量文件重命名",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"将撤销 {journal.Time} 对 {journal.Count} 个文件的改名。\n\n"
            + $"目录：{journal.Directory}\n\n"
            + "若目标文件已被移走或删除，那一条会被跳过（不会覆盖任何现有文件）。\n\n确定撤销吗？",
            "撤销上次改名",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var (undone, message) = RenameJournalStore.Undo(journal);

        // 全撤成功才清账本；部分失败则保留，让用户能再试一次
        if (undone == journal.Count)
        {
            RenameJournalStore.Clear();
        }

        MessageBox.Show(this, message, "撤销上次改名",
            MessageBoxButton.OK, MessageBoxImage.Information);

        if (!string.IsNullOrWhiteSpace(journal.Directory) && Directory.Exists(journal.Directory))
        {
            LoadDirectory(journal.Directory);
        }

        UpdateUndoButton();
    }

    private void UpdateUndoButton()
    {
        var journal = RenameJournalStore.Load();
        var has = journal is { Count: > 0 };

        UndoBtn.IsEnabled = has;
        HintText.Text = has
            ? $"可撤销：{journal!.Time} 的 {journal.Count} 个文件。"
            : "先看预览里的「新名」和「说明」，确认无误再执行。";
    }
}
