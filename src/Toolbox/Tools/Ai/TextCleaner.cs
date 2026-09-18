using System.Text;

namespace Toolbox.Tools.Ai;

/// <summary>
/// 送进模型之前的轻量清洗。
///
/// ⚠️ 这里刻意**做得很少**。交接文档说得很清楚：
/// 「最可靠的兜底是让用户看到并编辑将要送出的文本」——
/// 任何自动清洗都可能删掉用户真正想要的内容，而用户看不出来。
/// 所以只做四件确定性很高的事，而且界面上永远显示"读到 N 字"并允许手动改。
/// </summary>
internal static class TextCleaner
{
    public static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n');

        text = CollapseBlankLines(text);
        text = RemoveRepeatedLines(text);
        text = TrimLines(text);

        return text.Trim();
    }

    /// <summary>连续空行压成一个。</summary>
    private static string CollapseBlankLines(string text)
    {
        var lines = text.Split('\n');
        var builder = new StringBuilder();
        var blankRun = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                blankRun++;
                if (blankRun > 1)
                {
                    continue;
                }
            }
            else
            {
                blankRun = 0;
            }

            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// 去掉**连续重复**的行（同一行紧挨着出现多次）。
    /// 只处理"连续"的情况：网页里页脚那种"间隔出现"的重复行不好判断，
    /// 误删的代价比留着大。
    /// </summary>
    private static string RemoveRepeatedLines(string text)
    {
        var lines = text.Split('\n');
        var builder = new StringBuilder();
        string? previous = null;
        var repeatCount = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.Length > 0 && trimmed == previous)
            {
                repeatCount++;
                if (repeatCount >= 1)
                {
                    continue; // 连续重复的直接跳过
                }
            }
            else
            {
                repeatCount = 0;
            }

            previous = trimmed;
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    private static string TrimLines(string text)
        => string.Join('\n', text.Split('\n').Select(l => l.TrimEnd()));

    /// <summary>
    /// 按段落边界把长文本切成块。
    /// 长文本必须分块，否则模型会**静默截断**（用户会以为"总结漏了"，但其实是没送进去）。
    /// </summary>
    public static List<string> Chunk(string text, int maxChars)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return chunks;
        }

        if (maxChars < 200)
        {
            maxChars = 200;
        }

        var paragraphs = text.Split('\n');
        var current = new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            // 单段就超长：硬切
            if (paragraph.Length > maxChars)
            {
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                }

                for (var i = 0; i < paragraph.Length; i += maxChars)
                {
                    chunks.Add(paragraph.Substring(i, Math.Min(maxChars, paragraph.Length - i)));
                }

                continue;
            }

            if (current.Length + paragraph.Length + 1 > maxChars && current.Length > 0)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }

            current.Append(paragraph).Append('\n');
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString().Trim());
        }

        return chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
    }
}
