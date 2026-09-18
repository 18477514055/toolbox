using Path = System.IO.Path;

namespace Toolbox.Tools.BatchRename;

/// <summary>
/// 批量改名的**规则**（纯数据，无副作用）。
/// </summary>
internal sealed class RenameRule
{
    /// <summary>在原名前加的内容（可空）。</summary>
    public string? Prefix { get; set; }

    /// <summary>在原名后、扩展名前加的内容（可空）。</summary>
    public string? Suffix { get; set; }

    /// <summary>查找并替换（可空 = 不替换）。</summary>
    public string? FindText { get; set; }

    public string? ReplaceText { get; set; }

    /// <summary>是否区分大小写（替换时用）。</summary>
    public bool MatchCase { get; set; }

    /// <summary>序号：起始值。</summary>
    public int SequenceStart { get; set; } = 1;

    /// <summary>序号：位数（不足左侧补 0）。0 或负数 = 不补零。</summary>
    public int SequencePadding { get; set; } = 3;

    /// <summary>序号：步长。</summary>
    public int SequenceStep { get; set; } = 1;

    /// <summary>是否启用序号（启用时序号会加在名字前面）。</summary>
    public bool UseSequence { get; set; }

    /// <summary>时间戳：是否启用。</summary>
    public bool UseTimestamp { get; set; }

    /// <summary>时间戳格式（.NET 日期格式串）。</summary>
    public string TimestampFormat { get; set; } = "yyyyMMdd";

    /// <summary>
    /// 时间戳用哪个时间：true = 文件**修改时间**；false = **当前时间**。
    ///
    /// 默认用修改时间：给照片/文档批量改名时，用户想要的是"这张照片是什么时候的"，
    /// 而不是"我什么时候整理的"。用当前时间会让一批文件全叫同一个名字（还得靠序号区分）。
    /// </summary>
    public bool TimestampUseFileTime { get; set; } = true;

    /// <summary>扩展名是否改成小写。</summary>
    public bool LowercaseExtension { get; set; }

    /// <summary>目标文件名冲突时的策略。</summary>
    public NameConflictPolicy ConflictPolicy { get; set; } = NameConflictPolicy.AutoNumber;
}

/// <summary>改名后与已有文件重名时怎么办。</summary>
internal enum NameConflictPolicy
{
    /// <summary>自动加 (1)(2)…（默认，最安全）。</summary>
    AutoNumber,

    /// <summary>跳过这一条，不改。</summary>
    Skip,

    /// <summary>仍然改（危险：会覆盖已有文件）。</summary>
    Overwrite,
}

/// <summary>
/// 一条待执行的改名计划。
/// </summary>
/// <param name="SourcePath">原完整路径。</param>
/// <param name="TargetName">目标文件名（不含目录）。</param>
/// <param name="Status">状态说明（正常 / 跳过原因 / 冲突提示）。</param>
internal sealed record RenamePlanItem(
    string SourcePath,
    string TargetName,
    string Status)
{
    public string SourceName => Path.GetFileName(SourcePath);

    public string TargetPath => Path.Combine(Path.GetDirectoryName(SourcePath) ?? "", TargetName);

    /// <summary>这一条是否会被真正执行。</summary>
    public bool WillApply => Status.Length == 0;

    /// <summary>名字真的变了吗。</summary>
    public bool Changes => !string.Equals(SourceName, TargetName, StringComparison.Ordinal);
}

/// <summary>
/// 批量改名的**核心逻辑**：给一批文件 + 一条规则，算出改名计划。
///
/// ⚠️ 刻意做成**纯函数**（不碰磁盘、不改文件），理由有两条：
///   1. "预览"和"执行"必须用**同一套计算**。若各写一份，预览里看到的和实际改出来的
///      迟早会不一致 —— 而用户是**照着预览点的确定**，那等于骗他。
///   2. 纯函数能被自检直接断言，不用真的动文件。
/// </summary>
internal static class RenamePlanner
{
    /// <summary>
    /// 计算改名计划。
    /// </summary>
    /// <param name="paths">原始文件路径（顺序即序号顺序）。</param>
    /// <param name="rule">规则。</param>
    /// <param name="now">"当前时间"（显式传入以便测试固定）。</param>
    /// <param name="fileTime">取文件修改时间的委托（显式传入以便测试）。</param>
    /// <param name="exists">判断路径是否已存在的委托（显式传入以便测试）。</param>
    public static List<RenamePlanItem> Plan(
        IReadOnlyList<string> paths,
        RenameRule rule,
        DateTime now,
        Func<string, DateTime> fileTime,
        Func<string, bool> exists)
    {
        var result = new List<RenamePlanItem>(paths.Count);

        // 已产出的目标名（同批次内去重要用）——
        // 大小写不敏感：Windows 文件系统默认就是大小写不敏感的
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < paths.Count; i++)
        {
            var source = paths[i];
            var srcName = Path.GetFileName(source);
            var ext = Path.GetExtension(srcName);
            var stem = Path.GetFileNameWithoutExtension(srcName);

            var status = "";

            // ---- ① 替换（先做，因为它是"改原名"，其他都是"加工"）----
            if (!string.IsNullOrEmpty(rule.FindText))
            {
                var cmp = rule.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (stem.Contains(rule.FindText, cmp))
                {
                    stem = stem.Replace(rule.FindText, rule.ReplaceText ?? "", cmp);
                }
            }

            // ---- ② 时间戳 ----
            if (rule.UseTimestamp)
            {
                DateTime stamp;
                if (rule.TimestampUseFileTime)
                {
                    try
                    {
                        stamp = fileTime(source);
                    }
                    catch
                    {
                        // 取不到修改时间（文件被占用 / 权限）就退回当前时间，
                        // 但要说清楚 —— 静默用错时间会让用户拿到一批名字不对的文件
                        stamp = now;
                        status = "取不到修改时间，已用当前时间";
                    }
                }
                else
                {
                    stamp = now;
                }

                // 格式串写坏时 Format 会抛，兜一下
                string stampText;
                try
                {
                    stampText = stamp.ToString(rule.TimestampFormat);
                }
                catch
                {
                    stampText = stamp.ToString("yyyyMMdd");
                    status = "时间戳格式无效，已改用 yyyyMMdd";
                }

                stem = stampText + "_" + stem;
            }

            // ---- ③ 序号 ----
            if (rule.UseSequence)
            {
                var n = rule.SequenceStart + i * rule.SequenceStep;
                var num = rule.SequencePadding > 0
                    ? n.ToString().PadLeft(rule.SequencePadding, '0')
                    : n.ToString();

                stem = num + "_" + stem;
            }

            // ---- ④ 前后缀 ----
            if (!string.IsNullOrEmpty(rule.Prefix))
            {
                stem = rule.Prefix + stem;
            }

            if (!string.IsNullOrEmpty(rule.Suffix))
            {
                stem = stem + rule.Suffix;
            }

            // ---- ⑤ 扩展名 ----
            if (rule.LowercaseExtension)
            {
                ext = ext.ToLowerInvariant();
            }

            var target = stem + ext;

            // ---- 校验：文件名不能为空 / 非法字符 ----
            if (string.IsNullOrWhiteSpace(stem))
            {
                result.Add(new RenamePlanItem(source, srcName, "改名后文件名为空，已跳过"));
                continue;
            }

            var invalid = Path.GetInvalidFileNameChars();
            if (target.IndexOfAny(invalid) >= 0)
            {
                result.Add(new RenamePlanItem(source, target, "改名后含非法字符（\\ / : * ? \" < > |），已跳过"));
                continue;
            }

            var dir = Path.GetDirectoryName(source) ?? "";

            // ---- 冲突处理 ----
            var targetPath = Path.Combine(dir, target);
            var sameName = string.Equals(srcName, target, StringComparison.OrdinalIgnoreCase);

            if (!sameName && (taken.Contains(target) || exists(targetPath)))
            {
                switch (rule.ConflictPolicy)
                {
                    case NameConflictPolicy.Skip:
                        result.Add(new RenamePlanItem(source, target, "目标已存在，已跳过"));
                        continue;

                    case NameConflictPolicy.Overwrite:
                        // 用户明确选择了覆盖：照改，但把风险写在状态里
                        status = Append(status, "将与已有文件同名（覆盖）");
                        break;

                    default: // AutoNumber
                        var unique = MakeUnique(dir, stem, ext, taken, exists);
                        target = unique;
                        targetPath = Path.Combine(dir, target);
                        status = Append(status, "重名，已自动加序号");
                        break;
                }
            }

            taken.Add(target);

            result.Add(new RenamePlanItem(source, target, status));
        }

        return result;
    }

    private static string Append(string a, string b)
        => string.IsNullOrEmpty(a) ? b : a + "；" + b;

    /// <summary>在 stem 后面加 (1)(2)… 直到不冲突。</summary>
    private static string MakeUnique(
        string dir, string stem, string ext,
        HashSet<string> taken, Func<string, bool> exists)
    {
        for (var i = 1; i < 10000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!taken.Contains(candidate) && !exists(Path.Combine(dir, candidate)))
            {
                return candidate;
            }
        }

        // 极端情况：用 GUID 兜底，绝不返回一个会冲突的名字
        return $"{stem}_{Guid.NewGuid():N}{ext}";
    }
}
