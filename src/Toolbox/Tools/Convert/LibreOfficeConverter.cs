using System.Diagnostics;
using System.IO;
using System.Text;
using Toolbox.Core;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace Toolbox.Tools.Convert;

/// <summary>
/// 文档转换的首选后端：LibreOffice 无头模式。
/// `soffice.exe --headless --convert-to <fmt> --outdir <dir> <file>` 一条命令搞定
/// docx / xlsx / pptx / odt / html → pdf / docx / txt / csv 等，不开 GUI、免费。
///
/// 四个坑，这里都处理了（交接文档 §五·工具3 风险）：
///   1. **并发冲突**：同一用户下多实例会抢用户配置锁 → 全局串行 + 独立 profile 目录。
///   2. **中文路径**：用 ProcessStartInfo.ArgumentList（.NET 自己拼命令行，全程 Unicode），
///      不经 shell，所以不会有编码 mangling；输出先落 ASCII 临时目录再搬回去。
///   3. **异步假成功**：--convert-to 是异步的，**必须等进程退出**再报成功，
///      而且要**确认输出文件真的存在**，否则会出现"报成功但文件没生成"。
///   4. **大文件卡队列**：每个转换都有超时，超时就杀掉，不让一个坏文件堵住整条队列。
/// </summary>
internal sealed class LibreOfficeConverter : IConverter
{
    /// <summary>全局串行闸门。soffice 多实例会冲突，宁可慢一点也不能互相踩。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly Func<string?> _sofficeProvider;

    public LibreOfficeConverter(Func<string?> sofficeProvider)
    {
        _sofficeProvider = sofficeProvider;
    }

    public string Name => "LibreOffice";

    /// <summary>单个文件的转换超时。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public bool IsAvailable(out string? reason)
    {
        var path = _sofficeProvider();
        if (string.IsNullOrEmpty(path))
        {
            reason = "没找到 LibreOffice（soffice.exe）。文档转换不可用；图片互转不受影响。"
                     + "装了 LibreOffice 之后可以在「设置」里手动指定它的路径。";
            return false;
        }

        reason = null;
        return true;
    }

    public bool CanConvert(string sourcePath, string targetFormat)
    {
        // soffice 读不了 PDF（LibreOffice 的 PDF 导入能力有限，实测确认），这条明确排除
        if (Formats.IsPdf(sourcePath))
        {
            return false;
        }

        if (Formats.IsImage(sourcePath))
        {
            return string.Equals(targetFormat, "pdf", StringComparison.OrdinalIgnoreCase);
        }

        return Formats.IsDocument(sourcePath);
    }

    public async Task<ConvertResult> ConvertAsync(
        string sourcePath,
        string targetFormat,
        string outputDir,
        CancellationToken ct)
    {
        var soffice = _sofficeProvider();
        if (string.IsNullOrEmpty(soffice))
        {
            return new ConvertResult(false, null,
                "没找到 LibreOffice，无法转换文档。装了之后在「设置」里指定路径即可。");
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        var tempDir = FileNaming.CreateTempDir();

        try
        {
            var args = new List<string>
            {
                // 独立 profile：用户自己开着 LibreOffice 也不会跟我们抢配置锁
                "-env:UserInstallation=" + new Uri(SofficeLocator.ProfileDir).AbsoluteUri,
                "--headless",
                "--norestore",
                "--nolockcheck",
                "--nodefault",
                "--convert-to", targetFormat.ToLowerInvariant(),
                "--outdir", tempDir,
                sourcePath,
            };

            var psi = new ProcessStartInfo
            {
                FileName = soffice,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(soffice) ?? tempDir,
            };

            // ★ 用 ArgumentList：.NET 负责正确加引号并保持 Unicode，
            //   不会像手拼字符串那样在中文路径上被编码搞坏
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            if (!process.Start())
            {
                return new ConvertResult(false, null, "启动 LibreOffice 失败。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 杀不掉也只能算了
                }

                return ct.IsCancellationRequested
                    ? new ConvertResult(false, null, "已取消")
                    : new ConvertResult(false, null, $"转换超时（超过 {Timeout.TotalMinutes:0} 分钟），已中止。");
            }

            // ★ 必须确认输出真的存在。soffice 有时返回 0 但什么都没生成。
            var produced = FindProduced(tempDir, sourcePath, targetFormat);
            if (produced is null)
            {
                var detail = FirstMeaningful(stderr.ToString(), stdout.ToString());
                return new ConvertResult(false, null,
                    "LibreOffice 跑完了但没有生成输出文件。"
                    + (string.IsNullOrEmpty(detail) ? "" : $"\n它说：{detail}"));
            }

            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            var ext = Path.GetExtension(produced);
            var finalPath = FileNaming.UniquePath(Path.Combine(outputDir, baseName + ext));

            // 跨盘移动要 Copy+Delete
            try
            {
                File.Move(produced, finalPath, overwrite: false);
            }
            catch (IOException)
            {
                File.Copy(produced, finalPath, overwrite: false);
                File.Delete(produced);
            }

            var size = new FileInfo(finalPath).Length;
            return new ConvertResult(true, finalPath, $"{size / 1024} KB");
        }
        catch (OperationCanceledException)
        {
            return new ConvertResult(false, null, "已取消");
        }
        catch (Exception ex)
        {
            Log.Exception($"LibreOffice 转换失败：{sourcePath}", ex);
            return new ConvertResult(false, null, $"转换失败：{ex.Message}");
        }
        finally
        {
            FileNaming.SafeDeleteDir(tempDir);
            Gate.Release();
        }
    }

    private static string? FindProduced(string tempDir, string sourcePath, string targetFormat)
    {
        try
        {
            var files = Directory.GetFiles(tempDir);
            if (files.Length == 0)
            {
                return null;
            }

            var wanted = Path.GetFileNameWithoutExtension(sourcePath);

            // 优先同名（LibreOffice 一般保留原文件名）
            var exact = files.FirstOrDefault(f =>
                string.Equals(Path.GetFileNameWithoutExtension(f), wanted, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            // 再按扩展名找
            var byExt = files.FirstOrDefault(f =>
                Path.GetExtension(f).TrimStart('.').Equals(targetFormat, StringComparison.OrdinalIgnoreCase));
            return byExt ?? files[0];
        }
        catch
        {
            return null;
        }
    }

    private static string FirstMeaningful(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            var line = c.Split('\n')
                .Select(s => s.Trim())
                .FirstOrDefault(s => s.Length > 0 && !s.Contains("javaldx", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(line))
            {
                return line.Length > 200 ? line[..200] + "…" : line;
            }
        }

        return "";
    }
}
