using System.Text;
using Toolbox.Core;

namespace Toolbox.Shell;

/// <summary>
/// 记录「最近一个不是工具箱自己的焦点窗口」。
///
/// 这是 AI 工具「读当前页面」能work的前提（交接文档 §五·工具4 的警告）：
/// 工具唤出面板后会**抢走焦点**，此时用 UIA 读"当前焦点窗口"读到的是我们自己的面板。
/// 所以必须在唤出之前就把上一个外部窗口记下来。
///
/// 实现用 SetWinEventHook(EVENT_SYSTEM_FOREGROUND) + **WINEVENT_SKIPOWNPROCESS**：
/// 后者一步到位地排除了悬浮窗和所有工具箱面板（它们全在本进程里），
/// 不需要自己维护"哪些窗口是自己的"名单——那种名单迟早会漏。
/// </summary>
internal sealed class FocusTracker : IDisposable
{
    private readonly NativeMethods.WinEventDelegate _callback; // 保引用
    private IntPtr _hook;

    public FocusTracker()
    {
        _callback = OnWinEvent;
    }

    private readonly object _gate = new();

    public IntPtr LastForeignWindow { get; private set; }
    public string LastForeignTitle { get; private set; } = "";
    public string LastForeignProcess { get; private set; } = "";
    public DateTime LastForeignAt { get; private set; }

    public void Start()
    {
        // 注意：回调投递到**调用 SetWinEventHook 的那个线程**的消息队列，
        // 所以本方法必须在有消息循环的线程（WPF UI 线程）上调用。
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (_hook == IntPtr.Zero)
        {
            Log.Error("前台窗口事件钩子安装失败——AI 的「读当前页面」将退化为「读唤出前的焦点窗口」的即时快照。");
        }
        else
        {
            Log.Line("前台窗口追踪已启动（WINEVENT_SKIPOWNPROCESS，自动排除工具箱自己的窗口）。");
        }

        CaptureNow();
    }

    /// <summary>
    /// 立刻记一次当前前台窗口。用于程序刚启动、或钩子不可用时的兜底。
    /// 会自动跳过属于本进程的窗口。
    /// </summary>
    public void CaptureNow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        Remember(hwnd);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            // 只关心窗口本身的前台切换，不关心控件级焦点
            if (idObject != 0 /* OBJID_WINDOW */ || idChild != 0)
            {
                return;
            }

            Remember(hwnd);
        }
        catch (Exception ex)
        {
            Log.Exception("前台窗口追踪回调出错", ex);
        }
    }

    private void Remember(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return;
        }

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root != IntPtr.Zero)
        {
            hwnd = root;
        }

        // 再兜一层：钩子带 SKIPOWNPROCESS，但 CaptureNow 是主动查询，这里要自己判断
        if (IsOwnProcess(hwnd))
        {
            return;
        }

        var title = GetWindowText(hwnd);
        var process = NativeMethods.ProcessNameOfWindow(hwnd);

        lock (_gate)
        {
            LastForeignWindow = hwnd;
            LastForeignTitle = title;
            LastForeignProcess = process;
            LastForeignAt = DateTime.Now;
        }
    }

    private static bool IsOwnProcess(IntPtr hwnd)
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

    private static string GetWindowText(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(512);
            NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>供 UI 显示：「当前会去读哪个窗口」。</summary>
    public string DescribeTarget()
    {
        lock (_gate)
        {
            if (LastForeignWindow == IntPtr.Zero)
            {
                return "（还没记录到外部窗口）";
            }

            var name = string.IsNullOrEmpty(LastForeignTitle) ? "(无标题)" : LastForeignTitle;
            return $"{LastForeignProcess} · {name}";
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
