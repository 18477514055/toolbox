using System.Windows;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using MemoryStream = System.IO.MemoryStream;

namespace Toolbox.Tools.Ocr;

/// <summary>
/// 一次 OCR 的结果。
/// </summary>
/// <param name="Success">是否成功识别（失败时 Text 为空、Error 有原因）。</param>
/// <param name="Text">识别出的文字（已做轻量整理：压掉多余空行）。</param>
/// <param name="Lines">逐行结果，保留原始换行结构。</param>
/// <param name="Error">失败原因（中文，可直接显示给用户）。</param>
internal sealed record OcrResult(bool Success, string Text, IReadOnlyList<string> Lines, string? Error)
{
    public static OcrResult Ok(string text, IReadOnlyList<string> lines) => new(true, text, lines, null);

    public static OcrResult Fail(string error) => new(false, "", Array.Empty<string>(), error);
}

/// <summary>
/// OCR 引擎。**用 Windows 自带的 <c>Windows.Media.Ocr</c>**，不引任何第三方库。
///
/// 为什么选它（实测过才定的）：
///   · **零依赖**：Win10 1903+ 天生就有，对方电脑不用装任何东西；
///   · **离线**：不联网、不上传，截图内容不出本机；
///   · **中文可用**：本机实测（zh-Hans-CN）「你好世界」「桌面工具箱Toolbox测试」
///     「快捷截图能够识别屏幕上的文字内容」全部逐字识别正确。
///
/// 已知弱点（实测，写进 README）：**数字与相似字母会混**（`1` 认成 `I`、
///   半角句点认成全角 `．`）。这是纯视觉 OCR 的固有局限，不是本实现的问题。
///
/// 与 <c>ScreenCapture</c> 的分工：本类只管"位图 → 文字"，
///   截屏交给已有的 <c>ScreenCapture.CaptureFullScreen()</c>（复用，不重写）。
/// </summary>
internal static class OcrEngineService
{
    /// <summary>引擎缓存。创建有开销（要加载语言模型），不要每次识别都建一遍。</summary>
    private static Windows.Media.Ocr.OcrEngine? _engine;
    private static string _engineTag = "";
    private static readonly object Gate = new();

    /// <summary>当前引擎用的语言标签（显示在界面上，让用户知道识别的是哪种语言）。</summary>
    public static string CurrentLanguageTag
    {
        get
        {
            lock (Gate)
            {
                return _engineTag;
            }
        }
    }

    /// <summary>
    /// 本机可用的 OCR 语言（如 zh-Hans-CN / en-US）。
    /// 一台电脑没装任何 OCR 语言包时返回空 —— 上层要据此给出明确提示。
    /// </summary>
    public static IReadOnlyList<string> AvailableLanguages()
    {
        try
        {
            return Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages
                .Select(l => l.LanguageTag)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Exception("枚举 OCR 语言失败", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 是否可用。不可用时 <paramref name="reason"/> 给出**中文**原因（可直接显示）。
    /// </summary>
    public static bool IsAvailable(out string? reason)
    {
        reason = null;

        var langs = AvailableLanguages();
        if (langs.Count == 0)
        {
            reason = "这台电脑没有安装任何 OCR 语言包。\n"
                     + "请在「设置 → 时间和语言 → 语言和区域」里给中文添加「可选功能 → 光学字符识别」。";
            return false;
        }

        try
        {
            var engine = GetEngine(out var tag);
            if (engine is null)
            {
                reason = $"找到了 OCR 语言包（{string.Join("、", langs)}），但引擎创建失败。";
                return false;
            }

            _ = tag;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"OCR 引擎不可用：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 取引擎。优先中文，其次用户配置语言，最后随便挑一个可用的。
    ///
    /// 为什么优先中文：这是给中文用户做的工具，屏幕上的内容绝大多数是中文；
    ///   而 Windows 的 OCR 引擎**一次只能按一种语言识别**（没有"自动多语言"），
    ///   选错语言包会让中文整片识别不出来。
    /// </summary>
    private static Windows.Media.Ocr.OcrEngine? GetEngine(out string tag)
    {
        lock (Gate)
        {
            if (_engine is not null)
            {
                tag = _engineTag;
                return _engine;
            }

            var available = Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages
                .Select(l => l.LanguageTag)
                .ToList();

            // ① 优先中文（zh-Hans-CN / zh-CN / zh-Hans 都算）
            var zh = available.FirstOrDefault(t =>
                t.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("zh", StringComparison.OrdinalIgnoreCase));

            if (zh is not null)
            {
                try
                {
                    var e = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                        new Windows.Globalization.Language(zh));
                    if (e is not null)
                    {
                        _engine = e;
                        _engineTag = zh;
                        tag = zh;
                        Log.Line($"OCR 引擎已就绪（中文）：{zh}");
                        return e;
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception($"创建中文 OCR 引擎失败（{zh}），改用默认引擎", ex);
                }
            }

            // ② 退到用户配置语言
            try
            {
                var e = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                if (e is not null)
                {
                    _engine = e;
                    _engineTag = e.RecognizerLanguage.LanguageTag;
                    tag = _engineTag;
                    Log.Line($"OCR 引擎已就绪（用户默认语言）：{_engineTag}");
                    return e;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("创建默认 OCR 引擎失败", ex);
            }

            // ③ 还有别的语言包就挑第一个可用的
            foreach (var t in available)
            {
                try
                {
                    var e = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                        new Windows.Globalization.Language(t));
                    if (e is not null)
                    {
                        _engine = e;
                        _engineTag = t;
                        tag = t;
                        Log.Line($"OCR 引擎已就绪（回退）：{t}");
                        return e;
                    }
                }
                catch
                {
                    // 试下一个
                }
            }

            tag = "";
            return null;
        }
    }

    /// <summary>
    /// 识别一张位图里的文字。
    ///
    /// 注意：**WPF 的 BitmapSource 不能直接喂给 WinRT**，
    /// 必须转成 <c>SoftwareBitmap</c>。这里走"编码成 PNG 内存流 → WinRT 解码"这条路，
    /// 比手写像素格式转换短得多，也不容易在 BGRA/PBGRA 上踩坑。
    /// </summary>
    public static async Task<OcrResult> RecognizeAsync(BitmapSource image)
    {
        var engine = GetEngine(out var tag);
        if (engine is null)
        {
            IsAvailable(out var reason);
            return OcrResult.Fail(reason ?? "OCR 引擎不可用。");
        }

        try
        {
            // ⚠️ 两条与"线程亲和性"有关的硬要求，缺一条就会**静默识别不出任何东西**：
            //
            //   ① **输入的图必须 Freeze**。
            //      WPF 的 BitmapSource 默认有线程亲和性：在哪个线程创建就只能那个线程访问。
            //      而 OCR 要在后台跑（识别几百毫秒起步，不能占着 UI 线程）。
            //      不 Freeze 就抛 "The calling thread cannot access this object"。
            //
            //   ② **编码 PNG 必须和创建 encoder 在同一个线程上完成**。
            //      PngBitmapEncoder.Save 会去读 BitmapFrame，而 BitmapFrame 是线程亲和的。
            //      原来的写法先 `await writer.StoreAsync()` 再 Save —— await 之后续体
            //      可能落在**另一个线程**上，于是 Save 抛同样的异常。
            //      ⇒ 改成：PNG 编码**全同步做完**，之后才进入 await 区域。
            //
            //   这两条都是实测踩出来的（自检里三项全部返回空字符串，日志里才有线索）。
            var source = image;
            if (!source.IsFrozen && source.CanFreeze)
            {
                source.Freeze();
            }

            // ---- 预处理：缩小超大图（Windows OCR 上限 10000，超了直接抛）----
            var prepared = DownscaleIfNeeded(source, Windows.Media.Ocr.OcrEngine.MaxImageDimension);

            if (!prepared.IsFrozen && prepared.CanFreeze)
            {
                prepared.Freeze();
            }

            // ---- BitmapSource → PNG 字节（**全程同步，不许跨 await**）----
            byte[] pngBytes;
            {
                var encoder = new PngBitmapEncoder();

                // ⚠️ 这里必须用 **CopyPixels 复制出来的独立位图**，
                //    不能直接把传进来的 BitmapSource 包成 BitmapFrame。
                //
                //    实测堆栈（这就是"识别永远返回空"的真凶）：
                //      BitmapFrame.Create(source)
                //        → BitmapFrameDecode.get_InternalMetadata()
                //        → BitmapDecoder.get_IsDownloading()      ← 炸在这里
                //        → Dispatcher.VerifyAccess() 抛 InvalidOperationException
                //
                //    根因：如果传进来的是 BitmapFrameDecode（`BitmapDecoder.Frames[0]`
                //    或 `BitmapImage` 解码产物就是这种），它**始终持有解码器的 Dispatcher 引用**，
                //    Freeze() 冻结了画面数据却**冻结不掉那层引用**。
                //    所以只要调用线程不是创建它的那个线程，创建 BitmapFrame 就会炸。
                //
                //    解法：把像素**拷进一个由我们完全拥有**的 WriteableBitmap ——
                //    它不依赖任何解码器，因而没有线程包袱。
                //    代价是多一次内存拷贝（一张 4K 图约 33 MB，可接受），
                //    换来的是"无论调用方从哪个线程、传什么来源的图都能用"。
                var safe = ToOwnedBitmap(prepared);

                encoder.Frames.Add(BitmapFrame.Create(safe));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                pngBytes = ms.ToArray();
            }

            // ---- 到这里才开始异步：把字节喂给 WinRT ----
            var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var outStream = ras.GetOutputStreamAt(0))
            {
                var writer = new Windows.Storage.Streams.DataWriter(outStream);
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            ras.Seek(0);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            // ---- 识别 ----
            var result = await engine.RecognizeAsync(softwareBitmap);

            // ---- 整理输出 ----
            var lines = result.Lines
                .Select(l => l.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            var text = TidyText(lines);

            if (string.IsNullOrWhiteSpace(text))
            {
                return OcrResult.Fail("没有识别到文字。\n"
                                      + "可能原因：截图区域里确实没有文字，或者文字太小 / 太模糊。\n"
                                      + "提示：框选时把文字部分包含得完整一些，别太小。");
            }

            Log.Line($"OCR 完成（{tag}）：{lines.Count} 行 / {text.Length} 字");
            return OcrResult.Ok(text, lines);
        }
        catch (Exception ex)
        {
            // 记完整堆栈 —— 这条路径的异常曾经表现为"识别结果为空"，
            // 只看 Message 定位不到是哪一行，必须带堆栈。
            Log.Line($"[OCR 失败] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return OcrResult.Fail($"识别失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 图片过大就等比缩小到上限内。
    /// 用 <c>TransformedBitmap</c>（WPF 内置，无需 System.Drawing）。
    /// </summary>
    private static BitmapSource DownscaleIfNeeded(BitmapSource image, uint maxDimension)
    {
        var longest = Math.Max(image.PixelWidth, image.PixelHeight);
        if (longest <= maxDimension)
        {
            return image;
        }

        var scale = (double)maxDimension / longest;
        var scaled = new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale));

        if (scaled.CanFreeze)
        {
            scaled.Freeze();
        }

        Log.Line($"OCR 前缩放：{image.PixelWidth}×{image.PixelHeight} → {scaled.PixelWidth}×{scaled.PixelHeight}");
        return scaled;
    }

    /// <summary>
    /// 把任意来源的 BitmapSource 复制成一张**完全由本方法拥有**的位图。
    ///
    /// 为什么需要它（这是本工具最容易踩、也最难查的一个坑）：
    ///   传进来的图常常是 <c>BitmapFrameDecode</c>（`BitmapDecoder.Frames[0]` / BitmapImage 的解码产物），
    ///   这种对象**始终持有解码器 Dispatcher 的引用**，<c>Freeze()</c> 冻结不了那层引用。
    ///   于是只要调用线程 ≠ 创建它的线程，<c>BitmapFrame.Create(source)</c> 就会抛
    ///   InvalidOperationException（实测堆栈见调用处注释）。
    ///
    ///   拷进 <see cref="WriteableBitmap"/> 之后，新对象既没有解码器、也没有别的外部引用，
    ///   因此可以安全地跨线程传递/编码。
    /// </summary>
    private static BitmapSource ToOwnedBitmap(BitmapSource src)
    {
        // 统一成 Bgra32：WriteableBitmap 的构造与 CopyPixels 都要求格式匹配，
        // 而源图的格式五花八门（Indexed8 / Bgr24 / Bgra32…），先归一化最省事。
        var formatted = src.Format == System.Windows.Media.PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        var owned = new WriteableBitmap(
            formatted.PixelWidth,
            formatted.PixelHeight,
            formatted.DpiX <= 0 ? 96 : formatted.DpiX,
            formatted.DpiY <= 0 ? 96 : formatted.DpiY,
            System.Windows.Media.PixelFormats.Bgra32,
            null);

        // CopyPixels 要求输入是"我们的"对象也无妨 —— 它在**当前线程**读一次源，
        // 之后就不再碰它了。真正的关键在下一行 Freeze。
        var stride = owned.PixelWidth * 4;
        var buffer = new byte[stride * owned.PixelHeight];
        formatted.CopyPixels(buffer, stride, 0);
        owned.WritePixels(new Int32Rect(0, 0, owned.PixelWidth, owned.PixelHeight),
            buffer, stride, 0);

        owned.Freeze();
        return owned;
    }

    /// <summary>
    /// 轻量整理：压掉连续空行、去掉每行首尾空白。
    ///
    /// 刻意**不做**任何"智能合并段落"之类的加工 ——
    /// 那些加工一旦猜错，用户拿到的就是被改过的文字，而且他不会知道。
    /// OCR 结果要尽可能忠实原样。
    /// </summary>
    private static string TidyText(IReadOnlyList<string> lines)
    {
        var sb = new System.Text.StringBuilder();
        var lastBlank = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.Length == 0)
            {
                if (!lastBlank && sb.Length > 0)
                {
                    sb.Append('\n');
                }

                lastBlank = true;
                continue;
            }

            lastBlank = false;
            sb.Append(line).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }
}
