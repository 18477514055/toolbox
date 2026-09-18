namespace Toolbox.Tools.Ai;

/// <summary>
/// 提示词。
///
/// 三条来自交接文档的硬要求，全在这里落实：
///   1. **方向必须写死**（"翻译成英文"而不是"翻译成中文"这种含糊话）——
///      7B 模型对含糊指令会自己猜方向，猜错时质量明显下降；
///   2. **中译英比英译中更容易出错**（术语、语气）→ 加"保持原文语气与专业程度"；
///   3. **代码片段 / 专有名词 / URL 原样保留**；
///   4. 7B 对中文指令遵循一般 → 提示词要**短、结构化、明确要求输出格式**。
/// </summary>
internal static class Prompts
{
    /// <summary>
    /// 翻译。targetLanguage 必须是明确的"英文"或"中文"，不能含糊。
    /// </summary>
    public static string Translate(string targetLanguage, string content)
        => $"""
把下面的内容翻译成{targetLanguage}。

要求：
1. 只输出译文，不要解释、不要加任何前后缀说明；
2. 保持原文的语气、专业程度和段落结构；
3. 代码片段、专有名词、URL、数字原样保留，不要翻译；
4. 不要漏译，不要自行增删内容。

内容：
{content}
""";

    /// <summary>分块翻译时，告诉模型这是长文的第几段，避免它自作主张地"接上文"。</summary>
    public static string TranslateChunk(string targetLanguage, int index, int total, string content)
    {
        var position = total > 1
            ? $"\n（这是一篇长文的第 {index}/{total} 段，只翻译这一段本身，不要总结、不要衔接上下文。）"
            : "";

        return $"""
把下面的内容翻译成{targetLanguage}。

要求：
1. 只输出译文，不要解释、不要加任何前后缀说明；
2. 保持原文的语气、专业程度和段落结构；
3. 代码片段、专有名词、URL、数字原样保留；
4. 不要漏译，不要自行增删内容。{position}

内容：
{content}
""";
    }

    /// <summary>总结。要求结构化、限条数，否则 7B 容易写成一大段废话。</summary>
    public static string Summarize(string content)
        => $"""
用中文总结下面这段内容。

要求：
1. 输出 3-8 条要点，每条一句话，用「- 」开头；
2. 只保留事实与结论，不要写"本文介绍了…"这类空话；
3. 如果内容里有数字、日期、人名、结论，优先保留；
4. 只输出摘要本身，不要加任何说明。

内容：
{content}
""";

    /// <summary>长文分块总结的第二步：把各段摘要再汇总一次。</summary>
    public static string Reduce(string partialSummaries)
        => $"""
下面是同一篇长文分段总结出来的若干组要点。请把它们合并成一份最终摘要。

要求：
1. 去掉重复的要点，合并意思相近的；
2. 输出 3-8 条，每条一句话，用「- 」开头；
3. 按重要性排序，最重要的在前；
4. 只输出最终摘要本身。

分段要点：
{partialSummaries}
""";

    /// <summary>自由问答（顺带支持，成本很低）。</summary>
    public static string Ask(string question, string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return question;
        }

        return $"""
根据下面的内容回答问题。如果内容里找不到答案，就明确说"材料里没有提到"。

内容：
{context}

问题：{question}
""";
    }
}
