// 插件清单 —— 主程序读它来了解插件（见 PluginContracts.cs 的说明）。
// ⚠️ 这里的 Id / 版本必须与 tools.json 里的一致，否则主程序会对账失败。

using Toolbox.Shell;

[assembly: ToolboxPlugin(
    "rename",
    "批量重命名",
    Version = "1.0.0",
    ContractsVersion = "1.0")]