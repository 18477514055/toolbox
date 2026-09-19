namespace Toolbox.Shell;

/// <summary>
/// 「哪个工具正在干活」。
///
/// 为什么需要它：AI 在本地 7B 模型上可能要跑十几秒到几分钟。
/// 悬浮窗上必须能看出"它在干活"——否则用户会以为程序卡死了，
/// 然后重复点击、或者直接去任务管理器结束进程。
/// 交接文档把「AI 流式生成时图标显示进度环」列为悬浮窗的必备特性。
/// </summary>
public static class BusyState
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Busy = new(StringComparer.OrdinalIgnoreCase);

    public static void Set(string toolId, bool busy, string? label = null)
    {
        lock (Gate)
        {
            if (busy)
            {
                Busy[toolId] = label ?? "处理中";
            }
            else
            {
                Busy.Remove(toolId);
            }
        }
    }

    public static bool IsBusy(string toolId)
    {
        lock (Gate)
        {
            return Busy.ContainsKey(toolId);
        }
    }

    public static string? LabelOf(string toolId)
    {
        lock (Gate)
        {
            return Busy.TryGetValue(toolId, out var v) ? v : null;
        }
    }
}
