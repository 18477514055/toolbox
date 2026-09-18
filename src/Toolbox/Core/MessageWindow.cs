namespace Toolbox.Core;

/// <summary>
/// 一个「消息专用窗口」（HWND_MESSAGE 的子窗口）。
///
/// 这是 DECISIONS.md 坑 1 的正确写法：显式 CreateWindowExW + SetWindowLongPtr 子类化。
/// 千万不要退回 WinForms 的 NativeWindow + CreateParams —— 那样注册监听会「成功」，
/// 但一条 WM_CLIPBOARDUPDATE 都收不到，能查一整天。
///
/// 线程约定：窗口属于创建它的线程，所有消息都在该线程的 WndProc 上回调。
/// 所以要收到剪贴板通知，调用方必须在该线程上跑消息循环（见 MessageThread）。
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private readonly NativeMethods.WndProcDelegate _proc; // 必须保引用，否则 GC 后回调会崩
    private readonly Func<uint, IntPtr, IntPtr, bool> _handler;
    private IntPtr _hwnd;

    /// <param name="handler">返回 true 表示已处理，不再交给 DefWindowProc。</param>
    public MessageWindow(string title, Func<uint, IntPtr, IntPtr, bool> handler)
    {
        _handler = handler;
        _proc = WndProc;

        _hwnd = NativeMethods.CreateWindowExW(
            dwExStyle: 0,
            lpClassName: "STATIC",
            lpWindowName: title,
            dwStyle: NativeMethods.WS_POPUP,
            x: 0, y: 0, nWidth: 0, nHeight: 0,
            hWndParent: NativeMethods.HWND_MESSAGE,
            hMenu: IntPtr.Zero,
            hInstance: IntPtr.Zero,
            lpParam: IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"创建消息窗口失败（Win32 错误码 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）。");
        }

        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_WNDPROC,
            System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_proc));
    }

    public IntPtr Handle => _hwnd;

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 坑 2：异常绝不能从窗口过程逃出去。逃出去 = 消息循环线程死亡 =
        // 之后所有剪贴板事件静默消失，表现得像"监听坏了"，但其实是线程没了。
        try
        {
            if (_handler(msg, wParam, lParam))
            {
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log.Exception("消息窗口回调内未捕获异常（已兜住，消息循环继续存活）", ex);
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.DestroyWindow(_hwnd);
        }
        catch
        {
            // 退出路径上不值得因为销毁失败再抛一次
        }

        _hwnd = IntPtr.Zero;
    }
}
