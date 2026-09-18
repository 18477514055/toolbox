using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// ============================================================================
// 热键占用探针（工具箱**未运行**时测）
//
// 目的：给新增的 5 个工具挑不冲突的热键与降级链。
// 项目 DECISIONS §七 里那张占用表是 5 个工具时代测的，现在要重测，
// 因为**工具箱自己注册的键也会显示为"被占用"**——必须先确保它没在跑。
// ============================================================================

const int MOD_ALT = 0x0001;
const int MOD_CONTROL = 0x0002;
const int MOD_SHIFT = 0x0004;
const int MOD_WIN = 0x0008;
const int MOD_NOREPEAT = 0x4000;

[DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
[DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

Console.WriteLine("=== 热键占用探测（各字母族）===");
Console.WriteLine();

// 先确认工具箱没在跑
var running = System.Diagnostics.Process.GetProcessesByName("Toolbox");
Console.WriteLine($"工具箱进程数：{running.Length}  {(running.Length == 0 ? "✅ 未运行，测量有效" : "★ 正在运行，测到的'占用'可能是它自己")}");
Console.WriteLine();

string[] families =
{
    "Win+Alt", "Win+Shift", "Ctrl+Alt", "Ctrl+Shift", "Win+Ctrl",
};

var results = new Dictionary<string, (List<char> Free, List<char> Taken)>();

foreach (var family in families)
{
    uint mods = family switch
    {
        "Win+Alt" => MOD_WIN | MOD_ALT,
        "Win+Shift" => MOD_WIN | MOD_SHIFT,
        "Ctrl+Alt" => MOD_CONTROL | MOD_ALT,
        "Ctrl+Shift" => MOD_CONTROL | MOD_SHIFT,
        "Win+Ctrl" => MOD_WIN | MOD_CONTROL,
        _ => 0,
    };

    var free = new List<char>();
    var taken = new List<char>();

    for (var c = 'A'; c <= 'Z'; c++)
    {
        var vk = (uint)c;
        var id = 9000 + (int)c;

        if (RegisterHotKey(IntPtr.Zero, id, mods | MOD_NOREPEAT, vk))
        {
            free.Add(c);
            UnregisterHotKey(IntPtr.Zero, id);
        }
        else
        {
            taken.Add(c);
        }
    }

    results[family] = (free, taken);

    Console.WriteLine($"{family,-12} 空闲: {string.Join(" ", free)}");
    Console.WriteLine($"{"",-12} 占用: {string.Join(" ", taken)}");
    Console.WriteLine();
}

// 对照组：验证探针本身有效
Console.WriteLine("=== 对照组（应失败）===");
var ctrlShiftF9 = RegisterHotKey(IntPtr.Zero, 9999, MOD_CONTROL | MOD_SHIFT, 0x78); // Ctrl+Shift+F9
Console.WriteLine($"Ctrl+Shift+F9 注册: {(ctrlShiftF9 ? "成功（空闲）" : "失败")}  ← 用于验证探针工作正常");
if (ctrlShiftF9) { UnregisterHotKey(IntPtr.Zero, 9999); }

Console.WriteLine();
Console.WriteLine("--------------------------------------------------");
Console.WriteLine("本工具新增 5 个工具需要的热键：");
foreach (var want in new[] { "Win+Alt+T (OCR)", "Win+Alt+R (重命名)", "Win+Alt+H (哈希)",
                             "Win+Alt+Q (二维码)", "Win+Alt+P (置顶)" })
{
    Console.WriteLine($"  {want}");
}
Console.WriteLine();
Console.WriteLine("Win+Alt 族空闲情况见上表 —— 据此挑键与降级链。");

return 0;
