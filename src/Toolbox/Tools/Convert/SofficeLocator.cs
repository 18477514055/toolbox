using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace Toolbox.Tools.Convert;

/// <summary>
/// 找 soffice.exe。它**没有进 PATH**（实测），所以必须按安装目录去找。
/// 找不到时上层要给出明确的中文提示，而不是让用户对着"转换失败"发呆。
/// </summary>
internal static class SofficeLocator
{
    private static string? _cached;

    public static string? Find(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            _cached = configured;
            return configured;
        }

        if (_cached is not null && File.Exists(_cached))
        {
            return _cached;
        }

        foreach (var candidate in Candidates())
        {
            try
            {
                if (File.Exists(candidate))
                {
                    _cached = candidate;
                    return candidate;
                }
            }
            catch
            {
                // 路径非法就跳过
            }
        }

        // 最后碰碰运气：PATH 里有没有
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim(), "soffice.exe");
                if (File.Exists(candidate))
                {
                    _cached = candidate;
                    return candidate;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        yield return @"C:\Program Files\LibreOffice\program\soffice.exe";
        yield return @"C:\Program Files (x86)\LibreOffice\program\soffice.exe";

        foreach (var drive in new[] { "C:", "D:", "E:" })
        {
            yield return $@"{drive}\LibreOffice\program\soffice.exe";
            yield return $@"{drive}\Program Files\LibreOffice\program\soffice.exe";
        }
    }

    /// <summary>
    /// LibreOffice 的独立用户配置目录（纯 ASCII 路径）。
    ///
    /// 为什么要单独给一个：默认配置目录是共享的，用户自己开着 LibreOffice 时，
    /// 我们的无头实例会因为"用户配置被锁"直接失败。给个独立 profile 就能并存。
    /// 路径刻意放在 Temp 下并保证全 ASCII —— LibreOffice 对 file:// URL 里的中文很敏感。
    /// </summary>
    public static string ProfileDir
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "toolbox-lo-profile");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // 建不出来就让 LibreOffice 用默认的
            }

            return dir;
        }
    }
}
