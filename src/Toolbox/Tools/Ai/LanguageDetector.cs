namespace Toolbox.Tools.Ai;

/// <summary>
/// 中英双向自动判方向。
///
/// 用户明确要求：「先固定中文和英文，双向吧，目前也只用这个」——
/// 所以**不做语言选择器**，靠检测输入内容自动决定译成什么。
///
/// ⚠️ 交接文档的一条关键提醒：**提示词里必须写明方向**。
/// 7B 模型对含糊指令（"翻译成中文"这种）会自己猜方向，猜错时质量明显下降。
/// 所以这里检测出的方向会被写进提示词（见 Prompts）。
///
/// 抽成独立小函数是刻意的：以后要加日语等语种，只改这里，不用去动调用处。
/// </summary>
internal static class LanguageDetector
{
    /// <summary>CJK 字符占比超过这个阈值就认为"这段主要是中文"。</summary>
    private const double ChineseThreshold = 0.30;

    public static bool IsMostlyChinese(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true; // 空内容按中文处理，无所谓
        }

        var cjk = 0;
        var letters = 0;

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                cjk++;
                letters++;
            }
            else if (char.IsLetter(ch))
            {
                letters++;
            }
        }

        if (letters == 0)
        {
            return true;
        }

        return (double)cjk / letters > ChineseThreshold;
    }

    /// <summary>返回目标语言的中文名（"英文" / "中文"），用于拼提示词。</summary>
    public static string TargetLanguageFor(string text)
        => IsMostlyChinese(text) ? "英文" : "中文";

    private static bool IsCjk(char ch)
        => (ch >= 0x4E00 && ch <= 0x9FFF)     // 基本汉字
           || (ch >= 0x3400 && ch <= 0x4DBF)  // 扩展 A
           || (ch >= 0xF900 && ch <= 0xFAFF); // 兼容汉字
}
