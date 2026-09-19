using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Toolbox.Core;

namespace Toolbox.Shell;

/// <summary>
/// 工具箱的悬浮窗——**主要入口**，不是附属品。
///
/// 三条硬要求（都来自交接文档，且都影响功能正确性而不只是好看）：
///   1. ⛔ 绝不能抢焦点 → ShowActivated=false **并且** WS_EX_NOACTIVATE。
///      只做前者不够：那只是"显示时不激活"，用户一点还是会激活。
///      AI 的"读当前页面"全靠这条，一抢焦点就会读到悬浮窗自己。
///   2. 不进 Alt+Tab → WS_EX_TOOLWINDOW。
///   3. 位置按工作区百分比存 → 外接屏拔掉后不会跑到屏幕外。
/// </summary>
internal sealed partial class FloatingWindow : Window
{
    private readonly ToolboxContext _ctx;
    private readonly ToolRegistry _registry;

    private bool _dragging;
    private NativeMethods.POINT _dragCursorStart;
    private NativeMethods.RECT _dragWindowStart;

    /// <summary>被大窗口工具压住的次数（嵌套时用计数，不要用 bool）。</summary>
    private int _suppressCount;

    private bool _collapsed;
    private bool _positioned;

    public FloatingWindow(ToolboxContext ctx, ToolRegistry registry)
    {
        _ctx = ctx;
        _registry = registry;

        InitializeComponent();

        BuildButtons();

        DragHandle.MouseLeftButtonDown += OnDragStart;
        DragHandle.MouseMove += OnDragMove;
        DragHandle.MouseLeftButtonUp += OnDragEnd;

        ShellBorder.MouseEnter += (_, _) => AnimateOpacity(1.0);
        ShellBorder.MouseLeave += (_, _) => AnimateOpacity(_ctx.Settings.FloatingOpacity);

        Loaded += OnLoaded;
    }

    // ---------------------------------------------------------------- 焦点铁律

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_NOACTIVATE;   // 点了也不抢焦点
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;   // 不进 Alt+Tab
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        Log.Line($"悬浮窗扩展样式已设置：NOACTIVATE + TOOLWINDOW（0x{exStyle:X8}）");
    }

    /// <summary>永远不要激活自己——连 WPF 内部的激活请求也挡掉。</summary>
    protected override void OnActivated(EventArgs e)
    {
        // 故意不调用 base：一旦激活就会把用户的焦点从正在编辑的窗口抢走
    }

    // ---------------------------------------------------------------- 生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_positioned)
        {
            RestorePosition();
            _positioned = true;
        }

        Opacity = _ctx.Settings.FloatingOpacity;

        if (_ctx.Settings.FloatingCollapsed)
        {
            // ★ 必须**先**把字段同步过来，再应用外观。
            //
            //   踩过的坑（实测）：原来只调 ApplyCollapsed(true)，
            //   而 _collapsed 字段仍是默认的 false。于是自愈逻辑里
            //   `if (_collapsed)` 不成立 —— 窗口被放大了，但
            //   **没写回设置**，下次启动又走一遍同样的异常流程
            //   （每次开机都报一条"尺寸异常"）。
            //
            //   字段与设置必须一致，否则"当前到底收没收起"就有两个答案。
            _collapsed = true;
            ApplyCollapsed(true);
        }
    }

    private void RestorePosition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;

        var (x, y) = ScreenPlacement.Resolve(
            _ctx.Settings.FloatingX, _ctx.Settings.FloatingY, w, h, 1, 1);

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, (int)x, (int)y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void AnimateOpacity(double target)
    {
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(140))
        {
            FillBehavior = FillBehavior.HoldEnd,
        };
        BeginAnimation(OpacityProperty, anim);
    }

    // ---------------------------------------------------------------- 拖动

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out _dragCursorStart))
        {
            return;
        }

        _dragWindowStart = rect;
        _dragging = true;
        DragHandle.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetCursorPos(out var now))
        {
            return;
        }

        // 全程用物理像素 + SetWindowPos：避免 DIP 换算在多显示器/不同缩放比下出错
        var x = _dragWindowStart.Left + (now.X - _dragCursorStart.X);
        var y = _dragWindowStart.Top + (now.Y - _dragCursorStart.Y);

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        DragHandle.ReleaseMouseCapture();
        e.Handled = true;

        SavePosition();

        // 收起状态下，点一下拖动柄就是展开
        if (_collapsed)
        {
            SetCollapsed(false);
        }
    }

    private void SavePosition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;

        var (pctX, pctY) = ScreenPlacement.ToPercent(rect.Left, rect.Top, w, h, 1, 1);
        _ctx.Settings.FloatingX = pctX;
        _ctx.Settings.FloatingY = pctY;
        _ctx.SaveSettings();
    }

    // ---------------------------------------------------------------- 按钮

    public void BuildButtons()
    {
        ToolHost.Children.Clear();
        FooterHost.Children.Clear();

        foreach (var tool in _registry.VisibleTools(_ctx.Settings))
        {
            var spec = HotKeyOf(tool);
            var busy = BusyState.IsBusy(tool.Id);

            // 长任务进行中时换成沙漏：否则用户不知道它在干活，会以为卡死了
            var glyph = busy ? "⏳" : tool.Glyph;

            var tip = busy
                ? $"{tool.Name}　{(BusyState.LabelOf(tool.Id) ?? "处理中")}…"
                : string.IsNullOrEmpty(spec)
                    ? $"{tool.Name}\n{tool.Description}"
                    : $"{tool.Name}　{spec}\n{tool.Description}";

            var id = tool.Id;
            ToolHost.Children.Add(MakeButton(glyph, tip, () => _ctx.InvokeTool(id)));
        }

        FooterHost.Children.Add(MakeButton("⚙", "设置", OpenSettings));
        FooterHost.Children.Add(MakeButton("⌃", "收起 / 隐藏到托盘", () => SetCollapsed(!_collapsed)));
    }

    private string HotKeyOf(IToolboxTool tool)
        => _ctx.Settings.HotKeys.TryGetValue(tool.Id, out var custom) && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : tool.DefaultHotKey;

    private UIElement MakeButton(string glyph, string tooltip, Action onClick)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x30, 0x39)),
        };

        var border = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(9),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 1, 0, 1),
            Child = text,
            ToolTip = tooltip,
        };

        var hover = new SolidColorBrush(Color.FromArgb(0x28, 0x29, 0x70, 0xD1));
        border.MouseEnter += (_, _) => border.Background = hover;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;

        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try
            {
                onClick();
            }
            catch (Exception ex)
            {
                Log.Exception("悬浮窗按钮点击处理失败", ex);
            }
        };

        return border;
    }

    private void OpenSettings()
    {
        try
        {
            // 交给 App 去开：它才有 ToolRegistry 和「重新注册热键」的回调。
            _ctx.OpenSettings();
        }
        catch (Exception ex)
        {
            Log.Exception("打开设置窗口失败", ex);
        }
    }

    // ---------------------------------------------------------------- 收起 / 隐藏

    private void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        ApplyCollapsed(collapsed);

        _ctx.Settings.FloatingCollapsed = collapsed;
        _ctx.SaveSettings();
    }

    private void ApplyCollapsed(bool collapsed)
    {
        var visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ToolHost.Visibility = visibility;
        FooterHost.Visibility = visibility;
        Separator.Visibility = visibility;

        // ══════════════════════════════════════════════════════════════════
        //  ★★ 关键：显式重算窗口尺寸 ★★
        //
        //  这里踩过一个**会让悬浮窗彻底展不开**的坑（实测抓到，见 DECISIONS 坑 47）：
        //
        //  现象：用户点了展开（或从收起状态启动），内容 Visibility 已经变成 Visible，
        //        但窗口**仍然是收起时的 22×32 像素** —— 内容被裁得完全看不见，
        //        表现就是"点了没反应 / 无法展开"。
        //
        //  根因：XAML 是 `SizeToContent=WidthAndHeight`，本来应该由 WPF
        //        在布局后自动改窗口大小。但本窗口同时满足这三个条件：
        //          ① ResizeMode="NoResize"（窗口句柄被建成不可调整大小）
        //          ② 我们**自己**用 SetWindowPos 改过窗口位置（ClampIntoWorkArea）
        //          ③ 收起到展开时，WPF 认为"窗口大小没变，不需要重算"
        //        ⇒ 自动尺寸调整**不生效**，窗口卡在极小尺寸。
        //
        //  修法：在可见性变化后**主动**把窗口尺寸收放一次，逼 WPF 重新测量：
        //        先归零（Width/Height = Auto 时让内容决定），再同步更新布局。
        //        这一手不依赖任何时序假设，比"等 Dispatcher 跑一轮"可靠得多。
        // ══════════════════════════════════════════════════════════════════

        // SizeToContent 模式下，把显式尺寸清掉，让内容重新决定大小
        SizeToContent = SizeToContent.Manual;
        Width = double.NaN;
        Height = double.NaN;

        // 立刻跑一轮布局，让上面三个面板的可见性变化反映到 DesiredSize 上
        UpdateLayout();

        // 再交回 SizeToContent，窗口就会按新的内容尺寸调整
        SizeToContent = SizeToContent.WidthAndHeight;
        UpdateLayout();

        // 尺寸变化之后重新贴回工作区（收起时变小、展开时变大，两个方向都可能出界）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ClampIntoWorkArea();

            // 记一行日志：这类"尺寸没更新"的问题极难从界面上判断，
            // 有日志才能事后确认"展开时窗口到底多大了"
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r))
            {
                // ⚠️ 记**实际状态**（_collapsed），不是参数 collapsed ——
                //    因为 EnsureUsableSize 可能刚刚把收起的窗口自愈展开，
                //    用参数会记成"收起：136x660"这种自相矛盾的行（实测出现过）。
                var state = _collapsed ? "收起" : "展开";
                Log.Line($"悬浮窗{state}：尺寸 {(r.Right - r.Left)}x{(r.Bottom - r.Top)}");
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 兜底自救：悬浮窗尺寸小得不正常时，强制恢复成可用尺寸**并展开**。
    ///
    /// 背景（实测事故，见 <see cref="ApplyCollapsed"/> 的注释）：
    ///   悬浮窗曾卡在 22×32 像素 —— 内容全被裁掉、点了没反应。
    ///   用户唯一的出路是手改 settings.json，而普通用户根本不会。
    ///
    /// ⚠️ 这里有两个层次，缺一不可：
    ///   ① **把窗口放大** —— 保证有点得着的地方；
    ///   ② **把内容展开** —— 光放大没用，收起状态下内容仍是 Collapsed，
    ///      用户看到的还是一片空白（第一版只做了 ①，实测发现不够）。
    ///
    /// **凡是"界面把自己变得没法操作"的故障都必须有兜底** ——
    /// 因为那种情况下用户连"打开设置改回来"都做不到。
    /// </summary>
    private void EnsureUsableSize()
    {
        try
        {
            if (ActualWidth >= MinUsableWidth && ActualHeight >= MinUsableHeight)
            {
                return;
            }

            Log.Error($"悬浮窗尺寸异常（{ActualWidth:0}×{ActualHeight:0}），强制恢复并展开。");

            // ① 尺寸：取消 SizeToContent 的自动收缩，直接给一个可用尺寸
            SizeToContent = SizeToContent.Manual;
            Width = Math.Max(ActualWidth, DefaultExpandedWidth);
            Height = Math.Max(ActualHeight, DefaultExpandedHeight);

            // ② 内容：把展开状态也一并恢复。
            //
            //    只做 ① 的话窗口是大了，但 ToolHost 仍是 Collapsed ⇒ 一片空白，
            //    用户依然什么都点不到。这个坑我实测踩过（第一版只放大了窗口）。
            //
            //    ⚠️ 判据**不能**用 `if (_collapsed)`：
            //       尺寸异常本身已经说明状态不对，无论字段说什么都得展开并写回。
            //       （曾因字段没和设置同步，自愈只放大了窗口却没写回设置，
            //         于是每次开机都重复触发一次这个异常路径。）
            _collapsed = false;
            ToolHost.Visibility = Visibility.Visible;
            FooterHost.Visibility = Visibility.Visible;
            Separator.Visibility = Visibility.Visible;

            // 设置里也要改回去，否则下次启动又是收起的
            try
            {
                _ctx.Settings.FloatingCollapsed = false;
                _ctx.SaveSettings();
            }
            catch (Exception ex)
            {
                Log.Exception("恢复展开状态时写设置失败", ex);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("恢复悬浮窗尺寸失败", ex);
        }
    }

    /// <summary>悬浮窗的最小可用尺寸。小于它就没法点击/阅读了。</summary>
    private const double MinUsableWidth = 100;
    private const double MinUsableHeight = 100;

    /// <summary>恢复时使用的默认尺寸（与 XAML 里的设计尺寸一致）。</summary>
    private const double DefaultExpandedWidth = 136;
    private const double DefaultExpandedHeight = 660;

    private void ClampIntoWorkArea()
    {
        // ★ 兜底自救：如果窗口尺寸小得不正常，强制拉回可用尺寸。
        //
        //   为什么需要（实测事故）：
        //     悬浮窗曾因"展开时尺寸没重算"卡在 22×32 像素 ——
        //     内容全被裁掉、点也没反应，用户**完全没法继续用**，
        //     只能手工改 settings.json。
        //
        //   这类"界面把自己变得没法操作"的故障最糟糕：用户没有任何自救手段。
        //   所以在这里加一道：只要发现尺寸小于可用下限，就主动恢复。
        //   宁可尺寸偶尔不对，也绝不能变成一块点不动的碎片。
        EnsureUsableSize();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;

        var (pctX, pctY) = ScreenPlacement.ToPercent(rect.Left, rect.Top, w, h, 1, 1);
        var (x, y) = ScreenPlacement.Resolve(pctX, pctY, w, h, 1, 1);

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, (int)x, (int)y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>大窗口工具打开时，把悬浮窗临时藏起来（默认行为，可在设置里关掉）。</summary>
    public IDisposable Suppress()
    {
        _suppressCount++;
        if (_suppressCount == 1 && _ctx.Settings.HideFloatingWhenToolOpen)
        {
            Hide();
        }

        return new SuppressToken(this);
    }

    private sealed class SuppressToken : IDisposable
    {
        private readonly FloatingWindow _owner;
        private bool _disposed;

        public SuppressToken(FloatingWindow owner) => _owner = owner;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner._suppressCount = Math.Max(0, _owner._suppressCount - 1);

            if (_owner._suppressCount == 0 && _owner._ctx.Settings.ShowFloatingWindow)
            {
                _owner.ShowWithoutActivation();
            }
        }
    }

    /// <summary>显示但绝不激活。</summary>
    public void ShowWithoutActivation()
    {
        if (!IsVisible)
        {
            Show();
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            ShowWithoutActivation();
        }

        _ctx.Settings.ShowFloatingWindow = IsVisible;
        _ctx.SaveSettings();
    }
}
