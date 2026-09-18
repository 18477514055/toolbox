using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;
using MemoryStream = System.IO.MemoryStream;
using WClipboard = System.Windows.Clipboard;

namespace Toolbox.Core;

/// <summary>
/// 读剪贴板。★ 读取顺序与重试逻辑来自 P0 探针，不要改。★
///
/// 两条铁律（DECISIONS.md §三）：
///   1. 读取顺序必须是 **文件 → 图片 → 文本**。
///      从资源管理器复制文件时，剪贴板里除了 CF_HDROP 还挂着 shell 的「延迟渲染缩略图」。
///      先读图片就会逼 shell 现场生成缩略图 —— 慢，大目录上可能卡住。
///   2. **绝不枚举全部格式**。GetFormats() 拿清单是安全的（不触发渲染），
///      但逐个 GetData 所有格式会触发全部延迟渲染，轻则慢，重则把源程序卡死。
/// </summary>
internal static class ClipboardReader
{
    private const int MaxTextPreview = 200;
    private const int ReadAttempts = 4;
    private const int FirstRetryDelayMs = 60;

    /// <summary>超过这个长度的正文不进索引，改外置到 volatile\ 目录。</summary>
    public const int LongTextThreshold = 10 * 1024;

    public static ClipReadResult Read(int seq)
    {
        var sw = Stopwatch.StartNew();
        var result = new ClipReadResult();
        var entry = result.Entry;
        entry.Seq = seq;
        entry.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        try
        {
            entry.Kind = ReadCore(result);
        }
        catch (Exception ex)
        {
            entry.Kind = ClipKind.Failed;
            entry.Error = $"{ex.GetType().Name}: {ex.Message}";
            Log.Exception("读取剪贴板失败", ex);
        }

        sw.Stop();
        entry.ReadMs = sw.ElapsedMilliseconds;

        // 「我们自己写的」标记：token 优先，哈希兜底（见 SelfWriteGuard）
        if (SelfWriteGuard.IsOurs(result.SelfToken, entry.Hash))
        {
            entry.Origin = "tool";
        }

        return result;
    }

    private static ClipKind ReadCore(ClipReadResult result)
    {
        var entry = result.Entry;
        var attempts = 1;

        var data = WithRetry(() => WClipboard.GetDataObject(), out var a1);
        attempts = Math.Max(attempts, a1);
        if (data is null)
        {
            entry.Attempts = attempts;
            entry.Error = "GetDataObject 返回 null";
            return ClipKind.Empty;
        }

        var formats = WithRetry(() => data.GetFormats(false), out var a2) ?? Array.Empty<string>();
        attempts = Math.Max(attempts, a2);
        entry.Formats = formats.ToList();

        // 先看有没有我们自己塞的标记（读取它不会触发任何延迟渲染）
        result.SelfToken = TryGetSelfToken(data);

        // ---- 1) 文件（CF_HDROP）----
        if (formats.Any(f => f.Equals("FileDrop", StringComparison.OrdinalIgnoreCase)
                          || f.Equals("FileNameW", StringComparison.OrdinalIgnoreCase)))
        {
            var list = WithRetry(() => WClipboard.GetFileDropList(), out var a3);
            attempts = Math.Max(attempts, a3);
            if (list is { Count: > 0 })
            {
                entry.Files = new List<ClipFile>();
                long total = 0;
                foreach (var path in list)
                {
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    var item = new ClipFile { Path = path };
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            item.IsDirectory = true;
                            item.Exists = true;
                        }
                        else if (File.Exists(path))
                        {
                            item.Exists = true;
                            item.Size = new FileInfo(path).Length;
                        }
                    }
                    catch
                    {
                        // 权限不足之类，就当读不到——不因此判失败
                    }

                    total += item.Size;
                    entry.Files.Add(item);
                }

                entry.Bytes = total;
                entry.Hash = SelfWriteGuard.HashPaths(entry.Files.Select(f => f.Path));
                entry.Attempts = attempts;
                result.FilePaths = entry.Files.Select(f => f.Path).ToArray();
                return ClipKind.Files;
            }
        }

        // ---- 2) 图片 ----
        var hasBitmap = formats.Any(f =>
            f.Contains("Bitmap", StringComparison.OrdinalIgnoreCase) ||
            f.Equals("PNG", StringComparison.OrdinalIgnoreCase) ||
            f.Equals("DeviceIndependentBitmap", StringComparison.OrdinalIgnoreCase));

        if (hasBitmap)
        {
            // ★ 坑 2 的关键点：某些来源（非 OLE 的 GDI 位图、损坏的 DIB、shell 缩略图）
            //   会让 Clipboard.GetImage() 直接抛 NullReferenceException —— 这是 .NET 内部实现的问题。
            //   必须在这里接住，异常逃出去会打死消息循环线程。
            BitmapSource? source = null;
            string? imageError = null;
            try
            {
                source = WithRetry(() => WClipboard.GetImage(), out var a4);
                attempts = Math.Max(attempts, a4);
            }
            catch (Exception ex)
            {
                imageError = $"{ex.GetType().Name}: {ex.Message}";
            }

            if (source is not null)
            {
                var png = EncodePng(source);
                result.PngBytes = png;
                result.Image = source;

                entry.Bytes = png.Length;
                entry.ImageWidth = source.PixelWidth;
                entry.ImageHeight = source.PixelHeight;
                entry.Hash = SelfWriteGuard.HashBytes(png);
                entry.Attempts = attempts;

                var textAlongside = TryGetText(formats, out var a4b);
                attempts = Math.Max(attempts, a4b);
                if (!string.IsNullOrEmpty(textAlongside))
                {
                    result.FullText = textAlongside;
                    entry.TextLength = textAlongside.Length;
                    entry.TextPreview = Truncate(textAlongside);
                    AddExtra(entry, "同时带文本");
                }

                entry.Attempts = attempts;
                return ClipKind.Image;
            }

            if (imageError is not null)
            {
                AddExtra(entry, "图片读取失败：" + imageError);
            }
        }

        // ---- 3) 文本 ----
        var plain = TryGetText(formats, out var a5);
        attempts = Math.Max(attempts, a5);
        if (!string.IsNullOrEmpty(plain))
        {
            var utf8 = Encoding.UTF8.GetBytes(plain);
            entry.Bytes = utf8.Length;
            entry.TextLength = plain.Length;
            entry.TextPreview = Truncate(plain);
            entry.Hash = SelfWriteGuard.HashBytes(utf8);
            entry.Attempts = attempts;
            result.FullText = plain;

            var html = TryGetCustom(data, "HTML Format");
            if (!string.IsNullOrEmpty(html))
            {
                result.Html = html;
                AddExtra(entry, "带 HTML 格式");
            }

            var rtf = TryGetCustom(data, "Rich Text Format");
            if (!string.IsNullOrEmpty(rtf))
            {
                result.Rtf = rtf;
                AddExtra(entry, "带 RTF 格式");
            }

            return ClipKind.Text;
        }

        // ---- 4) 没认出来 ----
        entry.Attempts = attempts;
        if (formats.Length == 0)
        {
            return ClipKind.Empty;
        }

        entry.Error = "有格式但都不是我们支持的（文本/图片/文件）";
        return ClipKind.Empty;
    }

    private static string? TryGetSelfToken(System.Windows.IDataObject data)
    {
        try
        {
            var raw = data.GetData(SelfWriteGuard.SelfWriteFormat);
            return raw switch
            {
                string s => s,
                MemoryStream ms => new StreamReader(ms, Encoding.UTF8).ReadToEnd(),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetText(string[] formats, out int attemptsUsed)
    {
        attemptsUsed = 1;
        if (!formats.Any(f => f.Contains("Text", StringComparison.OrdinalIgnoreCase)
                           || f.Equals("UnicodeText", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var text = WithRetry(() => WClipboard.GetText(), out var a);
        attemptsUsed = a;
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? TryGetCustom(System.Windows.IDataObject data, string formatPrefix)
    {
        try
        {
            foreach (var fmt in data.GetFormats(false))
            {
                if (!fmt.Contains(formatPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (data.GetData(fmt) is string s && !string.IsNullOrEmpty(s))
                {
                    return s;
                }
            }
        }
        catch
        {
            // 自定义格式读不到无所谓
        }

        return null;
    }

    /// <summary>
    /// 带重试的读取。延迟渲染（Office / 浏览器 / 部分编辑器）下第一次读常抛
    /// COMException 或返回空，退避几十毫秒再来一次基本就成。
    /// **重试次数高不是故障指标，是正常现象。**
    /// </summary>
    public static T? WithRetry<T>(Func<T?> action, out int attemptsUsed)
    {
        var delay = FirstRetryDelayMs;
        Exception? last = null;

        for (var attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            try
            {
                var result = action();
                if (result is not null)
                {
                    attemptsUsed = attempt;
                    return result;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            if (attempt < ReadAttempts && delay > 0)
            {
                Thread.Sleep(delay);
                delay *= 2;
            }
        }

        attemptsUsed = ReadAttempts;
        if (last is not null)
        {
            throw last;
        }

        return default;
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void AddExtra(ClipEntry entry, string note)
    {
        entry.Extras ??= new List<string>();
        entry.Extras.Add(note);
    }

    public static string Truncate(string s, int max = MaxTextPreview)
        => s.Length <= max ? s : s[..max] + "…";
}
