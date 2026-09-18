using System.Windows.Threading;
using Toolbox.Core;

namespace Toolbox.Shell;

/// <summary>
/// 所有小工具的统一接口。这是「工具箱」三个字的落点。
///
/// 目的：以后加「取色器」「常用短语」「快速计算」「OCR 取字」，
/// 只需要新增一个 Tools\Xxx\ 目录 + 实现这个接口 + 在 ToolRegistry 里加一行，
/// **不用动外壳的任何一行代码**。
/// </summary>
internal interface IToolboxTool
{
    /// <summary>"clipboard" / "image" / "convert" / "ai"</summary>
    string Id { get; }

    /// <summary>中文名，显示在悬浮窗 tooltip 和托盘菜单里</summary>
    string Name { get; }

    /// <summary>悬浮窗上显示的字符（用一个字符最省地方）</summary>
    string Glyph { get; }

    /// <summary>默认热键，如 "Win+Shift+V"</summary>
    string DefaultHotKey { get; }

    /// <summary>一句话说明，显示在 tooltip 第二行</summary>
    string Description { get; }

    /// <summary>是不是需要开一个大窗口（决定悬浮窗要不要临时躲开）</summary>
    bool OpensBigWindow { get; }

    void Start(ToolboxContext ctx);

    void Stop();

    /// <summary>热键 / 悬浮窗点击 / 托盘菜单触发的主动作。</summary>
    void Invoke();

    /// <summary>
    /// 额外的热键：key 是热键名（会拼成 "工具Id.热键名"），value 是默认组合键。
    ///
    /// 为什么需要：快捷 AI 有两个不同频率的入口——「打开面板」（Win+Shift+A）
    /// 和「翻译选中文本」（Win+Shift+T，最高频，值得单独占一个热键）。
    /// 一个工具只有一个主热键是不够的。
    /// </summary>
    IReadOnlyDictionary<string, string> ExtraHotKeys => EmptyExtraHotKeys;

    private static readonly Dictionary<string, string> EmptyExtraHotKeys = new();

    /// <summary>额外热键被按下。参数是热键名（不含 "工具Id." 前缀）。</summary>
    void InvokeExtra(string hotKeyName)
    {
    }
}

/// <summary>
/// 工具能用到的外壳能力。刻意只暴露这几个——工具不该能碰到别的东西。
/// </summary>
internal sealed class ToolboxContext
{
    public required SettingsStore SettingsStore { get; init; }
    public Settings Settings => SettingsStore.Current;

    public required ClipboardService Clipboard { get; init; }
    public required FocusTracker Focus { get; init; }
    public required Dispatcher Dispatcher { get; init; }

    /// <summary>弹一个气泡提示（托盘图标）。</summary>
    public required Action<string, string> Notify { get; init; }

    /// <summary>按 Id 调用某个工具（悬浮窗按钮用）。</summary>
    public required Action<string> InvokeTool { get; init; }

    /// <summary>临时把悬浮窗藏起来，返回的对象 Dispose 后恢复。大窗口工具用。</summary>
    public required Func<IDisposable> SuppressFloating { get; init; }

    /// <summary>悬浮窗需要重画按钮时调用。</summary>
    public required Action RefreshFloating { get; init; }

    /// <summary>
    /// 打开设置窗口。
    ///
    /// 为什么放在 Context 里而不是让悬浮窗自己 new SettingsWindow：
    /// 设置窗口需要 ToolRegistry（要列出所有工具的热键行）和「重新注册热键」的回调，
    /// 这两样都只有 App 拿得到。悬浮窗若自己 new，就得把 registry 也塞给它 ——
    /// 那等于让一个纯展示控件知道整个工具注册表，耦合没必要。
    /// </summary>
    public required Action OpenSettings { get; init; }

    public void SaveSettings() => SettingsStore.Save();
}
