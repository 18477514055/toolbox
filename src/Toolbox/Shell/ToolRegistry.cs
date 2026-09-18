using Toolbox.Core;

namespace Toolbox.Shell;

/// <summary>
/// 工具注册表。P1 阶段硬编码一个列表就够了（见 03-快捷剪贴板设计.md §四），
/// 工具多到十几个再改成反射扫描。
/// </summary>
internal sealed class ToolRegistry
{
    private readonly List<IToolboxTool> _tools = new();

    public IReadOnlyList<IToolboxTool> Tools => _tools;

    public void Add(IToolboxTool tool) => _tools.Add(tool);

    public IToolboxTool? Find(string id)
        => _tools.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>悬浮窗要显示的按钮（按设置过滤）。</summary>
    public IEnumerable<IToolboxTool> VisibleTools(Settings settings)
    {
        // ① 先过"启停"关：关掉的工具不该在悬浮窗上占位置。
        //    这正是用户要的核心行为 ——「开关决定悬浮窗上显示哪些功能」。
        var enabled = _tools.Where(t => ToolGate.IsEnabled(settings, t.Id));

        // ② 再过"用户自定义显示哪些按钮"关（原有功能，保留）。
        //    两道关是**与**关系：必须既启用、又在自定义清单里。
        if (settings.FloatingButtons.Count == 0)
        {
            return enabled;
        }

        return enabled.Where(t => settings.FloatingButtons.Contains(t.Id, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 只启动**被启用**的工具。
    /// </summary>
    /// <returns>实际启动成功的工具 Id 集合（调用方要用它来跟踪"哪些在跑"）。</returns>
    public HashSet<string> StartAll(ToolboxContext ctx, Settings settings)
    {
        var started = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in _tools)
        {
            // ★ 关掉的工具**根本不 Start** —— 不是"启动后忽略它的输入"。
            //
            //   这个区别很重要：Start 里做的是真事（注册剪贴板监听、订阅热键、
            //   挂事件回调）。如果只是不显示按钮却照样 Start，
            //   那关掉的工具**仍在后台跑、仍占资源、仍会响应热键** ——
            //   那不是"停用"，只是"藏起来"。用户要的是前者。
            if (!ToolGate.IsEnabled(settings, tool.Id))
            {
                Log.Line($"工具未启动（已关闭）：{tool.Id}（{tool.Name}）");
                continue;
            }

            try
            {
                tool.Start(ctx);
                started.Add(tool.Id);
                Log.Line($"工具已启动：{tool.Id}（{tool.Name}）");
            }
            catch (Exception ex)
            {
                // 一个工具起不来不能拖垮整个工具箱
                Log.Exception($"工具启动失败：{tool.Id}", ex);
            }
        }

        return started;
    }

    public void StopAll()
    {
        foreach (var tool in _tools)
        {
            try
            {
                tool.Stop();
            }
            catch (Exception ex)
            {
                Log.Exception($"工具停止失败：{tool.Id}", ex);
            }
        }
    }
}
