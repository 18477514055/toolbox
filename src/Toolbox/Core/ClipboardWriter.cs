using System.Collections.Specialized;
using System.Windows.Media.Imaging;
using WClipboard = System.Windows.Clipboard;
using WDataObject = System.Windows.DataObject;
using WDataFormats = System.Windows.DataFormats;

namespace Toolbox.Core;

/// <summary>
/// 写剪贴板。三件事必须一起做：
///   1. **带重试** —— CLIPBRD_E_CANT_OPEN（剪贴板被别的进程占用）是常态；
///   2. **打自污染标记** —— 塞一个私有格式 token，我们自己的监听读到就知道该跳过归档（规则③）；
///   3. **注册内容哈希** —— token 万一被某些程序重建剪贴板时丢掉，哈希还能兜住文本场景。
///
/// ⚠️ 注意：这里写剪贴板是「用户主动要的」动作（复制历史条目 / 复制译文），
/// 属于规则③允许的范围。**工具自己为了搬数据偷偷写剪贴板仍然禁止。**
/// </summary>
internal static class ClipboardWriter
{
    private const int WriteAttempts = 4;
    private const int FirstRetryDelayMs = 60;

    public static bool SetText(string text, out string? error)
    {
        error = null;
        var token = SelfWriteGuard.NewToken();
        SelfWriteGuard.RegisterHash(SelfWriteGuard.HashText(text));

        var data = new WDataObject();
        data.SetData(WDataFormats.UnicodeText, text);
        data.SetData(SelfWriteGuard.SelfWriteFormat, token);

        return TrySet(data, out error);
    }

    public static bool SetImage(BitmapSource image, out string? error)
    {
        error = null;
        var token = SelfWriteGuard.NewToken();

        // 图片走 DIB 往返后像素格式可能变化，PNG 字节哈希对不上是正常的——
        // 所以图片场景**只能**靠 token 识别，哈希只是顺手注册。
        try
        {
            SelfWriteGuard.RegisterHash(SelfWriteGuard.HashBytes(ClipboardReader.EncodePng(image)));
        }
        catch
        {
            // 编码失败不影响写入
        }

        var data = new WDataObject();
        data.SetData(WDataFormats.Bitmap, image);
        data.SetData(SelfWriteGuard.SelfWriteFormat, token);

        return TrySet(data, out error);
    }

    public static bool SetFiles(IReadOnlyList<string> paths, out string? error)
    {
        error = null;
        if (paths.Count == 0)
        {
            error = "没有文件可复制。";
            return false;
        }

        var token = SelfWriteGuard.NewToken();
        SelfWriteGuard.RegisterHash(SelfWriteGuard.HashPaths(paths));

        var collection = new StringCollection();
        collection.AddRange(paths.ToArray());

        var data = new WDataObject();
        data.SetFileDropList(collection);
        data.SetData(SelfWriteGuard.SelfWriteFormat, token);

        return TrySet(data, out error);
    }

    private static bool TrySet(WDataObject data, out string? error)
    {
        error = null;
        var delay = FirstRetryDelayMs;
        Exception? last = null;

        for (var attempt = 1; attempt <= WriteAttempts; attempt++)
        {
            try
            {
                // copy: true —— 让数据在我们退出后依然留在剪贴板里
                WClipboard.SetDataObject(data, true);
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < WriteAttempts)
                {
                    Thread.Sleep(delay);
                    delay *= 2;
                }
            }
        }

        error = last is null
            ? "写入剪贴板失败（未知原因）。"
            : $"写入剪贴板失败：{last.GetType().Name}: {last.Message}";
        return false;
    }
}
