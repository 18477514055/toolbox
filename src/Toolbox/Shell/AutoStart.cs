using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Toolbox.Core;

namespace Toolbox.Shell;

/// <summary>
/// 开机自启：写 HKCU\...\CurrentVersion\Run。
///
/// 为什么选注册表 Run 键而不是启动文件夹快捷方式（DECISIONS.md §五·4）：
///   - 可写、易撤销（删一个值就干净了）；
///   - 不往用户的启动目录里塞文件，卸载时不会留垃圾。
///
/// 刻意**不引** Microsoft.Win32.Registry：本项目坚持零第三方依赖，
/// 而 advapi32 的这几个函数几十行就写完了，还少一个程序集引用。
/// </summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "桌面工具箱";

    /// <summary>
    /// 写进注册表时附带的参数，让程序知道"我是被开机自启拉起来的"。
    ///
    /// 有两个用处：
    ///   1. 走**延迟初始化** —— 避开开机瞬间对剪贴板服务 / 热键 / 消息窗口的资源争抢；
    ///   2. **不弹模态对话框** —— 开机时弹窗既打断用户，看起来又像"启动失败"，
    ///      自启模式下改用托盘气泡 + 日志。
    /// </summary>
    public const string StartupArgument = "--startup";

    private const int KEY_QUERY_VALUE = 0x0001;
    private const int KEY_SET_VALUE = 0x0002;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_FILE_NOT_FOUND = 2;

    private const int REG_SZ = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string subKey, int options, int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(IntPtr hKey, string valueName, int reserved, int type, byte[] data, int dataSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteValueW(IntPtr hKey, string valueName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(IntPtr hKey, string valueName, IntPtr reserved, out int type, byte[]? data, ref int dataSize);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);

    private static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));

    /// <summary>当前 exe 的完整路径。单文件发布时就是那个 exe 本身。</summary>
    public static string ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            var rc = RegOpenKeyExW(HKEY_CURRENT_USER, RunKeyPath, 0, KEY_QUERY_VALUE, out var key);
            if (rc != ERROR_SUCCESS)
            {
                return false;
            }

            try
            {
                var size = 0;
                var q = RegQueryValueExW(key, ValueName, IntPtr.Zero, out _, null, ref size);
                return q == ERROR_SUCCESS;
            }
            finally
            {
                RegCloseKey(key);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("查询开机自启状态失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 读出注册表里**实际写的那条命令**（原始字符串，含引号和参数）。
    /// 返回 null = 没开自启；error 非空 = 读的过程本身出错。
    ///
    /// 排障时这是最关键的一条信息：「注册表里到底指向哪个 exe」。
    /// 程序被移动过之后，自启失效的全部原因就在这个字符串上 ——
    /// 而在此之前，程序只会告诉你"已开启/未开启"，看不到它指向哪里。
    /// </summary>
    public static string? ReadRegisteredCommand(out string? error)
    {
        error = null;

        try
        {
            var rc = RegOpenKeyExW(HKEY_CURRENT_USER, RunKeyPath, 0, KEY_QUERY_VALUE, out var key);
            if (rc != ERROR_SUCCESS)
            {
                error = $"打不开注册表启动项（错误码 {rc}）。";
                return null;
            }

            try
            {
                var size = 0;
                var q = RegQueryValueExW(key, ValueName, IntPtr.Zero, out _, null, ref size);
                if (q == ERROR_FILE_NOT_FOUND)
                {
                    return null;
                }

                if (q != ERROR_SUCCESS || size <= 0)
                {
                    error = $"查询启动项失败（错误码 {q}）。";
                    return null;
                }

                var buffer = new byte[size];
                var read = size;
                var q2 = RegQueryValueExW(key, ValueName, IntPtr.Zero, out _, buffer, ref read);
                if (q2 != ERROR_SUCCESS)
                {
                    error = $"读取启动项失败（错误码 {q2}）。";
                    return null;
                }

                // REG_SZ 是 UTF-16，末尾带一个 NUL 结束符
                var text = Encoding.Unicode.GetString(buffer, 0, read).TrimEnd('\0');
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            finally
            {
                RegCloseKey(key);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("读取开机自启项失败", ex);
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 从注册表命令里剥出 exe 路径（去掉外层引号和后面的参数）。
    ///
    /// 两种写法都要认：
    ///   "C:\a b\app.exe" --startup   → 取引号内
    ///   C:\a b\app.exe --startup     → 没引号时取到第一个 .exe 为止
    /// </summary>
    public static string ExtractPath(string command)
    {
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 0 ? text[1..end] : text.Trim('"');
        }

        var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? text[..(exeIndex + 4)] : text;
    }

    /// <summary>注册表里那条命令指向的是不是当前这个 exe。</summary>
    public static bool PointsAtCurrentExe(string command)
    {
        var registered = ExtractPath(command);
        var current = ExecutablePath;
        if (string.IsNullOrEmpty(registered) || string.IsNullOrEmpty(current))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(registered),
                Path.GetFullPath(current),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 路径非法（比如带了引号没剥干净）→ 当作不一致，交给自愈去改写
            return false;
        }
    }

    /// <summary>
    /// 自愈：自启开着、但指向的不是当前这个 exe（程序被移动 / 改过目录名），
    /// 就顺手改写成当前路径。
    ///
    /// 为什么必须有（这正是"开机自启失效"最常见的原因）：
    ///   Run 键里存的是**绝对路径**。用户把文件夹挪个位置、改个名字，
    ///   自启就静默失效了 —— 而且因为开机时进程根本没起来，主日志里连痕迹都没有，
    ///   用户只能看到"没启动"，完全不知道该从哪儿查。
    ///   每次启动顺手校正一次，这个坑就永远不会出现。
    /// </summary>
    /// <returns>做了改写返回 true。</returns>
    public static bool EnsurePointsAtCurrentExe()
    {
        try
        {
            var registered = ReadRegisteredCommand(out _);
            if (registered is null)
            {
                return false; // 没开自启，不关我们的事
            }

            if (PointsAtCurrentExe(registered))
            {
                return false; // 已经对了
            }

            Log.Line($"自启路径自愈：注册表里是「{registered}」，当前程序是「{ExecutablePath}」，正在改写。");

            if (Set(true, out var error))
            {
                Log.Line("自启路径已校正为当前路径。");
                return true;
            }

            Log.Error($"自启路径自愈失败：{error}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Exception("自启路径自愈失败", ex);
            return false;
        }
    }

    public static bool Set(bool enabled, out string? error)
    {
        error = null;

        try
        {
            var rc = RegOpenKeyExW(HKEY_CURRENT_USER, RunKeyPath, 0, KEY_QUERY_VALUE | KEY_SET_VALUE, out var key);
            if (rc != ERROR_SUCCESS)
            {
                error = $"打不开注册表启动项（错误码 {rc}）。";
                return false;
            }

            try
            {
                if (!enabled)
                {
                    var del = RegDeleteValueW(key, ValueName);
                    if (del != ERROR_SUCCESS && del != ERROR_FILE_NOT_FOUND)
                    {
                        error = $"删除启动项失败（错误码 {del}）。";
                        return false;
                    }

                    Log.Line("开机自启已关闭。");
                    return true;
                }

                var exe = ExecutablePath;
                if (string.IsNullOrEmpty(exe))
                {
                    error = "拿不到程序自身的路径，无法设置开机自启。";
                    return false;
                }

                // 带引号：路径里可能有空格或中文。
                // 再带上 --startup，让程序知道这次是开机拉起来的（见 StartupArgument 的说明）。
                var command = $"\"{exe}\" {StartupArgument}";
                var bytes = Encoding.Unicode.GetBytes(command + "\0");

                var set = RegSetValueExW(key, ValueName, 0, REG_SZ, bytes, bytes.Length);
                if (set != ERROR_SUCCESS)
                {
                    error = $"写入启动项失败（错误码 {set}）。";
                    return false;
                }

                Log.Line($"开机自启已开启：{command}");
                return true;
            }
            finally
            {
                RegCloseKey(key);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("设置开机自启失败", ex);
            error = $"设置开机自启失败：{ex.Message}";
            return false;
        }
    }
}
