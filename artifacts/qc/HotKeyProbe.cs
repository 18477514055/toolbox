// 独立热键可注册性探针：直接调 RegisterHotKey，看真实返回与错误码。
// 用途：验证工具箱"默认热键被占用、已自动改用 XXX"的说法是不是真的。
using System;
using System.Runtime.InteropServices;

internal static class HotKeyProbe
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameTextW(int lParam, System.Text.StringBuilder lpString, int cchSize);

    private static int Main()
    {
        // 逐个测：都能注册说明这些键在本机是空闲的
        Test("Win+Shift+V", MOD_WIN | MOD_SHIFT, 0x56);
        Test("Win+Shift+X", MOD_WIN | MOD_SHIFT, 0x58);
        Test("Win+Shift+A", MOD_WIN | MOD_SHIFT, 0x41);
        Test("Win+Shift+C", MOD_WIN | MOD_SHIFT, 0x43);
        Test("Win+Shift+E", MOD_WIN | MOD_SHIFT, 0x45);
        Test("Win+Shift+Space", MOD_WIN | MOD_SHIFT, 0x20);
        Test("Win+Alt+V", MOD_WIN | MOD_ALT, 0x56);
        Test("Win+Alt+C", MOD_WIN | MOD_ALT, 0x43);
        Test("Win+Alt+A", MOD_WIN | MOD_ALT, 0x41);
        Test("Win+V (系统占用，预期失败)", MOD_WIN, 0x56);
        Test("Win+Shift+S (截图，预期失败)", MOD_WIN | MOD_SHIFT, 0x53);
        return 0;
    }

    private static void Test(string name, uint mods, uint vk)
    {
        // id 用 9000 段，避免与别的东西撞
        int id = 9000 + (int)(vk % 100);
        bool ok = RegisterHotKey(IntPtr.Zero, id, mods | MOD_NOREPEAT, vk);
        int err = Marshal.GetLastWin32Error();

        if (ok)
        {
            UnregisterHotKey(IntPtr.Zero, id);
            Console.WriteLine($"{name,-32} 可注册（空闲）");
        }
        else
        {
            Console.WriteLine($"{name,-32} 失败  Win32错误码={err} ({(err == 1409 ? "ERROR_HOTKEY_ALREADY_REGISTERED 已被占用" : "其它错误")})");
        }
    }
}
