namespace Toolbox.Shell;

/// <summary>
/// 工具的启停判定。**这是"哪个工具在运行"的唯一判据**。
///
/// 为什么必须集中在一处：
///   同时有 4 个地方要问"这个工具启用了吗" ——
///   悬浮窗（显示哪些按钮）、热键（注册哪些键）、托盘菜单（列哪些工具）、
///   以及托盘图标本身（总开关关掉时要不要显示）。
///   如果各写一份 `if (settings.XxxEnabled)`，迟早会有一处漏改，
///   症状是"界面上看得到、点了没反应"或者"点了能用但界面上没有" —— 极难查。
///
///   所以全部走这里。加新工具时**只需要在这里的 <see cref="DefaultOn"/> 里登记一次**
///   默认是开还是关，其余地方自动跟随。
/// </summary>
public static class ToolGate
{
    /// <summary>
    /// 首次运行时默认**开启**的工具。
    ///
    /// 依据（用户选定"只开常用的 5 个"）：
    ///   这 5 个是"几乎每天都会用"的：剪贴板（天天复制）、截图、裁剪、格式转换、AI。
    ///   其余 7 个属于"偶尔用一次"——默认关掉能让悬浮窗清爽，
    ///   也让新用户先看到核心能力，而不是被 12 个图标淹没。
    ///
    /// ⚠️ 想改默认策略只改这里。已经手动开/关过的用户**不受影响**
    ///   （他们的选择存在 `Settings.ToolEnabled` 里，见那边的说明）。
    /// </summary>
    private static readonly HashSet<string> DefaultOn = new(StringComparer.OrdinalIgnoreCase)
    {
        "clipboard",   // 快捷剪贴板
        "screenshot",  // 快捷截图
        "image",       // 图片裁剪
        "convert",     // 格式转换
        "ai",          // 快捷 AI
    };

    /// <summary>所有"默认开启"的工具（用于界面标注"默认开启 / 默认关闭"）。</summary>
    public static IReadOnlyCollection<string> DefaultOnTools => DefaultOn;

    /// <summary>这个工具是不是"默认开启"的（只反映默认策略，不反映用户当前选择）。</summary>
    public static bool IsDefaultOn(string toolId) => DefaultOn.Contains(toolId);

    /// <summary>
    /// 这个工具现在启用吗？
    /// </summary>
    /// <param name="settings">当前设置。</param>
    /// <param name="toolId">工具 Id（如 "clipboard"）。</param>
    public static bool IsEnabled(Settings settings, string toolId)
    {
        // ★ 总开关优先：总开关关掉时，**所有**工具一律不启用，无视单个开关。
        //
        //   这是刻意的语义：总开关的用途就是"一键让整个工具箱安静下来"。
        //   如果它还要尊重每个工具的开关，那用户关掉总开关后
        //   会发现"怎么还有几个工具在跑"，反倒要去挨个关 —— 那就失去意义了。
        if (!settings.ToolsEnabled)
        {
            return false;
        }

        // 用户显式设过 → 用他的
        if (settings.ToolEnabled.TryGetValue(toolId, out var explicitValue))
        {
            return explicitValue;
        }

        // 没设过 → 用默认
        return DefaultOn.Contains(toolId);
    }

    /// <summary>
    /// 这个工具**是否在本次会话里真正启动过**（用于界面区分"启用但没起来"）。
    ///
    /// 为什么要区分：工具启用 ≠ 启动成功。启动失败（比如某工具初始化抛异常）
    /// 只在日志里留一行，界面上仍显示成"已启用"，用户会以为能用。
    /// </summary>
    public static string DescribeState(Settings settings, string toolId, bool startedOk)
    {
        if (!settings.ToolsEnabled)
        {
            return "总开关已关闭";
        }

        if (!IsEnabled(settings, toolId))
        {
            return "已关闭";
        }

        return startedOk ? "运行中" : "已启用但启动失败（看日志）";
    }
}
