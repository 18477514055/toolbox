using System.IO;
using System.Text;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace Toolbox.Core;

/// <summary>
/// 极简日志：控制台（如果有）+ 文件双写。
///
/// 沿用 P0 探针的结论（DECISIONS.md 坑 3）：
///   - 输出绝不能因为控制台不可用而中断业务逻辑；
///   - 常驻程序的日志必须落到文件，因为用户根本看不到控制台。
/// 桌面程序是 WinExe，通常没有控制台，所以这里的 Console 分支基本走不到——
/// 保留它是为了 `--selftest` 这种带控制台的场景。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _consoleBroken;
    private static bool _hasConsole;

    /// <summary>单文件上限，超了就轮转一次，避免日志把磁盘吃满。</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

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
        Write("[错误] " + message);
    }

    public static void Exception(string context, Exception ex)
    {
        // ★ 记**完整堆栈**，不只记 Message。
        //
        //   为什么（实测吃过亏）：原来只写 `类型: 消息`，于是
        //   `NullReferenceException: Object reference not set...`
        //   这种**毫无信息量的**异常在日志里查不出任何线索 ——
        //   NullReference 必须知道"哪一行"才能修，只给消息等于没记。
        //   堆栈多占几行日志，换来的是"一眼定位"，非常划算。
        Write($"[异常] {context} → {ex.GetType().Name}: {ex.Message}");

        try
        {
            WriteTrace(ex.StackTrace, "    ");

            // 内部异常往往才是真凶（外层多半只是包装）
            var inner = ex.InnerException;
            var depth = 0;

            while (inner is not null && depth < 5)
            {
                Write($"    → 内部异常：{inner.GetType().Name}: {inner.Message}");
                WriteTrace(inner.StackTrace, "        ");

                inner = inner.InnerException;
                depth++;
            }
        }
        catch
        {
            // 记日志本身绝不能抛异常（否则会把真正的错误盖掉）
        }
    }

    /// <summary>把堆栈逐行写进日志（带缩进）。</summary>
    private static void WriteTrace(string? stackTrace, string indent)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return;
        }

        foreach (var line in stackTrace.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length > 0)
            {
                Write($"{indent}{trimmed}");
            }
        }
    }

    private static void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

        if (!_consoleBroken)
        {
            try
            {
                // WinExe 没有控制台时 Console.Out 是 TextWriter.Null，写进去是安全的；
                // 但被重定向后读者消失会抛 IOException，所以还是要接住。
                Console.WriteLine(line);
                _hasConsole = true;
            }
            catch (IOException)
            {
                _consoleBroken = true;
            }
            catch (ObjectDisposedException)
            {
                _consoleBroken = true;
            }
            catch
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
                RotateIfNeeded();
                File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志失败绝不能影响业务
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (_path is null || !File.Exists(_path))
            {
                return;
            }

            if (new FileInfo(_path).Length < MaxBytes)
            {
                return;
            }

            var old = _path + ".1";
            if (File.Exists(old))
            {
                File.Delete(old);
            }

            File.Move(_path, old);
        }
        catch
        {
            // 轮转失败就继续往原文件写，不能因此丢日志
        }
    }

    /// <summary>自检用：确认日志到底有没有落到文件。</summary>
    public static string? CurrentPath => _path;

    public static bool ConsoleAvailable => _hasConsole && !_consoleBroken;
}
