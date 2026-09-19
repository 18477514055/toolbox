// ============================================================================
// 可行性验证：WPF 窗口放在**独立程序集**里，主程序运行时加载它，能不能真的显示？
//
// 这是 B 阶段最大的技术未知数：
//   WPF 的 XAML 会被编译成 BAML 嵌进程序集的 .g.resources，
//   运行时由生成的 InitializeComponent() 通过 `Application.LoadComponent` 去取。
//   动态加载的 DLL 没有走主程序的资源合并流程 —— 能不能取到？
//
// **必须实测**，不能靠推测。若走不通，整个"工具插件化"的方案就得换思路
// （比如"每个工具一个独立 exe"或"预编译 XAML 成代码"）。
// ============================================================================

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;

Console.WriteLine("=== WPF 插件装配可行性验证 ===");
Console.WriteLine();

var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");

Console.WriteLine($"插件目录: {pluginDir}");
Console.WriteLine($"存在: {Directory.Exists(pluginDir)}");

if (!Directory.Exists(pluginDir))
{
    Console.WriteLine("插件目录不存在 —— 无法验证");
    return 2;
}

var dlls = Directory.GetFiles(pluginDir, "*.dll");
Console.WriteLine($"DLL 数: {dlls.Length}");
foreach (var d in dlls)
{
    Console.WriteLine($"  {Path.GetFileName(d)}  {new FileInfo(d).Length:N0} B");
}
Console.WriteLine();

// 在 STA 线程上跑 WPF（不然创建 Window 会抛）
var result = 0;

var thread = new System.Threading.Thread(() =>
{
    try
    {
        // 需要一个 Application 实例（哪怕不调用 Run）
        if (Application.Current is null)
        {
            _ = new Application();
        }

        foreach (var dll in dlls)
        {
            Console.WriteLine($"--- 加载 {Path.GetFileName(dll)} ---");

            try
            {
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
                Console.WriteLine($"  程序集: {asm.GetName().Name}");

                // 列出公开类型
                var types = asm.GetTypes();
                Console.WriteLine($"  类型数: {types.Length}");

                foreach (var t in types)
                {
                    if (!typeof(Window).IsAssignableFrom(t) || t.IsAbstract)
                    {
                        continue;
                    }

                    Console.WriteLine($"  发现窗口类: {t.FullName}");

                    try
                    {
                        // ★ 关键：真的 new 一个出来（会走 InitializeComponent → LoadComponent）
                        var win = (Window)Activator.CreateInstance(t)!;
                        Console.WriteLine($"    ✅ 实例化成功：Title=「{win.Title}」");

                        // 再确认它的内容真的被 XAML 填充了（而不是空壳）
                        var hasContent = win.Content is not null;
                        Console.WriteLine($"    ✅ Content 已加载: {hasContent}");

                        win.Close();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    ❌ 实例化失败: {ex.GetType().Name}: {ex.Message}");
                        if (ex.InnerException is not null)
                        {
                            Console.WriteLine($"       内部: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                        }
                        result = 1;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 加载失败: {ex.GetType().Name}: {ex.Message}");
                result = 1;
            }

            Console.WriteLine();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"线程异常: {ex}");
        result = 1;
    }
});

thread.SetApartmentState(System.Threading.ApartmentState.STA);
thread.Start();
thread.Join();

Console.WriteLine("--------------------------------------------------");
Console.WriteLine(result == 0 ? "结论：WPF 插件装配**可行**" : "结论：有问题，见上面");
return result;
