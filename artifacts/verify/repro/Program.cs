using System.IO;
using System.Text;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

// ============================================================================
// 复现：热键「自动降级」把降级结果**当成用户的选择**写回 settings.json
//
// 断言的问题（对应 src\Toolbox\App.xaml.cs:688）：
//     _settingsStore.Current.HotKeys[fullId] = alt;
//     _settingsStore.Save();
//
// 一旦写回，下次启动 hasEntry==true → isCustom==true
// ⇒ ① 那个键从此被当成"用户自己改过的键"，默认键被占用时**只报错、不降级**
//    ② 用户即使想恢复默认，也必须先去设置里手填「默认」两个字
//    ③ 更糟：原来那次降级的原因是"当时另一个程序占着默认键"。
//       那个程序后来退出了、默认键空出来了，工具箱也**永远不会回去用它** ——
//       用户被永久钉在一个更差的备选键上，而且界面上看不出这是"被自动改的"。
//
// 本程序用**纯逻辑复算**演示这条链，不依赖真实注册热键。
// ============================================================================

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"[通过] {name}"); }
    else { fail++; Console.WriteLine($"[失败] {name}" + (detail.Length > 0 ? $"\n        {detail}" : "")); }
}

Console.WriteLine("==================================================");
Console.WriteLine("复现：热键自动降级 → 写回设置 → 永久冻结");
Console.WriteLine("==================================================");
Console.WriteLine();

// --- 被测的真实逻辑（逐字照抄 App.xaml.cs RegisterOne 的判定分支）---
// 为了不引真实注册表/Win32，这里把 RegisterHotKey 换成一个可编程的"占用表"。
var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Win+Shift+V", "Win+Shift+A", "Win+Shift+C", "Win+Shift+T", // 本机实测被占
    "Win+Shift+X", "Win+Alt+S",                                   // 空闲
};

var fallbacks = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
{
    ["Win+Shift+V"] = ["Win+Alt+V", "Win+Shift+U"],
    ["Win+Shift+X"] = ["Win+Alt+X", "Win+Shift+K"],
    ["Win+Shift+C"] = ["Win+Alt+C", "Win+Shift+G"],
    ["Win+Shift+A"] = ["Win+Alt+A", "Win+Shift+I"],
    ["Win+Shift+T"] = ["Win+Shift+E", "Win+Alt+E"],
    ["Win+Alt+S"] = ["Win+Alt+Z", "Win+Alt+Q"],
};

// settings.json 的内存模型（HotKeys 字典）
var settingsHotKeys = new Dictionary<string, string>();

bool TryRegister(string spec) => !occupied.Contains(spec);

// 照抄 RegisterOne 的语义
string RegisterOne(string fullId, string defaultSpec, bool log)
{
    settingsHotKeys.TryGetValue(fullId, out var configured);
    var hasEntry = settingsHotKeys.ContainsKey(fullId);
    var custom = (configured ?? "").Trim();

    if (hasEntry && custom.Length == 0) return "DISABLED";

    var isCustom = hasEntry && custom.Length > 0;
    var spec = isCustom ? custom : defaultSpec;
    if (string.IsNullOrWhiteSpace(spec)) return "NONE";

    if (TryRegister(spec)) return spec; // 注册成功

    // ★ 这就是被质疑的那一段
    if (!isCustom && fallbacks.TryGetValue(defaultSpec, out var chain))
    {
        foreach (var alt in chain)
        {
            if (!TryRegister(alt)) continue;
            settingsHotKeys[fullId] = alt;   // ← 写回设置（App.xaml.cs:688）
            if (log) Console.WriteLine($"        自动降级：{fullId}  {spec} → {alt}（并写回 settings.json）");
            return alt;
        }
    }

    return "FAILED";
}

// ---------------------------------------------------------------- 第 1 次启动
Console.WriteLine("--- 第 1 次启动（默认键被占用）---");
var r1 = RegisterOne("clipboard", "Win+Shift+V", log: true);
Check("首次启动：自动降级到 Win+Alt+V", r1 == "Win+Alt+V", $"实际 {r1}");
Check("降级结果被写回 settings.json（交付方的既有行为）",
    settingsHotKeys.TryGetValue("clipboard", out var v1) && v1 == "Win+Alt+V",
    $"settings.json 里现在是：{v1}");
Console.WriteLine();

// ---------------------------------------------------------------- 关键：占用者退出了
Console.WriteLine("--- 占用 Win+Shift+V 的那个程序退出了（默认键重新空闲）---");
occupied.Remove("Win+Shift+V");
Check("默认键 Win+Shift+V 现在空闲", TryRegister("Win+Shift+V"));
Console.WriteLine();

// ---------------------------------------------------------------- 第 2 次启动
Console.WriteLine("--- 第 2 次启动（重新按设置注册）---");
var r2 = RegisterOne("clipboard", "Win+Shift+V", log: true);
Check("★ 默认键已空闲，工具箱应回到默认键 Win+Shift+V",
    r2 == "Win+Shift+V",
    $"实际仍然用 {r2} —— 因为上次降级把它写进了 settings.json，" +
    "现在被当成「用户自己改过的键」，代码明确不覆盖用户的选择。");

// ---------------------------------------------------------------- 第 3 次：默认键又被占
Console.WriteLine();
Console.WriteLine("--- 第 3 次：另一个程序又占了 Win+Shift+V ---");
occupied.Add("Win+Shift+V");
// 此时 Win+Alt+V 也被占（模拟用户装了别的软件）
occupied.Add("Win+Alt+V");
var r3 = RegisterOne("clipboard", "Win+Shift+V", log: true);
Check("★ 用户改过的键被占用时应「只报错、不自动换」——这是设计意图",
    r3 == "FAILED",
    $"实际 {r3}。注意：这条『不自动换』是设计意图，但它现在作用在一个**用户从没选过**的键上 ——" +
    "用户完全不知道自己的键是什么时候、被谁改成了 Win+Alt+V。");

Console.WriteLine();
Console.WriteLine("--------------------------------------------------");
Console.WriteLine($"结果：{pass}/{pass + fail} 通过");
Console.WriteLine("--------------------------------------------------");
Console.WriteLine();
Console.WriteLine("结论：『降级结果写回 settings.json』这行代码把**系统的兜底动作**"
                + "混进了**用户意图**所在的字段。");
Console.WriteLine("      DECISIONS.md 坑 22 已经写明『同一个字段别承载两种语义』，"
                + "这里正是同一个错误的另一个实例 —— 只是当时没被发现。");

return fail == 0 ? 0 : 1;
