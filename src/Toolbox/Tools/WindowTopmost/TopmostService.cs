using System.Text;
using Toolbox.Core;

namespace Toolbox.Tools.WindowTopmost;

/// <summary>
/// 把窗口置顶 / 取消置顶。
///
/// 实现方式：<c>SetWindowPos</c> 配 <c>HWND_TOPMOST</c> / <c>HWND_NOTOPMOST</c>。
/// 这是 Windows 官方支持的做法（任务管理器"置于顶层"用的就是同一套）。
///
/// ⚠️ 为什么不用 <c>WS_EX_TOPMOST</c> 扩展样式：
///   那是**只读**的反馈位，写它不生效。必须走 SetWindowPos。
///
/// ⚠️ 三个必须处理的边界（都会让"toggle"表现得像坏了）：
///   ① **最小化的窗口**置顶没有意义，且 SetWindowPos 会失败 —— 明确告诉用户；
///   ② **我们自己的窗口**（悬浮窗/各面板）不该被操作 —— 否则用户可能把悬浮窗
///      设成置顶，然后它就一直压在所有东西上面（它本来就是 Topmost）；
///   ③ **查当前状态**必须真的去问窗口，不能自己记 ——
///      用户可能用别的工具（或任务管理器）改过置顶状态，
///      自己记一个 bool 就会和实际不一致，toggle 反着走。
/// </summary>
internal static class TopmostService
{
    /// <summary>查询某个窗口当前是不是置顶。</summary>
    public static bool IsTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        // 用 GetWindowLongPtr(GWL_EXSTYLE) 读 WS_EX_TOPMOST —— 这一位是**只读反馈**，
        // 反映系统里真实的状态（写它没用，但读它是准的）。
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();

        return (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;
    }

    /// <summary>
    /// 设置置顶状态。
    /// </summary>
    /// <returns>成功返回 true；失败给 <paramref name="error"/> 中文原因。</returns>
    public static bool SetTopmost(IntPtr hwnd, bool topmost, out string? error)
    {
        error = null;

        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            error = "目标窗口已经不存在了（可能刚被关掉）。";
            return false;
        }

        // 最小化的窗口：置顶对它没有意义，而且 SetWindowPos 在这种状态下
        // 往往返回成功但看不出效果 —— 与其让用户以为没生效，不如直接说清楚。
        if (NativeMethods.IsIconic(hwnd))
        {
            error = "这个窗口当前是最小化的。先把它还原，再按热键置顶。";
            return false;
        }

        var insertAfter = topmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;

        // 只改 Z 序，不动位置和大小；NOACTIVATE 保证不抢焦点
        //（用户可能正在别的窗口里打字，置顶不该打断他）
        var flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;

        var ok = NativeMethods.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, flags);

        if (!ok)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            error = err switch
            {
                5 => "系统拒绝了这个操作（权限不足，可能目标窗口以管理员身份运行）。",
                _ => $"设置失败（Win32 错误码 {err}）。",
            };

            return false;
        }

        Log.Line($"窗口置顶已{(topmost ? "开启" : "关闭")}：{Describe(hwnd)}");
        return true;
    }

    /// <summary>切换置顶状态，返回切换后的状态。</summary>
    public static bool Toggle(IntPtr hwnd, out string? error, out bool nowTopmost)
    {
        nowTopmost = false;
        error = null;

        var current = IsTopmost(hwnd);
        var target = !current;

        if (!SetTopmost(hwnd, target, out error))
        {
            return false;
        }

        nowTopmost = target;
        return true;
    }

    /// <summary>取窗口标题（用于提示与日志）。失败返回空串。</summary>
    public static string GetTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return "";
        }

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

    /// <summary>窗口标题 + 类名，用于日志（标题可能为空，类名兜底）。</summary>
    public static string Describe(IntPtr hwnd)
    {
        var title = GetTitle(hwnd);
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title.Length > 60 ? title[..60] + "…" : title;
        }

        try
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
            return $"（无标题，类名 {sb}）";
        }
        catch
        {
            return "（无标题）";
        }
    }
}
