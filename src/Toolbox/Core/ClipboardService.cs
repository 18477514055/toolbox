namespace Toolbox.Core;

/// <summary>
/// 剪贴板服务：把「监听 → 读取 → 判定来源 → 交给上层」串起来。
///
/// 监听跑在独占的 STA 线程上（见 MessageThread 的说明），
/// 读到的结果通过 <see cref="Captured"/> 事件抛给上层；上层自己负责切回 UI 线程。
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private readonly MessageThread _thread;
    private readonly object _gate = new();
    private ClipboardListener? _listener;
    private int _seq;

    public ClipboardService()
    {
        _thread = new MessageThread("Toolbox.ClipboardListener", OnThreadReady);
    }

    /// <summary>抓到一条剪贴板内容。**在监听线程上触发**，订阅方注意切线程。</summary>
    public event Action<ClipReadResult>? Captured;

    /// <summary>暂停监听（托盘菜单「暂停监听」）。暂停期间事件直接丢弃。</summary>
    public bool IsPaused { get; set; }

    /// <summary>诊断：收到的剪贴板通知总数。</summary>
    public int MessageCount => _listener?.MessageCount ?? 0;

    /// <summary>诊断：消息循环线程是否存活（坑 2 的核心断言）。</summary>
    public bool ListenerThreadAlive => _thread.IsAlive;

    public bool Start()
    {
        _thread.Start();
        return _listener?.IsRegistered ?? false;
    }

    private void OnThreadReady(MessageThread thread)
    {
        var listener = new ClipboardListener();
        if (!listener.Start())
        {
            throw new InvalidOperationException(
                $"AddClipboardFormatListener 失败（Win32 错误码 {listener.LastError}）。");
        }

        listener.ClipboardUpdated += OnClipboardUpdated;
        _listener = listener;

        // 自查：投一条自投消息，确认「窗口 + 消息循环」是活的。
        // 没有这一步，坑 1 会伪装成「监听失效」，能查一整天。
        listener.SendTestMessage();
    }

    private void OnClipboardUpdated()
    {
        try
        {
            if (IsPaused)
            {
                return;
            }

            int seq;
            lock (_gate)
            {
                seq = ++_seq;
            }

            var result = ClipboardReader.Read(seq);
            Captured?.Invoke(result);
        }
        catch (Exception ex)
        {
            // 兜底：绝不能让它冒到窗口过程外面去（坑 2）
            Log.Exception("处理剪贴板更新时出错", ex);
        }
    }

    /// <summary>自检用：直接读一次当前剪贴板，不依赖事件。</summary>
    public ClipReadResult ReadNow()
    {
        int seq;
        lock (_gate)
        {
            seq = ++_seq;
        }

        return ClipboardReader.Read(seq);
    }

    public void Dispose()
    {
        if (_listener is not null)
        {
            _listener.ClipboardUpdated -= OnClipboardUpdated;
            _listener.Dispose();
            _listener = null;
        }

        _thread.Dispose();
    }
}
