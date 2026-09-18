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

        // 收起后高度变小，重新贴回工作区内（否则可能被任务栏盖住）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ClampIntoWorkArea();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ClampIntoWorkArea()
    {
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
