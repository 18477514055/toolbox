namespace Toolbox.Shell;

/// <summary>
/// 把工具 Id / 额外热键 Id 说成人话。
///
/// 存在的理由：`ai.translateSelection` 这种内部 Id 直接显示给用户是看不懂的。
/// 两处都要用（App 的热键通知、设置窗口的热键行），所以抽出来，避免两处各写一份然后慢慢走样。
/// </summary>
internal static class ToolNaming
{
    /// <summary>"translateSelection" → "翻译选中"</summary>
    public static string ExtraLabel(string extraName) => extraName switch
    {
        "translateSelection" => "翻译选中",
        _ => extraName,
    };

    /// <summary>"ai" → "快捷 AI"；"ai.translateSelection" → "快捷 AI · 翻译选中"</summary>
    public static string Describe(ToolRegistry? registry, string fullId)
    {
        var dot = fullId.IndexOf('.');
        if (dot < 0)
        {
            return registry?.Find(fullId)?.Name ?? fullId;
        }

        var tool = registry?.Find(fullId[..dot]);
        var label = ExtraLabel(fullId[(dot + 1)..]);
        return tool is null ? fullId : $"{tool.Name} · {label}";
    }
}
