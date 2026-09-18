using System.Diagnostics;
using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.WindowTopmost;

/// <summary>
/// 工具 10：窗口置顶（toggle）。
///
/// 交互：按热键 → 把**当前活动窗口**置顶；再按一次 → 取消置顶。
/// 托盘气泡会告诉你操作了哪个窗口、现在是置顶还是普通。
///
/// ⚠️ 「当前活动窗口」怎么取，是这个工具最容易做错的地方：
///   工具箱的悬浮窗**不抢焦点**（这是全项目的硬约束），所以按热键时
///   前台窗口仍然是用户真正在用的那个 —— 直接取 <c>GetForegroundWindow</c> 即可。
///   但有两个例外必须排除：
///     ① **我们自己的窗口**（用户可能正开着剪贴板面板之类的）——
///        把工具箱自己的面板置顶没有意义，还会让用户困惑；
///     ② 桌面 / 任务栏这类 shell 窗口。
///   排除方式：比较**进程号**（比列窗口名单可靠，见 DECISIONS 里 FocusTracker 的说明）。
/// </summary>
internal sealed class WindowTopmostTool : IToolboxTool
{
    private ToolboxContext? _ctx;

    public string Id => "topmost";

    public string Name => "窗口置顶";

    public string Glyph => "📌";

    /// <summary>P = Pin。本机 Win+Alt+P 空闲。</summary>
    public string DefaultHotKey => "Win+Alt+P";

    public string Description => "把当前窗口置顶 / 取消置顶";

    /// <summary>不打开任何窗口 —— 它是纯动作型工具，按一下就完事。</summary>
    public bool OpensBigWindow => false;

    public void Start(ToolboxContext ctx) => _ctx = ctx;

    public void Invoke()
    {
        if (_ctx is null)
        {
            return;
        }

        var target = ResolveTargetWindow(out var why);

        if (target == IntPtr.Zero)
        {
            _ctx.Notify("窗口置顶", why ?? "没找到可以置顶的窗口。");
            return;
        }

        var title = TopmostService.GetTitle(target);
        var wasTopmost = TopmostService.IsTopmost(target);

        if (!TopmostService.Toggle(target, out var error, out var nowTopmost))
        {
            _ctx.Notify("窗口置顶", error ?? "操作失败。");
            return;
        }

        var name = string.IsNullOrWhiteSpace(title) ? "当前窗口" : Shorten(title);
        _ctx.Notify("窗口置顶", nowTopmost ? $"「{name}」已置顶。" : $"「{name}」已取消置顶。");

        Log.Line($"窗口置顶 toggle：{name} —— {wasTopmost} → {nowTopmost}");
    }

    private static string Shorten(string s) => s.Length > 30 ? s[..30] + "…" : s;

    /// <summary>
    /// 决定要操作哪个窗口。
    ///
    /// 优先用 <c>FocusTracker</c> 记下的"最近一个非本工具的焦点窗口"，
    /// 它比直接取前台窗口更稳：万一工具箱自己的某个窗口刚好在前台
    /// （比如用户刚点了剪贴板面板），直接取前台就会操作到自己身上。
    /// </summary>
    private IntPtr ResolveTargetWindow(out string? why)
    {
        why = null;

        // ① 先看前台窗口 —— 正常情况下就是它
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != IntPtr.Zero && !IsOurProcess(foreground) && !IsShellWindow(foreground))
        {
            return foreground;
        }

        // ② 前台是我们自己或 shell ⇒ 退回到"最近一个外部窗口"
        var last = _ctx?.Focus.LastForeignWindow ?? IntPtr.Zero;
        if (last != IntPtr.Zero && NativeMethods.IsWindow(last) && !IsOurProcess(last))
        {
            return last;
        }

        why = "当前前台窗口是工具箱自己（或桌面）。\n"
              + "先点一下你想置顶的那个窗口，再按热键。";

        return IntPtr.Zero;
    }

    /// <summary>这个窗口是不是工具箱自己的。</summary>
    private static bool IsOurProcess(IntPtr hwnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 是不是 shell 窗口（桌面 / 任务栏）。
    /// 对它们置顶没有意义，而且桌面窗口置顶会让整个体验很怪。
    /// </summary>
    private static bool IsShellWindow(IntPtr hwnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
            var cls = sb.ToString();

            return cls is "Progman" or "WorkerW" or "Shell_TrayWnd"
                or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow";
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
    }
}
