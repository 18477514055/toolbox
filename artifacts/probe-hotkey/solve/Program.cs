// ============================================================================
// 为全部工具**机械地**求一组互不冲突的热键分配。
//
// 背景：工具从 5 个涨到 11 个之后，热键手配已经撞了三次
//   （见 DECISIONS 坑 33）。手配的问题是"每加一个都要重新在脑子里核对 20 多个键"，
//   人做不到可靠。所以改成**算**：把约束写成程序，让它给一组可用解。
//
// 约束（按优先级）：
//   ① 每个工具的**默认键**必须互不相同
//   ② 每个工具的降级链里，每个键都必须互不相同（跨工具全局）
//   ③ 降级链的键**不能等于任何工具的默认键**（否则 A 降级时会去抢 B 的默认键）
//   ④ 所有键都必须落在**实测空闲**的字母上
// ============================================================================

// 实测空闲表（工具箱未运行时测，见 artifacts\probe-hotkey）
var freeWinAlt = "ACEHIJLNO P QSU V X Z".Replace(" ", "").ToCharArray();
var freeWinShift = "BDEGHIJKLNOQUXYZ".ToCharArray();

Console.WriteLine("实测空闲：");
Console.WriteLine($"  Win+Alt   : {new string(freeWinAlt)}");
Console.WriteLine($"  Win+Shift : {new string(freeWinShift)}");
Console.WriteLine();

// 工具的默认键（语义优先，尽量给人顺手的；这里固定下来不再动）
var defaults = new (string Tool, string Key)[]
{
    ("快捷剪贴板", "Win+Shift+V"),
    ("图片裁剪",   "Win+Shift+X"),
    ("格式转换",   "Win+Shift+C"),
    ("快捷 AI",    "Win+Shift+A"),
    ("翻译选中",   "Win+Shift+T"),
    ("快捷截图",   "Win+Alt+S"),
    ("OCR 识字",   "Win+Alt+T"),
    ("哈希校验",   "Win+Alt+H"),
    ("二维码",     "Win+Alt+Q"),
    ("窗口置顶",   "Win+Alt+P"),
    ("批量重命名", "Win+Alt+R"),
    ("运行命令",   "Win+Alt+X"),
};

var defaultSet = new HashSet<string>(defaults.Select(d => d.Key), StringComparer.OrdinalIgnoreCase);

// 已被默认键用掉的字母
var usedWinAlt = defaults.Where(d => d.Key.StartsWith("Win+Alt+"))
    .Select(d => d.Key[^1]).ToHashSet();
var usedWinShift = defaults.Where(d => d.Key.StartsWith("Win+Shift+"))
    .Select(d => d.Key[^1]).ToHashSet();

Console.WriteLine($"默认键用掉的 Win+Alt 字母  : {string.Join(" ", usedWinAlt.OrderBy(c => c))}");
Console.WriteLine($"默认键用掉的 Win+Shift 字母: {string.Join(" ", usedWinShift.OrderBy(c => c))}");
Console.WriteLine();

// 可用字母 = 实测空闲 - 已被默认键占用
var availWinAlt = freeWinAlt.Where(c => !usedWinAlt.Contains(c)).ToList();
var availWinShift = freeWinShift.Where(c => !usedWinShift.Contains(c)).ToList();

Console.WriteLine($"可用于**降级链**的 Win+Alt  字母: {string.Join(" ", availWinAlt)}  （{availWinAlt.Count} 个）");
Console.WriteLine($"可用于**降级链**的 Win+Shift 字母: {string.Join(" ", availWinShift)}  （{availWinShift.Count} 个）");
Console.WriteLine();

// 12 个工具，每个要 1 个降级键 ⇒ 需要 12 个互不相同的键
// Win+Alt 剩 11 个 + Win+Shift 剩 15 个 = 26 个，够
var pool = new List<string>();
foreach (var c in availWinAlt) { pool.Add($"Win+Alt+{c}"); }
foreach (var c in availWinShift) { pool.Add($"Win+Shift+{c}"); }

var need = defaults.Length;
if (pool.Count < need)
{
    Console.WriteLine($"★ 可用键不足：需要 {need}，只有 {pool.Count}");
    return 1;
}

// 分配：按工具顺序取（先 Win+Alt，语义上更接近原来那批）
// 但**排除掉会与"同族默认键"混淆的**——其实不用，因为 pool 已经排除了默认键。
var assignment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

var idx = 0;
foreach (var (tool, defKey) in defaults)
{
    while (idx < pool.Count && taken.Contains(pool[idx])) { idx++; }

    if (idx >= pool.Count)
    {
        Console.WriteLine($"★ 轮到 {tool} 时没有可用键了");
        return 1;
    }

    assignment[tool] = pool[idx];
    taken.Add(pool[idx]);
    idx++;
}

Console.WriteLine("=== 建议的降级键分配（每个都互不相同、且不撞任何默认键）===");
Console.WriteLine();
foreach (var (tool, defKey) in defaults)
{
    Console.WriteLine($"  {tool,-12} 默认 {defKey,-14} → 降级 {assignment[tool]}");
}

Console.WriteLine();
Console.WriteLine("=== 自检 ===");
var assignedValues = assignment.Values.ToList();
var dup = assignedValues.GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
    .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
Console.WriteLine(dup.Count == 0 ? "  ✅ 降级键互不重复" : $"  ★ 重复：{string.Join(", ", dup)}");

var clash = assignedValues.Where(v => defaultSet.Contains(v)).ToList();
Console.WriteLine(clash.Count == 0 ? "  ✅ 降级键不撞任何默认键" : $"  ★ 撞默认键：{string.Join(", ", clash)}");

// 都在实测空闲字母里
var badLetter = assignment.Values.Where(v =>
{
    var letter = v[^1];
    return v.StartsWith("Win+Alt+") ? !freeWinAlt.Contains(letter) : !freeWinShift.Contains(letter);
}).ToList();
Console.WriteLine(badLetter.Count == 0
    ? "  ✅ 全部落在实测空闲字母上"
    : $"  ★ 不在空闲表：{string.Join(", ", badLetter)}");

// 默认键本身也不能重复
var dupDef = defaults.GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
    .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
Console.WriteLine(dupDef.Count == 0 ? "  ✅ 默认键互不重复" : $"  ★ 默认键重复：{string.Join(", ", dupDef)}");

return 0;
