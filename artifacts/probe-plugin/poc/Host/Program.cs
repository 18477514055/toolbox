using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Poc.Contracts;

namespace Poc.Host;

/// <summary>宿主实现：插件通过 IPocHost 接口回调它。</summary>
internal sealed class HostImpl : IPocHost
{
    private readonly Dictionary<string, string> _settings = new()
    {
        ["theme"] = "dark",
        ["lang"] = "zh-CN",
    };

    public string GetSetting(string key) => _settings.TryGetValue(key, out var v) ? v : "(未设置)";

    public void Log(string message) => Console.WriteLine($"      [宿主日志] {message}");
}

internal static class Program
{
    private static int Main()
    {
        // ★ WPF 要求 UI 线程是 STA。真实的工具箱主程序天然是 STA
        //   （WPF 的 App 入口就是 [STAThread]），POC 里要自己起一个。
        var code = 0;
        var t = new System.Threading.Thread(() => code = Run());
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join();
        return code;
    }

    private static int Run()
    {
        Console.WriteLine("=== 三方 POC：宿主 + 契约 + 插件 ===");

        // ★ 关键：挂一个程序集解析器。
        //
        //   为什么必需（实测踩到）：
        //     插件 DLL 里如果有 WPF 窗口，它会引用 PresentationFramework。
        //     而宿主若是**自包含单文件**发布，运行时被打进 exe 内部，
        //     磁盘上并没有 PresentationFramework.dll ⇒ 默认解析器找不到
        //     ⇒ 插件加载直接 ReflectionTypeLoadException。
        //
        //   解析顺序：
        //     ① 宿主**已经加载**的同名程序集（最优先 —— 保证类型身份一致）
        //     ② 宿主目录里的 dll
        //     ③ 插件自己目录里的 dll（插件自带的依赖）
        var baseDir = AppContext.BaseDirectory;
        var probeDir = Path.Combine(baseDir, "plugins");

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            // ① 宿主已加载的 —— 保证同一个类型身份（这条最重要）
            foreach (var a in ctx.Assemblies)
            {
                if (string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }

            // ② 宿主目录
            var cand = Path.Combine(baseDir, name.Name + ".dll");
            if (File.Exists(cand))
            {
                return ctx.LoadFromAssemblyPath(cand);
            }

            // ③ 插件目录
            cand = Path.Combine(probeDir, name.Name + ".dll");
            if (File.Exists(cand))
            {
                return ctx.LoadFromAssemblyPath(cand);
            }

            return null;
        };

        Console.WriteLine($"[解析器已挂载，宿主已加载 {AssemblyLoadContext.Default.Assemblies.Count()} 个程序集]");
        Console.WriteLine();

        var pluginDir = probeDir;

        if (!Directory.Exists(pluginDir))
        {
            Console.WriteLine($"插件目录不存在：{pluginDir}");
            return 2;
        }

        var host = new HostImpl();
        var loaded = 0;

        foreach (var dll in Directory.GetFiles(pluginDir, "*.dll"))
        {
            Console.WriteLine($"--- {Path.GetFileName(dll)} ---");

            try
            {
                // ★ 关键点 1：插件与宿主必须共用**同一个契约程序集实例**。
                //
                //   默认 AssemblyLoadContext 对"已加载的同名程序集"会复用，
                //   所以只要宿主自己已经引用了 Poc.Contracts，
                //   插件再去引用它时就会命中同一个 —— 类型才能对得上。
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
                Console.WriteLine($"  程序集: {asm.GetName().Name}");

                foreach (var t in asm.GetTypes())
                {
                    if (!typeof(IPocTool).IsAssignableFrom(t) || t.IsAbstract || t.IsInterface)
                    {
                        continue;
                    }

                    Console.WriteLine($"  发现工具: {t.FullName}");

                    var tool = (IPocTool)Activator.CreateInstance(t)!;
                    Console.WriteLine($"    Id   = {tool.Id}");
                    Console.WriteLine($"    Name = {tool.Name}");

                    // ★ 关键点 2：Attach 传入宿主实现，插件用它读设置/写日志。
                    //   类型必须跨程序集边界"认得"——这一步过了就证明契约方案成立。
                    tool.Attach(host);
                    loaded++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException is not null)
                {
                    Console.WriteLine($"     内部: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                return 1;
            }

            Console.WriteLine();
        }

        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine(loaded > 0
            ? $"结论：契约方案**成立**，成功装载 {loaded} 个插件（跨程序集类型匹配正常）"
            : "结论：没装到任何插件");

        return loaded > 0 ? 0 : 1;
    }
}
