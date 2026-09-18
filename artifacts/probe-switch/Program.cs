using System;
using System.Collections.Generic;
using System.Text.Json;
using Toolbox.Shell;

// ============================================================================
// 定位：为什么"默认只开 5 个"在自检里通过，实际启动却 12 个全开？
//
// 自检用的是 `new Settings()`（走 C# 初始化器）；
// 真实路径是 **JSON 反序列化**。这两条路对"缺失属性"的处理可能不同 ——
// 这正是要验的。
// ============================================================================

Console.WriteLine("=== 反序列化路径验证 ===");
Console.WriteLine();

var options = new JsonSerializerOptions
{
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = false,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
};

// ① 模拟用户真实的 settings.json：**没有** ToolsEnabled / ToolEnabled
var legacyJson = """
{
  "HotKeys": { "clipboard": "Win+Alt+V" },
  "StartWithWindows": true,
  "MaxEntries": 2000
}
""";

var s = JsonSerializer.Deserialize<Settings>(legacyJson, options)!;

Console.WriteLine("① 反序列化一个没有新字段的老配置：");
Console.WriteLine($"   ToolsEnabled = {s.ToolsEnabled}   （期望 True）");
Console.WriteLine($"   ToolEnabled  = {(s.ToolEnabled is null ? "null ★" : s.ToolEnabled.Count + " 条")}");
Console.WriteLine();

// ② 逐个问 ToolGate
Console.WriteLine("② ToolGate 对 12 个工具的判断：");
var ids = new[] { "clipboard", "image", "convert", "ai", "screenshot",
                  "ocr", "rename", "hash", "qrcode", "topmost", "run", "archive" };

foreach (var id in ids)
{
    var on = ToolGate.IsEnabled(s, id);
    var def = ToolGate.IsDefaultOn(id);
    Console.WriteLine($"   {id,-12} 默认={(def ? "开" : "关"),-2}  判定={(on ? "启用" : "停用"),-4}  {(on == def ? "" : "★ 与默认不一致")}");
}

Console.WriteLine();
var enabledCount = 0;
foreach (var id in ids) { if (ToolGate.IsEnabled(s, id)) { enabledCount++; } }
Console.WriteLine($"   实际启用数 = {enabledCount} / 12");
Console.WriteLine();

// ③ 再走一遍真实的 SettingsStore.Load 路径看看
Console.WriteLine("③ 直接用 SettingsStore 读磁盘上的真实 settings.json：");
try
{
    var store = new SettingsStore();
    var real = store.Current;

    Console.WriteLine($"   文件: {store.FilePath}");
    Console.WriteLine($"   ToolsEnabled = {real.ToolsEnabled}");
    Console.WriteLine($"   ToolEnabled 条目 = {real.ToolEnabled.Count}");

    var c = 0;
    foreach (var id in ids) { if (ToolGate.IsEnabled(real, id)) { c++; } }
    Console.WriteLine($"   实际启用数 = {c} / 12   {(c == 5 ? "✅ 符合预期" : "★ 不是 5")}");

    Console.WriteLine();
    Console.WriteLine("   逐个：");
    foreach (var id in ids)
    {
        Console.WriteLine($"     {id,-12} {(ToolGate.IsEnabled(real, id) ? "启用" : "停用")}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"   抛异常：{ex.GetType().Name}: {ex.Message}");
}

return 0;
