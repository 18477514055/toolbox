namespace Toolbox.Shell;

/// <summary>
/// 工具插件的清单（每个插件 DLL 里写一份，用程序集特性标注）。
///
/// 为什么要单独一个"清单"而不是只靠发现 <see cref="IToolboxTool"/>：
///   主程序需要**在实例化插件之前**就知道它的 Id / 名称 / 默认热键 ——
///   因为要拿这些去和 `tools.json` 对账、决定它是否该被加载、
///   以及在没有 UI 的情况下也能列出它。
///   实例化插件是有代价的（要 STA 线程、要它自己的依赖就绪），
///   所以"先读清单、再决定要不要实例化"是必要的。
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ToolboxPluginAttribute : Attribute
{
    public ToolboxPluginAttribute(string id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>工具 Id（须与主程序内置工具的 Id 不冲突）。</summary>
    public string Id { get; }

    /// <summary>显示名。</summary>
    public string Name { get; }

    /// <summary>插件内部版本（与 tools.json 里的 Version 对账）。</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// 插件编译时针对的**契约版本**。
    ///
    /// 为什么必须有：插件是独立发布的，用户可能装了一个
    /// "针对旧版主程序编译"的插件。契约一旦不兼容，
    /// 加载会以各种奇怪的方式失败（缺方法、类型不匹配……），
    /// 错误信息完全指不到"版本不对"上去。
    /// 有了它就能在加载前**明确拒绝**并说清原因。
    /// </summary>
    public string ContractsVersion { get; set; } = "";
}

/// <summary>
/// 契约版本号（主程序与插件都引用它）。
///
/// ⚠️ 改这个数字的时机：**契约发生不兼容变更时**（删/改公开成员）。
/// 只新增成员不算不兼容，不用改。
/// </summary>
public static class ContractsVersion
{
    /// <summary>当前契约版本。</summary>
    public const string Current = "1.0";

    /// <summary>
    /// 判断一个插件声明的契约版本是否兼容。
    ///
    /// 规则（故意保守）：**主版本号必须相同**。
    ///   1.x 的插件能被 1.y 的主程序加载（新增成员不影响）；
    ///   2.x 的插件不被 1.y 接受（可能用了新成员）。
    /// </summary>
    public static bool IsCompatible(string? pluginVersion, out string reason)
    {
        reason = "";

        if (string.IsNullOrWhiteSpace(pluginVersion))
        {
            reason = "插件没有声明契约版本（可能是很旧的构建）。";
            return false;
        }

        var hostMajor = Major(Current);
        var pluginMajor = Major(pluginVersion);

        if (pluginMajor != hostMajor)
        {
            reason = $"契约版本不兼容：插件要求 {pluginVersion}，主程序是 {Current}。"
                     + "请下载与本版主程序配套的插件。";
            return false;
        }

        return true;
    }

    private static string Major(string v)
    {
        var dot = v.IndexOf('.');
        return dot > 0 ? v[..dot] : v;
    }
}
