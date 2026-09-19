using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace Toolbox.Tools.Convert;

public static class FileNaming
{
    /// <summary>
    /// 永不覆盖：目标已存在就加 (1)、(2)…
    /// 这条是硬要求（交接文档 §五·工具3 验收标准 5），也适用于"输出目录恰好等于源目录"的情况。
    /// </summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    /// <summary>转换用的临时目录（纯 ASCII，避免中文路径跨进程出问题）。</summary>
    public static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "toolbox-convert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void SafeDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // 临时目录删不掉无所谓，系统会清
        }
    }
}
