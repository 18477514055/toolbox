using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Toolbox.Core;
using Toolbox.Tools.Ai;

// ============================================================================
// 真实联网验证「查找模型」+ 模型名校验的**最终行为**。
//
// 为什么必须再跑一次（而不是信离线用例）：
//   离线用例只证明了 AiCheckUi 这个颜色函数是对的；
//   但真正要回答的问题是 RemoteAiClient.CheckAsync 对用户这份配置
//   到底返回 Ok=true 还是 false ——
//   返回 false 会让面板硬拦截，等于把**本来能用的 AI 整个禁掉**。
//   这种事只有真跑一次才知道，猜不得。
//
// ⚠️ 不打印 Key。
// ============================================================================

Log.Open(Path.Combine(Path.GetTempPath(), "probe-models.log"));

var settingsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "桌面工具箱", "settings.json");

if (!File.Exists(settingsPath))
{
    Console.WriteLine("找不到 settings.json");
    return 2;
}

string url = "", key = "", model = "";

using (var doc = JsonDocument.Parse(File.ReadAllText(settingsPath)))
{
    var r = doc.RootElement;

    if (r.TryGetProperty("RemoteApiUrl", out var u)) { url = u.GetString() ?? ""; }
    if (r.TryGetProperty("RemoteApiKey", out var k)) { key = k.GetString() ?? ""; }
    if (r.TryGetProperty("RemoteModel", out var m)) { model = m.GetString() ?? ""; }
}

Console.WriteLine("=== 用户当前配置 ===");
Console.WriteLine($"  RemoteApiUrl = {url}");
Console.WriteLine($"  RemoteApiKey = {(string.IsNullOrWhiteSpace(key) ? "（空）" : $"已填，{key.Length} 字符")}");
Console.WriteLine($"  RemoteModel  = {model}");
Console.WriteLine();

// ---- 1. ModelDiscovery：查询本身 ----
Console.WriteLine("=== 1. 查询模型列表 ===");

using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var q = ModelDiscovery.QueryRemoteAsync(url, key, cts1.Token).GetAwaiter().GetResult();

Console.WriteLine($"  Ok={q.Ok}  模型数={q.Models.Count}");

var exact = false;
foreach (var m in q.Models)
{
    if (string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase)) { exact = true; break; }
}

Console.WriteLine($"  用户填的「{model}」精确存在：{(exact ? "是" : "否")}");
Console.WriteLine($"  LooksNonChat(「{model}」) = {ModelDiscovery.LooksNonChat(model)}");

var sug = ModelDiscovery.SuggestFor(q.Models, model);
Console.WriteLine($"  建议：{string.Join("、", sug)}");
Console.WriteLine();

// ---- 2. ★ 关键：CheckAsync 在这种"名字可疑"的情况下返回什么？ ----
//
//   期望是 Ok=true + ⚠ 开头的消息（黄色，不拦路）。
//   若返回 Ok=false，那就是我担心的"假红灯把功能禁掉"，必须改。
Console.WriteLine("=== 2. RemoteAiClient.CheckAsync 的实际返回 ===");

using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var client = new RemoteAiClient(url, key, model);
var check = client.CheckAsync(cts2.Token).GetAwaiter().GetResult();

Console.WriteLine($"  Ok      = {check.Ok}     ← ★必须是 True，否则 AI 会被硬拦死");
Console.WriteLine($"  带⚠前缀 = {check.Message.TrimStart().StartsWith("⚠")}");
Console.WriteLine($"  消息    = {check.Message}");
Console.WriteLine($"  颜色    = {AiCheckUi.ColorFor(check.Ok, check.Message)}");
Console.WriteLine();

var verdict = check.Ok && check.Message.TrimStart().StartsWith("⚠");
Console.WriteLine(verdict
    ? "  ✅ 行为正确：连得上但名字可疑 ⇒ 报黄不报红，AI 仍可继续使用"
    : "  ❌ 行为不对（要么被拦死了，要么没提示出来）");
Console.WriteLine();

// ---- 3. 对照：填**正确**的名字应该报绿 ----
Console.WriteLine("=== 3. 对照：用正确的模型名 ===");

var rightName = sug.Count > 0 ? sug[0] : model;

using var cts3 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var client2 = new RemoteAiClient(url, key, rightName);
var check2 = client2.CheckAsync(cts3.Token).GetAwaiter().GetResult();

Console.WriteLine($"  模型名  = {rightName}");
Console.WriteLine($"  Ok      = {check2.Ok}");
Console.WriteLine($"  带⚠前缀 = {check2.Message.TrimStart().StartsWith("⚠")}   （期望 False）");
Console.WriteLine($"  消息    = {check2.Message}");
Console.WriteLine();
Console.WriteLine("=== 完成 ===");

return verdict ? 0 : 1;
