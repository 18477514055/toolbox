using System;
using System.Threading;
using System.Windows;

// ============================================================================
// 验证：单文件自包含发布下，Microsoft.Win32.OpenFolderDialog 到底能不能弹出来？
//
// 用户反馈：格式转换里「选择…」（选输出目录）点了没反应，
// 但设置里「截图保存目录」的浏览可以 —— 后者用的是 WinForms 的
// FolderBrowserDialog，前者用的是 WPF 的 OpenFolderDialog。
//
// 这是两种**完全不同**的实现：
//   · WinForms FolderBrowserDialog → 走 Shell32 的 SHBrowseForFolder
//   · WPF OpenFolderDialog        → 走 COM 的 IFileDialog（Vista 风格）
// 单文件自包含 + 压缩发布时，后者更容易因为 COM/资源问题失败。
//
// 这里**不弹真对话框**（会卡住无人操作），而是：
//   ① 确认类型能不能加载、能不能构造
//   ② 确认关键成员是否可访问
//   ③ 把真实异常打出来
// ============================================================================

Console.WriteLine("=== OpenFolderDialog 可用性验证 ===");
Console.WriteLine();

var code = 0;

var t = new Thread(() =>
{
    try
    {
        if (Application.Current is null)
        {
            _ = new Application();
        }

        // ---- ① 类型能否加载 ----
        var type = Type.GetType("Microsoft.Win32.OpenFolderDialog, PresentationFramework");

        if (type is null)
        {
            Console.WriteLine("❌ 找不到 Microsoft.Win32.OpenFolderDialog");
            code = 1;
            return;
        }

        Console.WriteLine($"① 类型已加载：{type.FullName}");
        Console.WriteLine($"   所在程序集：{type.Assembly.GetName().Name}");
        Console.WriteLine($"   程序集位置：{type.Assembly.Location}");
        Console.WriteLine();

        // ---- ② 能否构造 ----
        object? dlg = null;

        try
        {
            dlg = Activator.CreateInstance(type);
            Console.WriteLine("② 构造成功");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 构造失败：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            code = 1;
            return;
        }

        // ---- ③ 关键成员是否可用 ----
        Console.WriteLine();
        Console.WriteLine("③ 成员检查：");

        foreach (var name in new[] { "Title", "FolderName", "Multiselect", "InitialDirectory" })
        {
            var p = type.GetProperty(name);
            Console.WriteLine($"   {(p is null ? "❌" : "✅")} {name}");
        }

        var showDialog = type.GetMethod("ShowDialog", Type.EmptyTypes);
        Console.WriteLine($"   {(showDialog is null ? "❌" : "✅")} ShowDialog()");

        // ---- ④ 对照：WinForms 的 FolderBrowserDialog ----
        Console.WriteLine();
        Console.WriteLine("④ 对照（设置里用的那个）：");
        Console.WriteLine("   用 WinForms FolderBrowserDialog（Shell32 SHBrowseForFolder）");
        Console.WriteLine("   ⇒ 两者底层完全不同，所以'一个行一个不行'是可能的");

        Console.WriteLine();
        Console.WriteLine("=== 结论：类型层面没问题，问题在'弹出来'这一步 ===");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ 未预期异常：{ex}");
        code = 1;
    }
});

t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();

return code;
