using System.Runtime.InteropServices;

namespace Toolbox.Core;

/// <summary>
/// 一条常驻的 STA 线程 + 消息循环。
///
/// 为什么剪贴板监听要独占一条线程，而不是挂在 WPF 的 UI 线程上：
/// 读取剪贴板带重试（延迟渲染时最长会等 60+120+240ms），
/// 挂在 UI 线程上会让界面在这段时间里卡住。探针当初就是这么做的，实测没问题，沿用它。
/// </summary>
internal sealed class MessageThread : IDisposable
{
    private readonly string _name;
    private readonly Action<MessageThread> _onReady;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private Exception? _startupFailure;
    private uint _threadId;

    public MessageThread(string name, Action<MessageThread> onReady)
    {
        _name = name;
        _onReady = onReady;
    }

    public MessageWindow Window { get; private set; } = null!;

    public uint ThreadId => _threadId;

    /// <summary>消息循环线程是否还活着。坑 2 的自查断言就是看这个。</summary>
    public bool IsAlive => _thread is { IsAlive: true };

    public void Start()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = _name,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(10));

        if (_startupFailure is not null)
        {
            throw new InvalidOperationException($"消息线程启动失败：{_startupFailure.Message}", _startupFailure);
        }
    }

    private void Run()
    {
        try
        {
            _threadId = (uint)Environment.CurrentManagedThreadId;
            Window = new MessageWindow(_name, OnMessage);
            _onReady(this);
        }
        catch (Exception ex)
        {
            _startupFailure = ex;
            _ready.Set();
            return;
        }

        _ready.Set();

        // 标准 GetMessage 循环。窗口销毁后收到 WM_QUIT 自然退出。
        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
    }

    /// <summary>
    /// 自定义「投递一段代码到本线程执行」的消息。
    /// 用 WM_APP + 100 而不是 WM_APP + 1，是为了避开 NativeMethods 里已占用的
    /// WM_PING（WM_APP + 1）——诊断用的 ping 和业务用的 run-action 不能撞车。
    /// </summary>
    private const int WM_RUN_ACTION = NativeMethods.WM_APP + 100;

    private bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_DESTROY)
        {
            NativeMethods.PostQuitMessage(0);
            return true;
        }

        if (msg == WM_RUN_ACTION)
        {
            // wParam 是一个 GCHandle，指向要执行的 Action。
            // 无论执行成功与否都必须 Free，否则每投递一次就泄漏一个句柄。
            var handle = GCHandle.FromIntPtr(wParam);
            try
            {
                (handle.Target as Action)?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Exception($"MessageThread[{_name}].Post 回调", ex);
            }
            finally
            {
                handle.Free();
            }

            return true;
        }

        return HandleMessage?.Invoke(msg, wParam, lParam) ?? false;
    }

    /// <summary>业务消息回调（在本线程上执行）。返回 true 表示已处理。</summary>
    public Func<uint, IntPtr, IntPtr, bool>? HandleMessage { get; set; }

    /// <summary>把工作投递到这条线程上执行。</summary>
    public void Post(Action action)
    {
        if (Window is null)
        {
            return;
        }

        var handle = GCHandle.Alloc(action);
        if (!NativeMethods.PostMessageW(Window.Handle, WM_RUN_ACTION,
                GCHandle.ToIntPtr(handle), IntPtr.Zero))
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        try
        {
            Window?.Dispose();
        }
        catch
        {
            // 退出路径
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
