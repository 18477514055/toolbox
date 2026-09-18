using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Toolbox.Core;

namespace Toolbox.Shell;

/// <summary>
/// 托盘图标。
///
/// 这里刻意只用 WinForms 的 NotifyIcon（唯一用到 WinForms 的地方），
/// 图标则在运行时用 GDI+ 画出来——不引入任何二进制资源文件，
/// 也不需要在 csproj 里配 Resource，改配色只要改这里的几行。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private System.Windows.Forms.NotifyIcon? _icon;
    private IntPtr _hIcon;
    private readonly Dictionary<string, System.Windows.Forms.ToolStripMenuItem> _pauseItems = new();

    public event Action? OpenClipboardRequested;
    public event Action? ToggleFloatingRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<bool>? PauseToggled;

    /// <summary>「所有工具」子菜单的宿主（由 App 填充，托盘不关心具体有哪些工具）。</summary>
    private System.Windows.Forms.ToolStripMenuItem? _toolsMenu;

    /// <summary>
    /// 填充「所有工具」子菜单。由 App 调用（它才有工具注册表）。
    /// </summary>
    /// <param name="tools">(显示名, 工具 Id, 热键提示) 三元组。</param>
    /// <param name="onInvoke">点击后调用的动作，参数是工具 Id。</param>
    public void FillToolsMenu(
        IEnumerable<(string Name, string Id, string HotKey)> tools,
        Action<string> onInvoke)
    {
        if (_toolsMenu is null)
        {
            return;
        }

        _toolsMenu.DropDownItems.Clear();

        foreach (var (name, id, hotKey) in tools)
        {
            var text = string.IsNullOrWhiteSpace(hotKey) ? name : $"{name}　（{hotKey}）";
            var item = new System.Windows.Forms.ToolStripMenuItem(text);
            var captured = id;
            item.Click += (_, _) => onInvoke(captured);
            _toolsMenu.DropDownItems.Add(item);
        }

        if (_toolsMenu.DropDownItems.Count == 0)
        {
            _toolsMenu.Enabled = false;
        }
    }

    /// <summary>
    /// 总开关变化时同步托盘菜单。
    ///
    /// 为什么托盘也要管：总开关关掉后悬浮窗上没按钮了，
    /// 如果托盘菜单还列着一堆工具且能点开，用户会以为总开关没生效。
    /// 关掉时把入口**变灰 + 改标题**（不是删掉）—— 删掉会让人以为功能没了，
    /// 变灰则明确表达"装了，但现在停着"。
    /// </summary>
    public void SetToolsEnabled(bool enabled)
    {
        if (_toolsMenu is null)
        {
            return;
        }

        _toolsMenu.Text = enabled ? "所有工具" : "所有工具（总开关已关闭）";
        _toolsMenu.Enabled = enabled && _toolsMenu.DropDownItems.Count > 0;
    }

    public void Show(Settings settings)
    {
        _hIcon = CreateIconHandle();

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = Icon.FromHandle(_hIcon),
            Text = "桌面工具箱",
            Visible = true,
        };

        _icon.DoubleClick += (_, _) => ToggleFloatingRequested?.Invoke();

        var menu = new System.Windows.Forms.ContextMenuStrip();

        var open = new System.Windows.Forms.ToolStripMenuItem("显示 / 隐藏悬浮窗");
        open.Click += (_, _) => ToggleFloatingRequested?.Invoke();
        menu.Items.Add(open);

        var clip = new System.Windows.Forms.ToolStripMenuItem("打开剪贴板历史");
        clip.Click += (_, _) => OpenClipboardRequested?.Invoke();
        menu.Items.Add(clip);

        // ★ 「所有工具」子菜单。
        //
        // 为什么需要它：工具从 5 个涨到 10 个之后，托盘里只有"剪贴板"一个直达项，
        // 其余工具想从托盘打开就得先想起热键、或去悬浮窗上找。
        // 有热键或悬浮窗当然更快，但托盘的定位就是"兜底入口"——
        // 用户忘了热键、又把悬浮窗收起来了时，得有个地方能点到**全部**工具。
        //
        // 这一项由外部注入（App 那边知道工具注册表），托盘本身不该知道有哪些工具
        // —— 那是 Shell 层不该有的耦合。
        var tools = new System.Windows.Forms.ToolStripMenuItem("所有工具");
        menu.Items.Add(tools);
        _toolsMenu = tools;

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var pause = new System.Windows.Forms.ToolStripMenuItem("暂停剪贴板监听")
        {
            CheckOnClick = true,
            Checked = settings.Paused,
        };
        pause.Click += (_, _) => PauseToggled?.Invoke(pause.Checked);
        _pauseItems["pause"] = pause;
        menu.Items.Add(pause);

        var settingsItem = new System.Windows.Forms.ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var exit = new System.Windows.Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        _icon.ContextMenuStrip = menu;
    }

    /// <summary>气泡提示。剪贴板回填后提示「已复制，Ctrl+V 粘贴」就用它。</summary>
    public void Notify(string title, string message)
    {
        try
        {
            _icon?.ShowBalloonTip(2500, title, message, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch
        {
            // 气泡提示失败无所谓（用户可能关了通知）
        }
    }

    public void SetPaused(bool paused)
    {
        if (_pauseItems.TryGetValue("pause", out var item))
        {
            item.Checked = paused;
        }
    }

    /// <summary>运行时画一个 32x32 的图标：蓝色圆底 + 白色工具箱。</summary>
    private static IntPtr CreateIconHandle()
    {
        using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var blue = Color.FromArgb(255, 41, 112, 209);

            using (var bg = new SolidBrush(blue))
            {
                g.FillEllipse(bg, 0, 0, 31, 31);
            }

            // 提手
            using (var pen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, 10, 7, 12, 12, 180, 180);
            }

            // 箱体
            using (var white = new SolidBrush(Color.White))
            {
                g.FillRectangle(white, 7, 16, 18, 10);
            }

            // 盖子分界线
            using (var line = new SolidBrush(blue))
            {
                g.FillRectangle(line, 7, 19, 18, 2);
            }
        }

        return bmp.GetHicon();
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }

        if (_hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
