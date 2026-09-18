namespace Toolbox.Core;

/// <summary>
/// 剪贴板变更监听：AddClipboardFormatListener + 一个隐藏的消息窗口。
///
/// ★ 这个文件是从 P0 探针原样搬过来的，不要重写。★
/// 每一行分支都是踩坑换来的（DECISIONS.md 坑 1 / 坑 2）：
///   - 必须用显式 CreateWindowExW 建窗口（MessageWindow 里做了），
///     用 WinForms 的 NativeWindow 封装会「注册成功但一条通知都收不到」；
///   - 回调里必须全包 try/catch，异常逃出去会打死整条消息循环线程，
///     之后所有剪贴板事件静默消失，伪装成「监听失效」。
///
/// 线程约定：必须在 STA 线程上 Start()，且该线程要跑消息循环。
/// </summary>
internal sealed class ClipboardListener : IDisposable
{
    private readonly MessageWindow _window;
    private bool _registered;
    private int _messageCount;
    private int _pingCount;
    private int _lastError;

    public ClipboardListener()
    {
        _window = new MessageWindow("Toolbox.Clipboard.MessageWindow", OnMessage);
    }

    public event Action? ClipboardUpdated;

    /// <summary>诊断：收到的 WM_CLIPBOARDUPDATE 次数（未做任何过滤）。</summary>
    public int MessageCount => _messageCount;

    /// <summary>诊断：收到的自投 ping 次数。</summary>
    public int PingCount => _pingCount;

    /// <summary>诊断：AddClipboardFormatListener 失败时的 Win32 错误码。</summary>
    public int LastError => _lastError;

    public bool IsRegistered => _registered;

    /// <summary>建立隐藏消息窗口并注册剪贴板监听。返回 false 表示系统拒绝。</summary>
    public bool Start()
    {
        _registered = NativeMethods.AddClipboardFormatListener(_window.Handle);
        if (!_registered)
        {
            _lastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }

        return _registered;
    }

    /// <summary>
    /// 诊断：给自己投一条消息，验证「窗口 + 消息循环」是否真的在工作。
    /// 收得到 → 问题在别处；收不到 → 消息循环本身有问题，与剪贴板无关。
    /// </summary>
    public bool SendTestMessage()
        => NativeMethods.PostMessageW(_window.Handle, NativeMethods.WM_PING, IntPtr.Zero, IntPtr.Zero);

    private bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_CLIPBOARDUPDATE:
                Interlocked.Increment(ref _messageCount);
                try
                {
                    ClipboardUpdated?.Invoke();
                }
                catch (Exception ex)
                {
                    // 坑 2：这里绝不能把异常放出去
                    Log.Exception("剪贴板回调内未捕获异常", ex);
                }

                return true;

            case NativeMethods.WM_PING:
                Interlocked.Increment(ref _pingCount);
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_registered)
        {
            try
            {
                NativeMethods.RemoveClipboardFormatListener(_window.Handle);
            }
            catch
            {
                // 退出路径
            }

            _registered = false;
        }

        _window.Dispose();
    }
}
