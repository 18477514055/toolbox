// 给第 5 个工具（快捷截图）找可用热键：扫描各组合族，报告哪些空闲。
using System;
using System.Runtime.InteropServices;

internal static class HotKeyProbe2
{
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static int _id = 9100;

    private static int Main()
    {
        Console.WriteLine("族一：Win+Shift+<字母>（已知被占 A C F M P R S T V W）");
        Scan("Win+Shift+", MOD_WIN | MOD_SHIFT, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");

        Console.WriteLine();
        Console.WriteLine("族二：Win+Alt+<字母>（交付方实测 B D F G K M R T W Y 被占）");
        Scan("Win+Alt+", MOD_WIN | MOD_ALT, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");

        Console.WriteLine();
        Console.WriteLine("族三：Ctrl+Alt+<字母>（给用户自定义留余地）");
        Scan("Ctrl+Alt+", MOD_CONTROL | MOD_ALT, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");

        Console.WriteLine();
        Console.WriteLine("族四：一些常见候选（含功能键）");
        Probe("Win+Shift+S", MOD_WIN | MOD_SHIFT, 0x53);
        Probe("Ctrl+Shift+S", MOD_CONTROL | MOD_SHIFT, 0x53);
        Probe("Alt+Shift+S", MOD_ALT | MOD_SHIFT, 0x53);
        Probe("Win+Shift+F1", MOD_WIN | MOD_SHIFT, 0x70);
        Probe("Win+Alt+S", MOD_WIN | MOD_ALT, 0x53);
        Probe("Win+Shift+`", MOD_WIN | MOD_SHIFT, 0xC0);
        Probe("PrintScreen", 0, 0x2C);
        return 0;
    }

    private static void Scan(string label, uint mods, string keys)
    {
        var free = "";
        var taken = "";
        foreach (var ch in keys)
        {
            if (TryRegister(mods, (uint)ch))
            {
                free += ch;
            }
            else
            {
                taken += ch;
            }
        }

        Console.WriteLine($"  {label}<字母>");
        Console.WriteLine($"    空闲: {(free.Length > 0 ? string.Join(" ", free.ToCharArray()) : "（无）")}");
        Console.WriteLine($"    被占: {(taken.Length > 0 ? string.Join(" ", taken.ToCharArray()) : "（无）")}");
    }

    private static void Probe(string name, uint mods, uint vk)
        => Console.WriteLine($"  {name,-16} {(TryRegister(mods, vk) ? "可注册（空闲）" : "失败（被占）")}");

    private static bool TryRegister(uint mods, uint vk)
    {
        var id = ++_id;
        var ok = RegisterHotKey(IntPtr.Zero, id, mods | MOD_NOREPEAT, vk);
        if (ok)
        {
            UnregisterHotKey(IntPtr.Zero, id);
        }

        return ok;
    }
}
