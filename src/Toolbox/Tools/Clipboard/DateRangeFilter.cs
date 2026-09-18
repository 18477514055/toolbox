namespace Toolbox.Tools.Clipboard;

/// <summary>
/// 剪贴板历史面板上那个「日期筛选」下拉框的判定逻辑。
///
/// 单独抽成一个静态纯函数（而不是留在面板的实例方法里），是为了**能被自检直接调用**。
/// 这块是 off-by-one 的高发区：「最近 7 天」到底含不含今天？「昨天」的右边界是开还是闭？
/// 跨月、跨年的时候 AddDays(-29) 会不会算错？这些靠手点几乎测不全，靠断言才靠得住。
///
/// 档位编号直接对上 ComboBox 的 SelectedIndex，省一层映射表（少一处能写错的地方）。
/// </summary>
internal static class DateRangeFilter
{
    internal const int All = 0;
    internal const int Today = 1;
    internal const int Yesterday = 2;
    internal const int Last7Days = 3;
    internal const int Last30Days = 4;

    /// <summary>
    /// 判断某个时间点是否落在指定档位内。
    /// <paramref name="today"/> 显式传进来而不是内部取 <c>DateTime.Today</c>，
    /// 否则这个函数就没法在测试里固定"今天"，跨零点的用例会随机失败。
    ///
    /// 注意这里**主动归一化到零点**（<c>.Date</c>）而不是直接用它比较。
    /// 生产调用点传的是 <c>DateTime.Today</c>（本来就是零点），看起来多余；
    /// 但这个函数一旦被传进 <c>DateTime.Now</c>，所有 "&gt;= today" 的判断就会把
    /// 今天凌晨的记录全部误判成"不在今天"—— 而这种错误只在白天出现、
    /// 半夜测试永远正常，属于最难发现的一类。归一化把它变成不可能犯的错。
    /// </summary>
    internal static bool Matches(DateTime time, int index, DateTime today)
    {
        if (index <= All)
        {
            return true; // 全部时间
        }

        if (time == DateTime.MinValue)
        {
            // 时间戳坏掉的条目不该因为"筛选"而凭空消失 —— 那看起来就像丢数据。
            // 宁可多显示一条，也不要让用户以为记录没了。
            return true;
        }

        var day = today.Date;

        return index switch
        {
            Today => time >= day,                                  // 今天：零点之后
            Yesterday => time >= day.AddDays(-1) && time < day,     // 昨天：左闭右开
            Last7Days => time >= day.AddDays(-6),                   // 最近 7 天（含今天，所以减 6）
            Last30Days => time >= day.AddDays(-29),                 // 最近 30 天（含今天，所以减 29）
            _ => true,                                              // 未知档位：不筛
        };
    }
}
