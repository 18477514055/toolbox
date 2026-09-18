using System.Windows.Input;
using Toolbox.Core;

namespace Toolbox.Shell;

/// <summary>
/// 全局热键。
///
/// ⚠️ 关键行为：RegisterHotKey 是**独占**的。被别的程序占了就注册不上，
/// 这时**必须给出明确提示**，绝不能静默失效——用户会以为"热键坏了"，然后一直查不出来。
/// 所以 Register 返回具体错误原因，上层负责显示出来。
///
/// 窗口建在 WPF 的 UI 线程上：WPF 的 Dispatcher 本身就在跑消息循环，
/// WM_HOTKEY 会被正常投递过来，不需要另开线程。
/// </summary>
internal sealed class HotKeyManager : IDisposable
{
    private readonly MessageWindow _window;
    private readonly Dictionary<int, string> _idToTool = new();
    private readonly Dictionary<string, int> _toolToId = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId = 1;

    public HotKeyManager()
    {
        _window = new MessageWindow("Toolbox.HotKey.MessageWindow", OnMessage);
    }

    /// <summary>热键被按下，参数是工具 Id。</summary>
    public event Action<string>? HotKeyPressed;

    /// <summary>注册结果，用于给用户明确提示。</summary>
    internal sealed record RegistrationResult(bool Success, string Spec, string? Error);

    public RegistrationResult Register(string toolId, string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return new RegistrationResult(false, spec, "热键为空");
        }

        if (!TryParse(spec, out var modifiers, out var vk, out var parseError))
        {
            return new RegistrationResult(false, spec, parseError);
        }

        // 同一个工具重复注册：先撤掉旧的
        Unregister(toolId);

        var id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_window.Handle, id,
                modifiers | NativeMethods.MOD_NOREPEAT, vk))
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            var hint = err switch
            {
                1409 => "已被别的程序占用",
                5 => "拒绝访问（可能被系统或安全软件拦截）",
                _ => $"Win32 错误码 {err}",
            };

            return new RegistrationResult(false, spec, $"注册失败：{hint}");
        }

        _idToTool[id] = toolId;
        _toolToId[toolId] = id;
        return new RegistrationResult(true, spec, null);
    }

    public void Unregister(string toolId)
    {
        if (_toolToId.Remove(toolId, out var id))
        {
            NativeMethods.UnregisterHotKey(_window.Handle, id);
            _idToTool.Remove(id);
        }
    }

    private bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != NativeMethods.WM_HOTKEY)
        {
            return false;
        }

        var id = wParam.ToInt32();
        if (_idToTool.TryGetValue(id, out var toolId))
        {
            try
            {
                HotKeyPressed?.Invoke(toolId);
            }
            catch (Exception ex)
            {
                Log.Exception($"热键处理出错（{toolId}）", ex);
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- 解析

    /// <summary>把 "Win+Shift+V" 解析成 (修饰键, 虚拟键码)。</summary>
    public static bool TryParse(string spec, out uint modifiers, out uint vk, out string? error)
    {
        modifiers = 0;
        vk = 0;
        error = null;

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "热键为空";
            return false;
        }

        var keyName = "";
        foreach (var raw in parts)
        {
            var part = raw.ToLowerInvariant();
            switch (part)
            {
                case "win" or "windows" or "super" or "meta":
                    modifiers |= NativeMethods.MOD_WIN;
                    continue;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    continue;
                case "ctrl" or "control":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    continue;
                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    continue;
            }

            // 不是修饰键，那它只能是主键 —— 而且一个热键只能有一个主键。
            //
            // 这里必须报错而不是让后者覆盖前者：早先的写法是 `keyName = raw`，
            // 于是 "Ctr+V"（Ctrl 打错）会静默变成「不带任何修饰键的 V」，
            // 用户以为设了 Ctrl+V，实际拿到裸 V —— 这种错误必须当场说出来。
            if (keyName.Length > 0)
            {
                error = $"认不出这个修饰键：{keyName}（一个热键只能有一个主键）";
                return false;
            }

            keyName = raw;
        }

        if (string.IsNullOrEmpty(keyName))
        {
            error = "热键缺少主键（例如 Win+Shift+V 里的 V）";
            return false;
        }

        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out var key))
        {
            error = $"认不出这个键：{keyName}";
            return false;
        }

        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            error = $"这个键不能作为热键：{keyName}";
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        foreach (var id in _idToTool.Keys.ToList())
        {
            NativeMethods.UnregisterHotKey(_window.Handle, id);
        }

        _idToTool.Clear();
        _toolToId.Clear();
        _window.Dispose();
    }
}
