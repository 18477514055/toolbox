using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using File = System.IO.File;
using Directory = System.IO.Directory;
using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.Clipboard;

/// <summary>列表里一行的显示数据。</summary>
internal sealed class ClipRow
{
    public required ClipEntry Entry { get; init; }
    public string Icon { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string TimeText { get; init; } = "";
    public string PinText { get; init; } = "";
    public BitmapSource? Thumb { get; init; }
    public Visibility IconVisibility { get; init; } = Visibility.Visible;
    public Visibility ThumbVisibility { get; init; } = Visibility.Collapsed;
}

/// <summary>
/// 快捷剪贴板的面板。
///
/// 与悬浮窗不同，**这个面板是要抢焦点的**——用户要在这里打字搜索。
/// 不抢焦点的是悬浮窗（AI 读页面依赖那一点）。
/// </summary>
internal sealed partial class ClipboardPanel : Window
{
    private readonly ToolboxContext _ctx;
    private readonly HistoryStore _store;
    private List<ClipRow> _rows = new();

    /// <summary>
    /// 标记窗口已完成首次加载。
    ///
    /// 为什么需要（这是一个真实存在过、且只在运行时才暴露的 NRE）：
    ///   XAML 里 DateFilter 这个 ComboBox 设了 SelectedIndex="0"，它**先于**列表 ListBox
    ///   完成初始化；InitializeComponent 设置 SelectedIndex 的瞬间会同步触发 SelectionChanged
    ///   → OnDateFilterChanged → RefreshList，而此时 List 还没被创建（null）→ 空引用异常。
    ///   这个异常被 RefreshList 的 try/catch 吃掉，所以界面不崩，但日志里会平白多一条
    ///   「刷新剪贴板列表失败」，而且那次初始化期的刷新是浪费。
    ///   用 _ready 拦住「窗口加载完成之前」的事件即可——用户真正改筛选框时窗口早已加载好。
    /// </summary>
    private bool _ready;

    public ClipboardPanel(ToolboxContext ctx, HistoryStore store)
    {
        _ctx = ctx;
        _store = store;

        InitializeComponent();

        // 窗口首次加载完成后再放开筛选框 / 搜索框的事件——见 _ready 字段上的说明。
        Loaded += (_, _) => _ready = true;

        SearchBox.TextChanged += (_, _) => RefreshList();

        List.KeyDown += OnListKeyDown;
        List.MouseDoubleClick += (_, _) => CopySelected();

        // ★ 右键 = 直接复制（用户要求："只需要右键剪贴板中的选项就可以将他复制"）
        //
        // 用 PreviewMouseRightButtonUp 而不是 MouseRightButtonUp：
        //   前者是**隧道**事件，在 ListBox 处理之前就到了我们手里，
        //   不会被列表项自身的处理吃掉（右键在某些控件上会被用来改变选中项）。
        //   而且此时选中项已经更新成右键点中的那一行（下面显式同步一次，双保险）。
        List.PreviewMouseRightButtonUp += OnListRightClick;

        // 右键菜单也留着 —— 它不是替代品，而是**补充**：
        //   右键直接复制是"快"，菜单里还给了置顶 / 删除 / 打开所在位置这几个常用动作。
        //   两者不冲突：右键松手就复制了，菜单是给"想干别的"的时候用的。
        BuildContextMenu();

        // 失焦自动收起（交接文档的通用交互模式）
        Deactivated += (_, _) => Hide();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        };
    }

    public void ShowPanel()
    {
        SearchBox.Text = "";
        RefreshList();

        if (!IsVisible)
        {
            Show();
        }

        Activate();
        SearchBox.Focus();

        // 布局完成后再定位，否则 GetWindowRect 拿到的是旧尺寸
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            ScreenPlacement.MoveWindowNearBottomRight(hwnd);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------------- 右键

    /// <summary>
    /// 右键直接复制。
    ///
    /// 行为细节（都是"用起来顺不顺"的关键）：
    ///   ① 右键点在哪一行，就先**把选中项切到那一行** —— 用户右键第 3 条却复制了第 1 条
    ///      会非常困惑（右键不像左键那样天然改变选中）；
    ///   ② 点到**空白处**（没命中任何行）时什么都不做，不要复制当前选中项 ——
    ///      那等于"我什么都没点，它却复制了东西"。
    /// </summary>
    private void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var row = FindRowUnder(e.OriginalSource as System.Windows.DependencyObject);
            if (row is null)
            {
                return;   // 点空白：不动作
            }

            // 同步选中项（见上面 ①）
            var item = FindListBoxItem(e.OriginalSource as System.Windows.DependencyObject);
            if (item is not null)
            {
                item.IsSelected = true;
                List.SelectedItem = item.DataContext;
            }

            e.Handled = true;
            CopySelected();
        }
        catch (Exception ex)
        {
            Log.Exception("右键复制失败", ex);
        }
    }

    /// <summary>从点击到的元素往上找，看它属于哪一行数据。</summary>
    private static ClipRow? FindRowUnder(System.Windows.DependencyObject? source)
    {
        var item = FindListBoxItem(source);
        return item?.DataContext as ClipRow;
    }

    /// <summary>
    /// 从点击到的元素往上找 ListBoxItem。
    ///
    /// ⚠️ 两个必须处理的现实情况（否则右键会"时灵时不灵"，甚至抛异常）：
    ///   ① <c>OriginalSource</c> 可能是**非 Visual** 的对象 ——
    ///      点在 TextBlock 的文字上时，命中的可能是 `Run`（ContentElement，
    ///      不在可视树里）。对它调 <c>VisualTreeHelper.GetParent</c> 会直接抛
    ///      `InvalidOperationException`，右键就"偶尔没反应"。
    ///      解法：先把它归一到 Visual / Visual3D，再往上走。
    ///   ② 走到顶（null）要能正常结束 —— 循环条件是 `is not null`。
    /// </summary>
    private static ListBoxItem? FindListBoxItem(System.Windows.DependencyObject? source)
    {
        var current = source;

        while (current is not null)
        {
            if (current is ListBoxItem item)
            {
                return item;
            }

            // ① 非 Visual 的对象先归一化（见上面的说明）
            if (current is not System.Windows.Media.Visual
                && current is not System.Windows.Media.Media3D.Visual3D)
            {
                current = LogicalTreeHelper.GetParent(current);
                continue;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// 右键菜单：置顶 / 删除 / 打开所在位置。
    ///
    /// 为什么不把"复制"放进来当第一项：右键**松手就已经复制了**，
    /// 菜单里再放一个"复制"会让用户以为"右键 → 复制"要两步。
    /// 菜单只放**右键不能直接做**的那几件事。
    /// </summary>
    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var pin = new MenuItem { Header = "置顶 / 取消置顶" };
        pin.Click += (_, _) => TogglePinSelected();
        menu.Items.Add(pin);

        var open = new MenuItem { Header = "打开所在位置" };
        open.Click += (_, _) => OpenSelectedLocation();
        menu.Items.Add(open);

        menu.Items.Add(new Separator());

        var del = new MenuItem { Header = "删除这一条" };
        del.Click += (_, _) => DeleteSelected();
        menu.Items.Add(del);

        List.ContextMenu = menu;
    }

    /// <summary>
    /// 在资源管理器里定位选中条目对应的文件 / 文件夹 / 图片。
    ///
    /// 为什么不给文本条目也做"打开所在位置"：文本没有磁盘位置，
    /// 硬做一个只会让菜单项时灵时不灵。文本条目的该项会被禁用。
    /// </summary>
    private void OpenSelectedLocation()
    {
        var entry = SelectedEntry;
        if (entry is null)
        {
            return;
        }

        try
        {
            var target = entry.Kind switch
            {
                // 文件：直接定位到第一个
                ClipKind.Files => (entry.Files ?? new List<ClipFile>()).FirstOrDefault()?.Path,

                // 图片：定位到工具箱保存的那份原图
                ClipKind.Image => GetAbsoluteBlobPath(entry),

                _ => null,
            };

            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target) && !Directory.Exists(target))
            {
                _ctx.Notify("快捷剪贴板", "这条记录没有可打开的磁盘位置。");
                return;
            }

            // /select 让它定位并选中那个文件，而不是只打开文件夹
            var args = Directory.Exists(target) ? $"\"{target}\"" : $"/select,\"{target}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Exception("打开所在位置失败", ex);
            _ctx.Notify("快捷剪贴板", $"打开失败：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 列表

    /// <summary>
    /// 缩略图内存缓存。
    ///
    /// 为什么需要：搜索框每敲一个字符都会 RefreshList，而 RefreshList 会把**所有**条目
    /// 重新 MakeRow 一遍 —— 图片条目每一条都要读盘 + 解码。上限 2000 条 / 500 MB 时，
    /// 敲几个字就能明显卡顿（ListBox 的虚拟化救不了这一步，因为行是先全建好再交给列表的）。
    /// 缓存之后同一张缩略图只解码一次。
    /// </summary>
    private readonly Dictionary<string, BitmapSource?> _thumbCache = new(StringComparer.OrdinalIgnoreCase);

    private BitmapSource? LoadThumbCached(ClipEntry e)
    {
        if (e.Kind != ClipKind.Image)
        {
            return null;
        }

        var key = e.Hash ?? e.Blob ?? e.TextPreview ?? "";
        if (key.Length == 0)
        {
            return _store.LoadThumb(e);
        }

        if (_thumbCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // 简单粗暴的上限：攒够 400 张就清空重来。
        // 不做 LRU 是刻意的 —— 缩略图很小，清空重建的代价远低于维护一套 LRU 的复杂度。
        if (_thumbCache.Count > 400)
        {
            _thumbCache.Clear();
        }

        var thumb = _store.LoadThumb(e);
        _thumbCache[key] = thumb;
        return thumb;
    }

    private void OnDateFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // 窗口加载完成前（包括 InitializeComponent 里 SelectedIndex="0" 触发的那次）一律忽略。
        // 见 _ready 字段上的说明——那时候列表 ListBox 还没建好，直接刷新会空引用。
        if (!_ready)
        {
            return;
        }

        RefreshList();
    }

    /// <summary>当前日期筛选档位。直接对上 ComboBox 的 SelectedIndex，省一层映射。</summary>
    private int DateFilterIndex => DateFilter?.SelectedIndex ?? 0;

    /// <summary>
    /// 这条记录是否落在当前日期档位内。
    /// 与关键字条件是「与」的关系：先由 Search 按关键字筛，这里再按日期筛。
    /// </summary>
    private bool InDateRange(ClipEntry e)
        => DateRangeFilter.Matches(e.TimeValue, DateFilterIndex, DateTime.Today);

    public void RefreshList()
    {
        try
        {
            var keyword = SearchBox.Text;
            var entries = _store.Search(keyword, _ctx.Settings.ShowToolWritten);

            var filtered = entries.Where(InDateRange).ToList();
            _rows = filtered.Select(MakeRow).ToList();
            List.ItemsSource = _rows;

            if (_rows.Count > 0 && List.SelectedIndex < 0)
            {
                List.SelectedIndex = 0;
            }

            var total = _store.Count;
            var hasFilter = !string.IsNullOrWhiteSpace(keyword) || DateFilterIndex > 0;
            CountText.Text = hasFilter
                ? $"命中 {_rows.Count} / {total} 条"
                : $"共 {total} 条";
        }
        catch (Exception ex)
        {
            Log.Exception("刷新剪贴板列表失败", ex);
        }
    }

    private ClipRow MakeRow(ClipEntry e)
    {
        var (icon, title, subtitle) = Describe(e);

        // 今天的只显示时刻，更早的带上日期 —— 否则筛"最近 30 天"时
        // 满屏都是 "14:32:07"，根本分不清是哪天的。
        var time = e.TimeValue == DateTime.MinValue
            ? ""
            : e.TimeValue.Date == DateTime.Today
                ? e.TimeValue.ToString("HH:mm:ss")
                : e.TimeValue.ToString("MM-dd HH:mm");

        var thumb = LoadThumbCached(e);

        return new ClipRow
        {
            Entry = e,
            Icon = icon,
            Title = title,
            Subtitle = subtitle,
            TimeText = time,
            PinText = e.Pinned ? "📌" : "",
            Thumb = thumb,
            IconVisibility = thumb is null ? Visibility.Visible : Visibility.Collapsed,
            ThumbVisibility = thumb is null ? Visibility.Collapsed : Visibility.Visible,
        };
    }

    private static (string Icon, string Title, string Subtitle) Describe(ClipEntry e)
    {
        switch (e.Kind)
        {
            case ClipKind.Text:
            {
                var flat = Flatten(e.TextPreview ?? "");

                // ★ 超长内容在列表里只显示一行摘要，不铺一整屏正文
                if (!string.IsNullOrEmpty(e.Volatile))
                {
                    return ("📄", $"文章（{e.TextLength} 字）", flat.Length > 60 ? flat[..60] + "…" : flat);
                }

                return ("📄", flat, $"{e.TextLength} 字");
            }

            case ClipKind.Image:
            {
                // ★ 显示图片在磁盘上的**原位置**（用户要求）。
                //
                // 这里要如实说清楚一件事：剪贴板里的图片**没有"来源文件路径"**这个信息。
                // 剪贴板传的是像素数据（CF_BITMAP / CF_DIB / PNG），
                // 不像 CF_HDROP（文件）那样天然带着路径 —— 系统 API 根本不提供
                // "这张图是从哪个文件复制来的"。想拿它只能靠猜（比如盯着前台窗口），
                // 那是不可靠的。
                //
                // 所以这里显示的是**工具箱把它存到了哪里**（blobs\<hash>.png）——
                // 那是它在磁盘上真实、可点开的位置，也是用户真正需要的东西：
                // "我复制过的那张图，现在在哪儿能找到"。
                var blobPath = GetAbsoluteBlobPath(e);
                var sizeText = $"{e.ImageWidth} × {e.ImageHeight}　{e.Bytes / 1024} KB";

                return ("🖼", sizeText, blobPath ?? "（原图文件已不存在）");
            }

            case ClipKind.Files:
            {
                var files = e.Files ?? new List<ClipFile>();
                var first = files.FirstOrDefault();
                var name = first is null ? "" : System.IO.Path.GetFileName(first.Path);
                if (string.IsNullOrEmpty(name))
                {
                    name = first?.Path ?? "";
                }

                var more = files.Count > 1 ? $" 等 {files.Count} 个" : "";

                // ★ 显示文件/文件夹的**原位置**（用户要求）。
                //   多个文件时显示所在的目录（比只显示第一个文件的完整路径更有用 ——
                //   用户想找的是"这批文件在哪个文件夹"）。
                var location = DescribeLocation(files);

                return ("📁", name + more, location);
            }

            default:
                return ("❔", e.Error ?? "(未知内容)", "");
        }
    }

    /// <summary>
    /// 图片原图在磁盘上的绝对路径。取不到返回 null。
    /// </summary>
    private static string? GetAbsoluteBlobPath(ClipEntry e)
    {
        if (string.IsNullOrEmpty(e.Blob))
        {
            return null;
        }

        try
        {
            var full = System.IO.Path.Combine(AppPaths.ClipboardDir, e.Blob);
            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 描述这批文件的"原位置"。
    ///
    /// 三种情况分开处理，因为这三种对用户的意义完全不同：
    ///   · 单个**文件**   → 显示它所在的文件夹
    ///   · 单个**文件夹** → 显示它的完整路径（否则只看到父目录，就找不到它自己）
    ///   · 多个条目      → 若都在同一目录，显示那个目录；否则说明"来自 N 个位置"
    /// </summary>
    private static string DescribeLocation(List<ClipFile> files)
    {
        if (files.Count == 0)
        {
            return "";
        }

        try
        {
            if (files.Count == 1)
            {
                var f = files[0];

                if (f.IsDirectory)
                {
                    return f.Exists ? f.Path : $"{f.Path}（已不存在）";
                }

                var dir = System.IO.Path.GetDirectoryName(f.Path) ?? f.Path;
                return f.Exists ? dir : $"{dir}（文件已不存在）";
            }

            // 多个条目：看看是不是同一个目录
            var dirs = files
                .Select(f =>
                {
                    if (f.IsDirectory)
                    {
                        return System.IO.Path.GetDirectoryName(f.Path) ?? f.Path;
                    }

                    return System.IO.Path.GetDirectoryName(f.Path) ?? "";
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (dirs.Count == 1)
            {
                return $"{dirs[0]}　（{files.Count} 个）";
            }

            // 来自不同目录：把前两个目录列出来，避免一行太长
            var shown = string.Join("；", dirs.Take(2));
            return $"{shown}　等 {dirs.Count} 个位置";
        }
        catch
        {
            return files[0].Path;
        }
    }

    private static string Flatten(string s)
        => s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();

    // ---------------------------------------------------------------- 操作

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CopySelected();
                e.Handled = true;
                break;

            // 两个键都收：底部提示写的是 Ctrl+D（更好按），但 Ctrl+Delete 也是很多人的肌肉记忆。
            // 原来只支持 Ctrl+Delete，而提示写着 Ctrl+D —— 用户照提示按毫无反应。
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.Control:
            case Key.D when Keyboard.Modifiers == ModifierKeys.Control:
                DeleteSelected();
                e.Handled = true;
                break;

            case Key.P when Keyboard.Modifiers == ModifierKeys.Control:
                TogglePinSelected();
                e.Handled = true;
                break;
        }
    }

    private ClipEntry? SelectedEntry
        => (List.SelectedItem as ClipRow)?.Entry;

    private void CopySelected()
    {
        var entry = SelectedEntry;
        if (entry is null)
        {
            return;
        }

        try
        {
            var ok = entry.Kind switch
            {
                ClipKind.Text => ClipboardWriter.SetText(_store.GetFullText(entry) ?? "", out var e1) is var r1
                    ? Report(r1, e1)
                    : false,

                ClipKind.Image => CopyImage(entry),

                ClipKind.Files => ClipboardWriter.SetFiles(
                    (entry.Files ?? new List<ClipFile>()).Select(f => f.Path).ToList(), out var e3) is var r3
                    ? Report(r3, e3)
                    : false,

                _ => false,
            };

            if (!ok)
            {
                return;
            }

            // 命中过的条目顶到最前
            _store.Touch(entry);

            Hide();

            // 提示"已复制，Ctrl+V 粘贴"——V1 刻意不自动粘贴（用户选的稳妥方案）
            _ctx.Notify("快捷剪贴板", $"已复制，去目标窗口 Ctrl+V 粘贴。");
        }
        catch (Exception ex)
        {
            Log.Exception("回填剪贴板失败", ex);
            _ctx.Notify("快捷剪贴板", $"回填失败：{ex.Message}");
        }
    }

    private static bool Report(bool ok, string? error)
    {
        if (!ok && error is not null)
        {
            Log.Error(error);
        }

        return ok;
    }

    private bool CopyImage(ClipEntry entry)
    {
        var image = _store.LoadImage(entry);
        if (image is null)
        {
            _ctx.Notify("快捷剪贴板", "这条图片的原始数据已经找不到了（可能被清理过）。");
            return false;
        }

        if (!ClipboardWriter.SetImage(image, out var error))
        {
            Log.Error(error ?? "写入图片到剪贴板失败");
            return false;
        }

        return true;
    }

    private void DeleteSelected()
    {
        var entry = SelectedEntry;
        if (entry is null)
        {
            return;
        }

        var index = List.SelectedIndex;
        _store.Remove(entry);
        RefreshList();

        if (List.Items.Count > 0)
        {
            List.SelectedIndex = Math.Min(index, List.Items.Count - 1);
        }
    }

    private void TogglePinSelected()
    {
        var entry = SelectedEntry;
        if (entry is null)
        {
            return;
        }

        _store.SetPinned(entry, !entry.Pinned);
        RefreshList();
    }
}
