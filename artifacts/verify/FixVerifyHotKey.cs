using System.IO;
using System.Text;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

// ============================================================================
// 修复验证：热键「自动降级」现在**不再冒充用户意图**
//
// 被测逻辑逐字照抄修复后的 src\Toolbox\App.xaml.cs RegisterOne（含 isAuto 分支）。
// 修复前 ReproHotKeyFreeze.cs 在第 2 次启动处失败；这里必须全绿。
// ============================================================================

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"[通过] {name}"); }
    else { fail++; Console.WriteLine($"[失败] {name}" + (detail.Length > 0 ? $"\n        {detail}" : "")); }
}

Console.WriteLine("==================================================");
Console.WriteLine("修复验证：热键自动降级 vs 用户意图 分离");
Console.WriteLine("==================================================");
Console.WriteLine();

var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Win+Shift+V", "Win+Shift+A", "Win+Shift+C", "Win+Shift+T",
    "Win+Shift+X", "Win+Alt+S",
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

var hotKeys = new Dictionary<string, string>();
var origins = new Dictionary<string, string>();   // "user" / "auto"

bool TryRegister(string spec) => !occupied.Contains(spec);

// 照抄修复后的 RegisterOne
string RegisterOne(string fullId, string defaultSpec, bool log)
{
    hotKeys.TryGetValue(fullId, out var configured);
    var hasEntry = hotKeys.ContainsKey(fullId);
    var custom = (configured ?? "").Trim();

    origins.TryGetValue(fullId, out var o);
    var isAuto = string.Equals(o, "auto", StringComparison.OrdinalIgnoreCase);

    if (hasEntry && custom.Length == 0 && !isAuto) return "DISABLED";

    var isCustom = hasEntry && custom.Length > 0 && !isAuto;
    var spec = isCustom ? custom : defaultSpec;
    if (string.IsNullOrWhiteSpace(spec)) return "NONE";

    var success = TryRegister(spec);

    // 默认键成功 → 清掉上次自动降级的痕迹
    if (success && !isCustom && isAuto)
    {
        hotKeys.Remove(fullId);
        origins.Remove(fullId);
        if (log) Console.WriteLine($"        回到默认键并清除自动降级痕迹：{fullId} → {spec}");
    }

    if (success) return spec;

    if (!isCustom && fallbacks.TryGetValue(defaultSpec, out var chain))
    {
        foreach (var alt in chain)
        {
            if (!TryRegister(alt)) continue;
            hotKeys[fullId] = alt;
            origins[fullId] = "auto";     // ★ 修复点：标明是自动写的
            if (log) Console.WriteLine($"        自动降级：{fullId}  {defaultSpec} → {alt}（标记为 auto）");
            return alt;
        }
    }

    return "FAILED";
}

// ---------------------------------------------------------------- 场景 A
Console.WriteLine("--- 场景 A：默认键被占用 → 降级 → 占用者退出 → 应回到默认键 ---");
var r1 = RegisterOne("clipboard", "Win+Shift+V", log: true);
Check("A1 首次启动自动降级到 Win+Alt+V", r1 == "Win+Alt+V", $"实际 {r1}");
Check("A2 降级被标记为 auto（不是用户意图）", origins.GetValueOrDefault("clipboard") == "auto");

occupied.Remove("Win+Shift+V");
Console.WriteLine("        （占用者退出，Win+Shift+V 重新空闲）");

var r2 = RegisterOne("clipboard", "Win+Shift+V", log: true);
Check("★A3 默认键空闲后**回到默认键**（修复前会永远卡在备选键）",
    r2 == "Win+Shift+V", $"实际 {r2}");
Check("A4 回到默认键后残留条目被清掉（设置界面不再显示备选键）",
    !hotKeys.ContainsKey("clipboard") && !origins.ContainsKey("clipboard"),
    $"HotKeys 里还有：{(hotKeys.ContainsKey("clipboard") ? hotKeys["clipboard"] : "无")}");

// ---------------------------------------------------------------- 场景 B
Console.WriteLine();
Console.WriteLine("--- 场景 B：用户在设置里亲手指定 Win+Alt+W ---");
hotKeys["clipboard"] = "Win+Alt+W";
origins["clipboard"] = "user";

var r3 = RegisterOne("clipboard", "Win+Shift+V", log: true);
Check("B1 用户指定的键生效", r3 == "Win+Alt+W", $"实际 {r3}");
Check("B2 用户的选择不会被自动逻辑改掉", hotKeys["clipboard"] == "Win+Alt+W");

occupied.Add("Win+Alt+W");
var r4 = RegisterOne("clipboard", "Win+Shift+V", log: true);
Check("★B3 用户指定的键被占用 → 只报错、不自动换（设计意图，必须保留）",
    r4 == "FAILED", $"实际 {r4}");

// ---------------------------------------------------------------- 场景 C
Console.WriteLine();
Console.WriteLine("--- 场景 C：用户留空 = 主动禁用 ---");
hotKeys["convert"] = "";
origins["convert"] = "user";
var r5 = RegisterOne("convert", "Win+Shift+C", log: true);
Check("C1 留空仍然是禁用（不占热键）", r5 == "DISABLED", $"实际 {r5}");

// ---------------------------------------------------------------- 场景 D
Console.WriteLine();
Console.WriteLine("--- 场景 D：老设置文件（没有 HotKeyOrigins 字段）的向后兼容 ---");
// 旧版本写的 settings.json 只有 HotKeys、没有 HotKeyOrigins。
// 那份数据里被降级写进去的键，现在**无从分辨**是不是用户填的。
// 保守处理：当成"用户填的"（原样保留、不擅自改动用户的键）。
hotKeys.Clear(); origins.Clear();
hotKeys["clipboard"] = "Win+Alt+V";     // 老文件里已被降级写死的值，但无 origin 记录
var r6 = RegisterOne("clipboard", "Win+Shift+V", log: true);
Check("D1 老数据无 origin 记录 → 保守当作「用户填的」，不擅自改动",
    r6 == "Win+Alt+V", $"实际 {r6}");
Check("D2 老数据也不会被写成 auto（避免误解锁用户可能真的选过的键）",
    !origins.ContainsKey("clipboard"));

Console.WriteLine();
Console.WriteLine("--------------------------------------------------");
Console.WriteLine($"修复验证结果：{pass}/{pass + fail} 通过 —— {(fail == 0 ? "全部通过" : $"有 {fail} 项失败")}");
Console.WriteLine("--------------------------------------------------");

return fail == 0 ? 0 : 1;
