using System.IO;
using System.Text;

namespace ClipboardProbe;

/// <summary>
/// 极简日志：同时写控制台和一个日志文件。
///
/// 为什么不用 Console.WriteLine 了：探针是要挂一整天的，用户很可能把它放到后台跑
/// （重定向 stdin/stdout，stdin 立刻 EOF）。实测两个坑：
///   1) Console.ReadLine() 在无标准输入时立刻返回 null —— 探针会“秒退”，一条都抓不到；
///   2) stdout 被重定向后一旦读者消失，Console.WriteLine 会抛 IOException —— 直接打断事件处理。
/// 结论：运行控制不能依赖标准输入，日志必须能落到文件。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _consoleBroken;

    public static void Open(string path)
    {
        _path = path;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch
        {
            _path = null;
        }
    }

    public static void Line(string message = "")
    {
        Write(message);
    }

    public static void Error(string message)
    {
        Write(message);
    }

    private static void Write(string message)
    {
        if (!_consoleBroken)
        {
            try
            {
                Console.WriteLine(message);
            }
            catch (IOException)
            {
                _consoleBroken = true; // 管道断了就别再试
            }
            catch (ObjectDisposedException)
            {
                _consoleBroken = true;
            }
        }

        if (_path is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path, message + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志失败绝不能影响抓取
        }
    }
}
