using System.IO;
using System.IO.Compression;
using Toolbox.Core;
using File = System.IO.File;
using Directory = System.IO.Directory;
using Path = System.IO.Path;

namespace Toolbox.Tools.Archiver;

/// <summary>压缩格式。</summary>
internal enum ArchiveFormat
{
    Zip,
    Rar,
}

/// <summary>压缩包放哪里。</summary>
internal enum ArchiveDestination
{
    /// <summary>和被压缩的文件放在同一个目录。</summary>
    SameFolder,

    /// <summary>放到设置里指定的输出目录。</summary>
    ConfiguredFolder,
}

/// <summary>一次压缩的结果。</summary>
internal sealed record ArchiveResult(bool Success, string OutputPath, long Bytes, long ElapsedMs, string? Error)
{
    public static ArchiveResult Fail(string error) => new(false, "", 0, 0, error);
}

/// <summary>
/// 打包成压缩包。
///
/// ⚠️ 两种格式的**性质完全不同**，这一点必须如实告诉用户：
///
///   **ZIP** —— 零依赖。用 .NET 自带的 <c>System.IO.Compression</c> 直接写，
///     任何 Windows 都能用，不需要装任何东西。
///
///   **RAR** —— **专有格式，Windows 不自带**。要建 RAR 必须装 WinRAR
///     （`Rar.exe` 的命令行版本）。这是**许可证限制**，不是技术问题：
///     RAR 的压缩算法受专利/许可保护，任何人都不能合法地把"建 RAR"的能力
///     打进自己的软件里分发。所以本工具只能**调用用户自己装的 WinRAR**。
///
///   ⇒ 找不到 WinRAR 时，界面上会**明确说明原因**并建议改用 ZIP，
///     而不是给一句"压缩失败"让人猜。
///
/// 依赖分层（与项目其它部分一致的思路）：
///   · ZIP：零依赖，所有人都能用；
///   · RAR：可选增强，缺了只影响这一种格式。
/// </summary>
internal static class ArchiveService
{
    /// <summary>WinRAR 命令行的常见安装位置。</summary>
    private static readonly string[] RarCandidates =
    {
        @"C:\Program Files\WinRAR\Rar.exe",
        @"C:\Program Files (x86)\WinRAR\Rar.exe",
        @"D:\Program Files\WinRAR\Rar.exe",
        @"D:\WinRAR\Rar.exe",
    };

    /// <summary>找 WinRAR 的 Rar.exe。找不到返回 null。</summary>
    public static string? FindRar()
    {
        // ① 常见安装位置
        foreach (var p in RarCandidates)
        {
            try
            {
                if (File.Exists(p))
                {
                    return p;
                }
            }
            catch
            {
                // 路径非法就跳过
            }
        }

        // ② PATH 里碰运气（有人会把 WinRAR 目录加进 PATH）
        try
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                         .Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "Rar.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // 跳过
                }
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    /// <summary>
    /// 这种格式现在能不能用。
    /// 不可用时 <paramref name="reason"/> 给出**中文原因 + 怎么办**（可直接显示）。
    /// </summary>
    public static bool IsAvailable(ArchiveFormat format, out string? reason)
    {
        reason = null;

        if (format == ArchiveFormat.Zip)
        {
            return true;   // .NET 自带，永远可用
        }

        var rar = FindRar();
        if (rar is not null)
        {
            return true;
        }

        reason = "系统里没找到 WinRAR，所以建不了 RAR 压缩包。\n\n"
                 + "原因是：**RAR 是专有格式**，它的压缩算法受许可保护，\n"
                 + "任何人都不能合法地把「建 RAR」的能力打进自己的软件里分发 ——\n"
                 + "所以这不是技术问题，是许可证限制。\n\n"
                 + "两个选择：\n"
                 + "  · 改用 **ZIP**（零依赖，本工具自己就能建，推荐）；\n"
                 + "  · 或者自己装一个 WinRAR，本工具会自动找到它。";

        return false;
    }

    /// <summary>
    /// 决定压缩包放哪、叫什么名。
    ///
    /// 命名规则（都是"别覆盖已有文件"的硬要求）：
    ///   · 单个文件夹  → 用文件夹名
    ///   · 单个文件    → 用文件名（不含扩展名）
    ///   · 多个条目    → 用它们所在目录名；跨目录时用「打包_N 项」
    /// </summary>
    public static string BuildOutputPath(
        IReadOnlyList<string> sources,
        ArchiveFormat format,
        ArchiveDestination destination,
        string? configuredDir,
        DateTime now)
    {
        var ext = format == ArchiveFormat.Zip ? ".zip" : ".rar";

        string dir;
        if (destination == ArchiveDestination.ConfiguredFolder
            && !string.IsNullOrWhiteSpace(configuredDir))
        {
            dir = configuredDir!;
        }
        else
        {
            // 默认：和被压缩的东西放一起
            dir = GuessBaseDirectory(sources);
        }

        var name = GuessArchiveName(sources);
        var stamp = now.ToString("yyyyMMdd_HHmmss");

        var full = Path.Combine(dir, $"{name}_{stamp}{ext}");

        // 同一秒内重复打包会撞名 —— 复用转换工具那套"永不覆盖"的命名
        return Toolbox.Tools.Convert.FileNaming.UniquePath(full);
    }

    private static string GuessBaseDirectory(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0)
        {
            return AppPaths.Root;
        }

        var first = sources[0];

        // 单个文件夹：放在它的**父目录**（不是它自己里面 —— 那样会把它自己也压进去）
        if (Directory.Exists(first))
        {
            return Path.GetDirectoryName(first.TrimEnd('\\', '/')) ?? AppPaths.Root;
        }

        return Path.GetDirectoryName(first) ?? AppPaths.Root;
    }

    private static string GuessArchiveName(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0)
        {
            return "打包";
        }

        if (sources.Count == 1)
        {
            var one = sources[0].TrimEnd('\\', '/');

            if (Directory.Exists(one))
            {
                return Sanitize(Path.GetFileName(one));
            }

            return Sanitize(Path.GetFileNameWithoutExtension(one));
        }

        // 多个：若都在同一目录，用目录名；否则用「打包_N 项」
        try
        {
            var dirs = sources
                .Select(s => Directory.Exists(s)
                    ? Path.GetDirectoryName(s.TrimEnd('\\', '/')) ?? ""
                    : Path.GetDirectoryName(s) ?? "")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (dirs.Count == 1 && !string.IsNullOrEmpty(dirs[0]))
            {
                return Sanitize(Path.GetFileName(dirs[0].TrimEnd('\\', '/')));
            }
        }
        catch
        {
            // 落到下面的兜底
        }

        return $"打包_{sources.Count}项";
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "打包";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim();
    }

    /// <summary>
    /// 执行打包。
    /// </summary>
    /// <param name="sources">要打包的文件/文件夹（完整路径）。</param>
    /// <param name="outputPath">目标压缩包完整路径。</param>
    public static async Task<ArchiveResult> CreateAsync(
        IReadOnlyList<string> sources,
        ArchiveFormat format,
        string outputPath,
        CancellationToken ct)
    {
        if (sources.Count == 0)
        {
            return ArchiveResult.Fail("没有要打包的文件。");
        }

        // 逐个检查存在性 —— 半路才发现某个文件没了，比一开始就说清楚糟糕得多
        var missing = sources.Where(s => !File.Exists(s) && !Directory.Exists(s)).ToList();
        if (missing.Count > 0)
        {
            return ArchiveResult.Fail(
                $"有 {missing.Count} 个条目已经找不到了：\n"
                + string.Join("\n", missing.Take(3).Select(Path.GetFileName)));
        }

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch (Exception ex)
        {
            return ArchiveResult.Fail($"创建输出目录失败：{ex.Message}");
        }

        return format == ArchiveFormat.Zip
            ? await CreateZipAsync(sources, outputPath, ct)
            : await CreateRarAsync(sources, outputPath, ct);
    }

    // ---------------------------------------------------------------- ZIP

    /// <summary>
    /// 建 ZIP。**零依赖**，用 .NET 自带的 <c>System.IO.Compression</c>。
    /// </summary>
    private static async Task<ArchiveResult> CreateZipAsync(
        IReadOnlyList<string> sources, string outputPath, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // FileMode.CreateNew：**绝不覆盖**。文件名已由 BuildOutputPath 保证唯一，
            // 这里再用 CreateNew 兜一道 —— 万一那一层被绕过，宁可报错也不覆盖用户文件。
            await using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

            var added = 0;

            foreach (var src in sources)
            {
                ct.ThrowIfCancellationRequested();

                if (Directory.Exists(src))
                {
                    added += await AddDirectoryToZipAsync(zip, src, ct);
                }
                else if (File.Exists(src))
                {
                    await AddFileToZipAsync(zip, src, Path.GetFileName(src), ct);
                    added++;
                }
            }

            if (added == 0)
            {
                // 空压缩包没意义，删掉它别留垃圾
                zip.Dispose();
                stream.Dispose();
                TryDelete(outputPath);
                return ArchiveResult.Fail("这些条目里没有可打包的内容（可能是空文件夹）。");
            }

            sw.Stop();
            var size = new FileInfo(outputPath).Length;

            Log.Line($"ZIP 打包完成：{added} 个条目 → {outputPath}（{size} 字节，{sw.ElapsedMilliseconds} ms）");

            return new ArchiveResult(true, outputPath, size, sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            return ArchiveResult.Fail("已取消。");
        }
        catch (Exception ex)
        {
            Log.Exception("建 ZIP 失败", ex);
            TryDelete(outputPath);
            return ArchiveResult.Fail($"打包失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把一个目录（含子目录）加进 zip。
    /// 用**相对路径**存条目名 —— 用绝对路径的话，解压出来会带一长串目录结构。
    /// </summary>
    private static async Task<int> AddDirectoryToZipAsync(ZipArchive zip, string dir, CancellationToken ct)
    {
        var rootName = Path.GetFileName(dir.TrimEnd('\\', '/'));
        var count = 0;

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            // 算相对路径作为 zip 内的条目名
            var relative = Path.GetRelativePath(dir, file);
            var entryName = Path.Combine(rootName, relative).Replace('\\', '/');

            await AddFileToZipAsync(zip, file, entryName, ct);
            count++;
        }

        // 空目录也要留一个条目，否则解压后目录结构就丢了
        if (count == 0)
        {
            zip.CreateEntry(rootName + "/");
            count++;
        }

        return count;
    }

    private static async Task AddFileToZipAsync(
        ZipArchive zip, string file, string entryName, CancellationToken ct)
    {
        var entry = zip.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);

        await using var entryStream = entry.Open();
        await using var source = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            81920, FileOptions.SequentialScan | FileOptions.Asynchronous);

        await source.CopyToAsync(entryStream, ct);
    }

    // ---------------------------------------------------------------- RAR

    /// <summary>
    /// 建 RAR —— **调用用户自己装的 WinRAR**（见类注释里的许可证说明）。
    /// </summary>
    private static async Task<ArchiveResult> CreateRarAsync(
        IReadOnlyList<string> sources, string outputPath, CancellationToken ct)
    {
        var rar = FindRar();
        if (rar is null)
        {
            IsAvailable(ArchiveFormat.Rar, out var reason);
            return ArchiveResult.Fail(reason ?? "找不到 WinRAR，无法建 RAR。");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = rar,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 工作目录设成输出目录：这样压缩包里的条目是**相对路径**，
                // 解压出来不会带一长串绝对路径
                WorkingDirectory = Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory,
            };

            // a = 添加  -y = 全部确认（不问）  -ep1 = 不保存最外层路径前缀
            //
            // ⚠️ 用 ArgumentList 而不是手拼字符串：路径里有空格/中文时手拼几乎必错
            //    （项目在格式转换那边已经踩过同一个坑）。
            psi.ArgumentList.Add("a");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ep1");

            // 用文件名（相对 WorkinDirectory）而不是绝对路径 —— 配合 -ep1 得到干净的条目名
            psi.ArgumentList.Add(Path.GetFileName(outputPath));

            foreach (var s in sources)
            {
                psi.ArgumentList.Add(Path.GetFileName(s.TrimEnd('\\', '/')));
            }

            using var process = new System.Diagnostics.Process { StartInfo = psi };

            if (!process.Start())
            {
                return ArchiveResult.Fail("无法启动 WinRAR。");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } } catch { }
                TryDelete(outputPath);
                return ArchiveResult.Fail(ct.IsCancellationRequested
                    ? "已取消。"
                    : "WinRAR 执行超时。");
            }

            var stdout = await stdoutTask;
            sw.Stop();

            if (!File.Exists(outputPath))
            {
                // 产物没出来就一定有问题，把 WinRAR 的输出原样带上帮用户判断
                var detail = string.IsNullOrWhiteSpace(stdout) ? "" : "\n\nWinRAR 输出：\n" + stdout.Trim();
                return ArchiveResult.Fail($"WinRAR 没有生成压缩包（退出码 {process.ExitCode}）。{detail}");
            }

            var size = new FileInfo(outputPath).Length;
            Log.Line($"RAR 打包完成：{sources.Count} 个条目 → {outputPath}（{size} 字节，{sw.ElapsedMilliseconds} ms）");

            return new ArchiveResult(true, outputPath, size, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            Log.Exception("建 RAR 失败", ex);
            TryDelete(outputPath);
            return ArchiveResult.Fail($"打包失败：{ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就算了，别掩盖真正的错误
        }
    }

    /// <summary>人看的体积格式。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) { return $"{bytes} B"; }
        if (bytes < 1024 * 1024) { return $"{bytes / 1024.0:F1} KB"; }
        if (bytes < 1024L * 1024 * 1024) { return $"{bytes / 1024.0 / 1024:F1} MB"; }
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }
}
