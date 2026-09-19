using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Toolbox.Core;
using Toolbox.Shell;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Toolbox.Tools.Store;

/// <summary>一个被发现的插件（**还没实例化**）。</summary>
internal sealed class PluginCandidate
{
    /// <summary>DLL 完整路径。</summary>
    public required string DllPath { get; init; }

    /// <summary>清单里声明的 Id。</summary>
    public required string Id { get; init; }

    /// <summary>显示名。</summary>
    public required string Name { get; init; }

    /// <summary>插件声明的契约版本。</summary>
    public string ContractsVersion { get; init; } = "";

    /// <summary>插件内部版本。</summary>
    public string Version { get; init; } = "";

    /// <summary>能不能加载；不能的话原因在这里（中文，可直接显示）。</summary>
    public string? RejectReason { get; init; }

    public bool Usable => RejectReason is null;
}

/// <summary>加载结果。</summary>
internal sealed class PluginLoadReport
{
    public List<PluginCandidate> Candidates { get; } = new();
    public List<IToolboxTool> Loaded { get; } = new();
    public List<string> Errors { get; } = new();
}

/// <summary>
/// 插件加载器 —— B 阶段的核心。
///
/// ═══════════════════════════════════════════════════════════════════
/// 三条硬约束（**都经过实测**，见 DECISIONS 坑 39）
/// ═══════════════════════════════════════════════════════════════════
///
/// **① 契约必须是同一个程序集实例**
///     插件与主程序都引用 Toolbox.Contracts.dll。
///     所以发布时**必须**用 ExcludeFromSingleFile 把它留在磁盘上 ——
///     否则它被打进 exe，插件解析不到，类型身份也就无从谈起。
///
/// **② 必须挂 AssemblyResolve 解析器**
///     解析顺序：宿主已加载 → 宿主目录 → 插件目录。
///     第①步最关键：它保证插件拿到的是**宿主那份**契约，而不是自己带的一份。
///
/// **③ 必须在 STA 线程上实例化**
///     WPF 要求。真实主程序天然满足（WPF 的 UI 线程就是 STA），
///     但**任何后台线程里加载插件都会抛**
///     `InvalidOperationException: 调用线程必须为 STA`。
///     所以 <see cref="LoadAll"/> 会**主动检查**并给出明确错误，
///     而不是让它以一个看不懂的异常失败。
///
/// ⚠️ 还有一个很容易被误诊的点（实测踩了半小时）：
///     **宿主项目必须 UseWPF=true**。否则 PresentationFramework 根本不进进程，
///     插件里的 WPF 窗口一加载就报"找不到 PresentationFramework" ——
///     错误信息完全指不到"你没开 WPF"上去。
/// </summary>
internal static class PluginLoader
{
    /// <summary>插件目录（数据目录下，不在程序目录里 —— 重装程序不会丢）。</summary>
    public static string PluginsRoot => Path.Combine(AppPaths.Root, "plugins");

    /// <summary>某个插件的目录。</summary>
    public static string PluginDir(string toolId) => Path.Combine(PluginsRoot, toolId);

    /// <summary>
    /// 这个插件是否已安装（目录里有 dll）。
    ///
    /// ⚠️ 只看**文件在不在**，不看"是否加载成功" —— 两者是不同的状态：
    ///    文件在但契约版本不符时，它是"装了但不能用"。
    ///    界面要能把这两种情况分开显示，否则用户不知道该怎么办。
    /// </summary>
    public static bool IsInstalled(string toolId)
    {
        try
        {
            var dir = PluginDir(toolId);
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.dll").Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool _resolverHooked;

    /// <summary>
    /// 挂程序集解析器（**只挂一次**，重复调用是安全的）。
    ///
    /// 应当在程序启动早期调用一次。
    /// </summary>
    public static void EnsureResolver()
    {
        if (_resolverHooked)
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            // ① 宿主**已加载**的同名程序集 —— 优先级最高。
            //
            //   这一条是插件化的命门：它保证插件拿到的 Toolbox.Contracts
            //   与主程序用的是**同一个实例**，类型身份才唯一。
            //   少了它，插件里的 IToolboxTool 会被当成另一个类型，
            //   主程序认不出来（或者更糟：认出来了但转换失败）。
            foreach (var a in ctx.Assemblies)
            {
                if (string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }

            // ② 宿主目录（单文件发布时，契约 dll 就在 exe 旁边）
            var candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            if (File.Exists(candidate))
            {
                return ctx.LoadFromAssemblyPath(candidate);
            }

            // ③ 插件自己的目录（插件可能带了自己的第三方依赖）
            //
            //   注意：这里**不递归**所有插件目录 —— 那样容易让 A 插件的依赖
            //   泄漏给 B 插件，出问题极难查。每个插件想带依赖就放在自己目录里，
            //   而本解析器只在"宿主没有"时才去插件目录找。
            foreach (var dir in SafeEnumPluginDirs())
            {
                candidate = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return ctx.LoadFromAssemblyPath(candidate);
                }
            }

            return null;
        };

        _resolverHooked = true;
        Log.Line("插件程序集解析器已挂载。");
    }

    private static IEnumerable<string> SafeEnumPluginDirs()
    {
        try
        {
            var root = PluginsRoot;
            return Directory.Exists(root)
                ? Directory.GetDirectories(root)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 扫描插件目录，**只读清单、不实例化**。
    ///
    /// 先读清单再决定要不要加载，有两个实际好处：
    ///   · 可以**先判断兼容性**（契约版本不符就别实例化了，免得炸在半路）；
    ///   · 界面上能列出"装了但用不了"的插件并说明原因，而不是让它凭空消失。
    /// </summary>
    public static List<PluginCandidate> Discover()
    {
        var list = new List<PluginCandidate>();

        foreach (var dir in SafeEnumPluginDirs())
        {
            string[] dlls;

            try
            {
                dlls = Directory.GetFiles(dir, "*.dll");
            }
            catch (Exception ex)
            {
                Log.Exception($"读取插件目录失败：{dir}", ex);
                continue;
            }

            foreach (var dll in dlls)
            {
                var candidate = ReadManifest(dll);
                if (candidate is not null)
                {
                    list.Add(candidate);
                }
            }
        }

        return list;
    }

    /// <summary>
    /// 从一个 DLL 里读插件清单（[assembly: ToolboxPlugin(...)]）。
    ///
    /// ⚠️ 用 <see cref="Assembly.LoadFrom(string)"/> 而不是 LoadFromAssemblyPath：
    ///    这里只是**读元数据**，用 LoadFrom 会走默认上下文并参与解析器，
    ///    行为与真正加载时一致，读到的版本号才可信。
    ///    读完后这个程序集就留在上下文里了 —— 对插件来说是可以接受的
    ///    （同一个插件本来也只会有一次）。
    /// </summary>
    private static PluginCandidate? ReadManifest(string dllPath)
    {
        try
        {
            var asm = Assembly.LoadFrom(dllPath);

            var attr = asm.GetCustomAttribute<ToolboxPluginAttribute>();

            if (attr is null)
            {
                // 没有清单特性 ⇒ 不是我们的插件。**静默跳过**是对的：
                // 用户可能在插件目录里放了别的东西，不该报一堆错吓唬他。
                return null;
            }

            var compatible = ContractsVersion.IsCompatible(attr.ContractsVersion, out var reason);

            return new PluginCandidate
            {
                DllPath = dllPath,
                Id = attr.Id,
                Name = attr.Name,
                Version = attr.Version,
                ContractsVersion = attr.ContractsVersion,
                RejectReason = compatible ? null : reason,
            };
        }
        catch (Exception ex)
        {
            // 有清单特性但读不出来 → 也可能只是加载失败。检查一下它到底有没有特性，
            // 有的话就把失败原因报出来（否则用户完全不知道为什么插件不见了）
            try
            {
                var probe = Assembly.LoadFrom(dllPath);
                if (probe.GetCustomAttribute<ToolboxPluginAttribute>() is not null)
                {
                    return new PluginCandidate
                    {
                        DllPath = dllPath,
                        Id = Path.GetFileNameWithoutExtension(dllPath),
                        Name = Path.GetFileNameWithoutExtension(dllPath),
                        RejectReason = $"加载失败：{ex.Message}",
                    };
                }
            }
            catch
            {
                // 连探测都失败 ⇒ 当作普通文件忽略
            }

            return null;
        }
    }

    /// <summary>
    /// 加载全部可用插件并实例化。
    ///
    /// **必须在 STA 线程上调用**（见类注释的约束③）。
    /// </summary>
    /// <param name="registryIds">已注册的工具 Id（用来查冲突）。</param>
    public static PluginLoadReport LoadAll(IReadOnlyCollection<string> registryIds)
    {
        var report = new PluginLoadReport();

        // ★ 先验线程。
        //
        //   不验的话，失败会以 `InvalidOperationException: 调用线程必须为 STA`
        //   的形式从插件内部抛出来 —— 用户（和未来的我）会以为是插件坏了，
        //   而不是"调用姿势不对"。这里主动检查，错误信息直接说清怎么做。
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            report.Errors.Add(
                "插件加载必须在 STA 线程上执行（当前不是）。"
                + "这是 WPF 的要求：插件窗口属于 UI，只能在 UI 线程上创建。");
            Log.Error("插件加载被拒绝：当前线程不是 STA。");
            return report;
        }

        EnsureResolver();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in Discover())
        {
            report.Candidates.Add(candidate);

            if (!candidate.Usable)
            {
                report.Errors.Add($"「{candidate.Name}」不可用：{candidate.RejectReason}");
                Log.Line($"插件被跳过：{candidate.Id} —— {candidate.RejectReason}");
                continue;
            }

            // Id 冲突：插件不许顶掉内置工具，也不许互相顶
            if (registryIds.Contains(candidate.Id, StringComparer.OrdinalIgnoreCase)
                || !seen.Add(candidate.Id))
            {
                var why = $"工具 Id「{candidate.Id}」已被占用（内置工具或另一个插件）。";
                report.Errors.Add($"「{candidate.Name}」不可用：{why}");
                Log.Error($"插件 Id 冲突：{candidate.Id}");
                continue;
            }

            try
            {
                var asm = Assembly.LoadFrom(candidate.DllPath);

                var implType = asm.GetTypes().FirstOrDefault(t =>
                    t.IsClass && !t.IsAbstract && typeof(IToolboxTool).IsAssignableFrom(t));

                if (implType is null)
                {
                    report.Errors.Add($"「{candidate.Name}」不可用：DLL 里找不到 IToolboxTool 的实现类。");
                    continue;
                }

                var tool = (IToolboxTool)Activator.CreateInstance(implType)!;

                // 插件自己声明的 Id 必须与清单一致 ——
                // 不然"清单说它是 A、实际是 B"，用户按 A 找就找不到
                if (!string.Equals(tool.Id, candidate.Id, StringComparison.OrdinalIgnoreCase))
                {
                    report.Errors.Add(
                        $"「{candidate.Name}」不可用：清单声明的 Id（{candidate.Id}）"
                        + $"与实现类返回的（{tool.Id}）不一致。");
                    continue;
                }

                report.Loaded.Add(tool);
                Log.Line($"插件已加载：{tool.Id}（{tool.Name}）v{candidate.Version} ← {candidate.DllPath}");
            }
            catch (Exception ex)
            {
                report.Errors.Add($"「{candidate.Name}」加载失败：{ex.Message}");
                Log.Exception($"插件加载失败：{candidate.Id}", ex);
            }
        }

        return report;
    }

    /// <summary>删掉一个已安装的插件（卸载时用）。</summary>
    public static bool Remove(string toolId, out string? error)
    {
        error = null;

        try
        {
            var dir = PluginDir(toolId);

            if (!Directory.Exists(dir))
            {
                error = "这个插件没有安装。";
                return false;
            }

            // ⚠️ 已加载的程序集**无法真的卸载**（.NET 的默认上下文不支持）。
            //     所以删文件可能失败（文件被占用）—— 这时要如实告知"重启后可删"，
            //     而不是假装成功。
            Directory.Delete(dir, true);
            Log.Line($"插件已删除：{toolId}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"删除失败（可能因为插件正在运行）：{ex.Message}\n"
                    + "关掉工具箱再试，或者重启后再删。";
            Log.Exception($"删除插件失败：{toolId}", ex);
            return false;
        }
    }
}
