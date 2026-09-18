using System.Runtime.InteropServices;

namespace ClipboardProbe;

/// <summary>
/// 剪贴板变更监听：AddClipboardFormatListener + 一个隐藏的消息窗口。
///
/// 踩坑记录（第一版失败的原因，别再退回这种写法）：
///   一开始用 WinForms 的 NativeWindow + CreateParams{Parent = HWND_MESSAGE} 建窗口，
///   AddClipboardFormatListener 返回 true，但一条 WM_CLIPBOARDUPDATE 都收不到——
///   （实测：写入后回读剪贴板一切正常，格式/内容都对，纯粹是通知收不到）。
///   改成显式 CreateWindowExW + 消息循环线程后恢复正常。
///   结论：这类系统广播通知对窗口的创建方式敏感，不要图省事用框架封装。
/// </summary>
internal sealed class ClipboardListener : IDisposable
{
    /// <summary>WM_CLIPBOARDUPDATE = 0x031D</summary>
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private const int WM_DESTROY = 0x0002;

    /// <summary>WM_APP + 1，诊断用自投消息，验证窗口能否正常收消息。</summary>
    public const int WM_PROBE_PING = 0x8000 + 1;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private const uint WS_POPUP = 0x80000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hwnd);

    private readonly WndProcDelegate _wndProcDelegate; // 必须保引用，否则 GC 后回调会崩

    private IntPtr _hwnd = IntPtr.Zero;
    private bool _registered;
    private int _messageCount;
    private int _pingCount;
    private int _lastFormatUpdateError;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public event Action? ClipboardUpdated;

    public ClipboardListener()
    {
        _wndProcDelegate = WndProc;
    }

    /// <summary>诊断：窗口句柄是否有效。</summary>
    public IntPtr WindowHandle => _hwnd;

    /// <summary>诊断：收到的 WM_CLIPBOARDUPDATE 次数（未做任何过滤）。</summary>
    public int MessageCount => _messageCount;

    /// <summary>诊断：收到的自投 ping 次数。</summary>
    public int PingCount => _pingCount;

    /// <summary>诊断：AddClipboardFormatListener 失败时的 Win32 错误码。</summary>
    public int LastError => _lastFormatUpdateError;

    /// <summary>诊断：注册监听时所在线程的 ID（必须和消息循环线程一致）。</summary>
    public int ThreadId { get; private set; }

    /// <summary>建立隐藏消息窗口并注册剪贴板监听。返回 false 表示系统拒绝。</summary>
    public bool Start()
    {
        ThreadId = Environment.CurrentManagedThreadId;

        _hwnd = CreateWindowExW(
            dwExStyle: 0,
            lpClassName: "STATIC",
            lpWindowName: "ClipboardProbe.MessageWindow",
            dwStyle: WS_POPUP,
            x: 0, y: 0, nWidth: 0, nHeight: 0,
            hWndParent: HWND_MESSAGE,
            hMenu: IntPtr.Zero,
            hInstance: IntPtr.Zero,
            lpParam: IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _lastFormatUpdateError = Marshal.GetLastWin32Error();
            return false;
        }

        // 子类化这个窗口，把 WndProc 换掉
        SetWindowLongPtrCompat(_hwnd, -4 /* GWLP_WNDPROC */, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

        _registered = AddClipboardFormatListener(_hwnd);
        if (!_registered)
        {
            _lastFormatUpdateError = Marshal.GetLastWin32Error();
        }

        return _registered;
    }

    /// <summary>诊断：给自己投一条消息，验证窗口 + 消息循环是否真的在工作。</summary>
    public bool SendTestMessage() => PostMessageW(_hwnd, WM_PROBE_PING, IntPtr.Zero, IntPtr.Zero);

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CLIPBOARDUPDATE:
                Interlocked.Increment(ref _messageCount);
                try
                {
                    ClipboardUpdated?.Invoke();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[探针] 回调内未捕获异常：{ex.Message}");
                }

                return IntPtr.Zero;

            case WM_PROBE_PING:
                Interlocked.Increment(ref _pingCount);
                return IntPtr.Zero;

            case WM_DESTROY:
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // 32/64 位都要能用：64 位用 SetWindowLongPtrW，32 位回退到 SetWindowLongW
    private static IntPtr SetWindowLongPtrCompat(IntPtr hwnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8)
        {
            return SetWindowLongPtrW(hwnd, index, value);
        }

        return new IntPtr(SetWindowLongW(hwnd, index, value.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);

    public void Dispose()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
            _registered = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
