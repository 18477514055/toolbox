using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Toolbox.Core;
using Toolbox.Shell;
using Toolbox.Tools.Ai;
using Toolbox.Tools.Clipboard;
using Toolbox.Tools.Convert;
using Toolbox.Tools.BatchRename;
using Toolbox.Tools.HashCheck;
using Toolbox.Tools.ImageCrop;
using Toolbox.Tools.Ocr;
using Toolbox.Tools.QrCode;
using Toolbox.Tools.Screenshot;
using Toolbox.Tools.Archiver;
using Toolbox.Tools.CommandRunner;
using Toolbox.Tools.WindowTopmost;

namespace Toolbox;

/// <summary>
/// 应用生命周期。
///
/// 启动顺序是有讲究的（见 04-交接文档.md §七 的「为什么这个顺序」）：
///   悬浮窗和「我们自己写的不进历史」标记必须在 P1 就做掉——
///   前者是主要入口，工具会一个个挂上去；后者是 AI 工具的前置条件，
///   晚做会先污染一批用户数据再返工。
/// </summary>
internal partial class App : Application
{
    private const string MutexName = "Global\\Toolbox_桌面工具箱_SingleInstance";
    private const string ShowEventName = "Global\\Toolbox_桌面工具箱_Show";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _showWatcher;

    private SettingsStore? _settingsStore;
    private ClipboardService? _clipboard;
    private FocusTracker? _focus;
    private HotKeyManager? _hotKeys;
    private TrayIcon? _tray;
    private ToolRegistry? _registry;
    private FloatingWindow? _floating;
    private ToolboxContext? _ctx;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ★★ 第一件事：决定数据放在哪（常规 %LOCALAPPDATA% / 便携 exe 旁边）。
        //
        // 必须抢在**任何** AppPaths 成员被访问之前 —— 否则 SettingsFile / LogDir
        // 会先按常规模式把目录建出来，之后再改 Root 就晚了。
        // 所以这行放在最前面，连自检分支都在它后面。
        AppPaths.InitializeMode(e.Args.Any(a =>
            a.Equals("--portable", StringComparison.OrdinalIgnoreCase)));

        // 自检模式：不建界面、不常驻，跑完就退。给自动化验收用。
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var code = SelfTest.Run(e.Args);
            Shutdown(code);
            return;
        }

        // 开机自启开关：同样是纯命令行，不建界面、不常驻。
        if (TryRunAutoStart(e.Args, out var autoStartCode))
        {
            Shutdown(autoStartCode);
            return;
        }

        // ---------- 启动哨兵（必须放在最早期） ----------
        // 用独立于主日志的方式记一行，这样"到底有没有启动过、启动到哪一步"永远有答案。
        WriteStartupSentinel(e.Args);
        var isStartupLaunch = e.Args.Any(a =>
            a.Equals(AutoStart.StartupArgument, StringComparison.OrdinalIgnoreCase));

        // ---------- 单实例 ----------
        _singleInstanceMutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // 已经有实例在跑：让它把悬浮窗显示出来，然后自己退出
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch
            {
                // 通知失败不影响"不重复启动"这个主要目的
            }

            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Exception("未处理异常（AppDomain）", args.ExceptionObject as Exception ?? new Exception("未知"));

        // ---------- 基础设施 ----------
        Log.Open(AppPaths.LogFile);
        Log.Line();
        Log.Line("================ 桌面工具箱启动 ================");
        Log.Line($"版本     : {typeof(App).Assembly.GetName().Version}");
        Log.Line($"数据目录 : {AppPaths.Root}{(AppPaths.IsPortable ? "（便携模式：数据在 exe 旁边）" : "")}");
        Log.Line($"日志文件 : {AppPaths.LogFile}");
        Log.Line($"启动方式 : {(isStartupLaunch ? "开机自启（" + AutoStart.StartupArgument + "）" : "手动启动")}");
        Log.Line($"工作目录 : {Environment.CurrentDirectory}");

        // ---------- 自启路径自愈 ----------
        // Run 键里存的是**绝对路径**，指向哪个 exe 就是哪个。常见的坏法有两种：
        //   ① 程序被移动 / 目录改过名 → 注册表里那条路径已经不存在，开机静默失败；
        //   ② 注册表指向的是**另一个构建产物**（比如开发时的 Debug 输出），
        //      交付的是 dist\Toolbox.exe，开机却拉起一个源码目录里的旧 exe。
        //      这个更阴险：它"能跑"，所以看起来一切正常，但一旦源码目录被清理 / 挪走，
        //      自启就死了 —— 而且你根本想不到去看注册表。
        //
        // ★ 自愈必须在**每一次启动**时做，不能只在自启启动时做。
        //   因为①和②的后果正是"自启启动根本不会发生"——
        //   只在自启启动时自愈，等于永远等不到那一次调用。
        //   所以：只要自启是开着的，就把注册表校正成当前这个 exe。
        if (AutoStart.IsEnabled())
        {
            var before = AutoStart.ReadRegisteredCommand(out _);
            if (AutoStart.EnsurePointsAtCurrentExe())
            {
                // 改的是系统状态，必须让用户看见一次 —— 但用托盘气泡而不是模态框：
                // 开机时弹窗既打断人，又长得像"启动失败"。
                _startupWarnings.Add(
                    "开机自启原来指向的不是当前这个程序，已自动改正。\n"
                    + $"原指向：{AutoStart.ExtractPath(before ?? "")}\n"
                    + $"已改为：{AutoStart.ExecutablePath}");
            }
        }

        // ---------- 自启模式：延迟初始化 ----------
        // 登录瞬间，磁盘 / 注册表 / 剪贴板服务 / Shell 都在忙。这时候抢
        // AddClipboardFormatListener / RegisterHotKey / 消息窗口，失败率明显高于平时。
        // 等几秒再抢，成功率高很多；代价只是托盘图标晚几秒出现。
        //
        // 用 DispatcherTimer 而不是 Thread.Sleep：不阻塞 UI 线程，
        // OnStartup 能立刻返回，消息循环正常起来。
        if (isStartupLaunch && StartupDelaySeconds > 0)
        {
            Log.Line($"自启模式：延迟 {StartupDelaySeconds} 秒后初始化（避开开机繁忙期）。");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(StartupDelaySeconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Log.Line("延迟结束，开始初始化。");
                InitializeCore();
            };
            timer.Start();
            return;
        }

        InitializeCore();
    }

    /// <summary>
    /// 真正的初始化：从建基础设施一路做到"启动完成"。
    ///
    /// 单独抽出来，是因为自启模式要**延迟**调用它（见 OnStartup 里的说明）。
    /// </summary>
    private void InitializeCore()
    {
        _settingsStore = new SettingsStore();

        _clipboard = new ClipboardService { IsPaused = _settingsStore.Current.Paused };
        StartClipboardWithRetry();

        _focus = new FocusTracker();
        _focus.Start();

        _hotKeys = new HotKeyManager();
        _hotKeys.HotKeyPressed += OnHotKeyPressed;

        // ---------- 工具注册 ----------
        //
        // ⚠️ 只有这 5 个是**随主程序内置**的 —— 正好是 ToolGate 里默认开启的那 5 个。
        //
        // 为什么是这 5 个（而不是全部、也不是只留剪贴板）：
        //   · 主程序开箱就得能用 —— 装完发现一个工具都没有，第一印象太差；
        //   · 剪贴板是**托盘与悬浮窗的支点**（悬浮窗用它取历史、托盘菜单以它为主），
        //     它若也是插件，不装插件时的行为会变得很别扭；
        //   · 这 5 个恰好覆盖"几乎每天都会用"的那一类。
        //
        // 其余 7 个（OCR / 重命名 / 哈希 / 二维码 / 窗口置顶 / 运行命令 / 批量打包）
        // 已拆成**可下载插件**，见下方 LoadPlugins()。
        // 它们的源码仍在 src\Toolbox\Tools\<名>\ ——
        // 由 plugins-src\ 下对应的插件工程用 <Compile Include Link> **共享同一份**，
        // 所以主程序不编译它们，也不会产生两份实现。
        _registry = new ToolRegistry();
        _registry.Add(new ClipboardTool());
        _registry.Add(new ImageCropTool());
        _registry.Add(new ConvertTool());
        _registry.Add(new AiTool());
        _registry.Add(new ScreenshotTool());

        // ⚠️ 注意：`WindowTopmostTool` **不再在这里注册**了。
        //
        //   从 B 阶段起，「窗口置顶」被拆成了**可下载插件**（plugins\topmost\），
        //   用来跑通整条插件链路。它的源码仍在 src\Toolbox\Tools\WindowTopmost\
        //   （插件工程用 <Compile Include Link> 共享同一份，杜绝分叉），
        //   但主程序**不再把它编进自己**。
        //
        //   为什么不保留"内置 + 插件"双份：那会造成 Id 冲突，
        //   加载器会明确拒绝插件（这条守卫已实测有效）。
        //   而这正是"按需下载"该有的样子 —— 用户没装，就没这个功能。

        // ---------- 插件加载（B 阶段）----------
        //
        // ★ 必须在**这里**做，也就是 `OnStartup` 里（UI 线程 = STA）。
        //   插件里的窗口是 WPF 对象，只能建在 STA 线程上。
        //   放到任何后台线程都会以 `调用线程必须为 STA` 失败。
        //
        // ★ 顺序也重要：要在 **工具注册之后、StartAll 之前**。
        //   这样插件工具能和内置工具一起进入同一套
        //   "开关判定 / 热键注册 / 悬浮窗显示"流程，不需要任何特殊照顾。
        LoadPlugins();

        _ctx = new ToolboxContext
        {
            SettingsStore = _settingsStore,
            Clipboard = _clipboard,
            Focus = _focus,
            Dispatcher = Dispatcher,
            Notify = (title, message) => _tray?.Notify(title, message),
            InvokeTool = id =>
            {
                var tool = _registry.Find(id);
                if (tool is null)
                {
                    Log.Error($"找不到工具：{id}");
                    return;
                }

                // ★ 关掉的工具不许被调用（哪怕从托盘菜单 / 命令行 --open 来）。
                //
                //   为什么要在这里挡：界面上虽然已经把入口藏了，但
                //   `--open xxx` 命令行参数、以及用户改设置前留下的旧快捷方式
                //   仍然可能调进来。给一句**明确的**提示，比让它默默什么都不做强 ——
                //   用户至少能知道"是它被关了"而不是"工具箱坏了"。
                if (!ToolGate.IsEnabled(_settingsStore!.Current, id))
                {
                    var why = _settingsStore.Current.ToolsEnabled
                        ? $"「{tool.Name}」当前是关闭的。可以在「设置 → 功能开关」里打开它。"
                        : $"工具箱的总开关当前是关闭的，所有功能都停了。\n"
                          + "可以在托盘图标右键 → 设置 → 功能开关 里重新打开。";

                    Log.Line($"工具调用被拒绝（已关闭）：{id}");
                    _tray?.Notify("桌面工具箱", why);
                    return;
                }

                IDisposable? suppress = null;
                if (tool.OpensBigWindow && _floating is not null)
                {
                    suppress = _floating.Suppress();
                }

                try
                {
                    tool.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Exception($"工具 {id} 执行失败", ex);
                    _tray?.Notify("桌面工具箱", $"「{tool.Name}」执行失败：{ex.Message}");
                }
                finally
                {
                    suppress?.Dispose();
                }
            },
            SuppressFloating = () => _floating?.Suppress() ?? new NoopDisposable(),
            RefreshFloating = () => _floating?.BuildButtons(),
            OpenSettings = OpenSettings,
            ReloadPlugins = ReloadPlugins,
            ApplyToolSwitches = ApplyToolSwitches,
        };

        // 记下"实际起来的工具"，供 ApplyToolSwitches 判断谁该停
        foreach (var id in _registry.StartAll(_ctx, _settingsStore.Current))
        {
            _toolsRunning.Add(id);
        }

        // ---------- 托盘 ----------
        _tray = new TrayIcon();
        _tray.OpenClipboardRequested += () => _ctx.InvokeTool("clipboard");
        _tray.ToggleFloatingRequested += () => _floating?.ToggleVisibility();
        _tray.SettingsRequested += OpenSettings;
        _tray.ExitRequested += () => Shutdown(0);
        _tray.PauseToggled += paused =>
        {
            _clipboard.IsPaused = paused;
            _settingsStore.Current.Paused = paused;
            _settingsStore.Save();
            Log.Line(paused ? "剪贴板监听已暂停。" : "剪贴板监听已恢复。");
        };
        _tray.Show(_settingsStore.Current);

        // 填充托盘的「所有工具」子菜单 + 同步总开关状态
        RefreshTrayToolsMenu();
        _tray.SetToolsEnabled(_settingsStore.Current.ToolsEnabled);

        // 启动期的警告在这里统一说 —— 用托盘气泡，不用模态框（见 _startupWarnings 的说明）。
        // 排到 Dispatcher 队列末尾：托盘图标刚 Show，立刻弹气泡可能被系统丢掉。
        if (_startupWarnings.Count > 0)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var warning in _startupWarnings)
                {
                    _tray?.Notify("桌面工具箱", warning);
                }
            }));
        }

        // ---------- 悬浮窗（主要入口） ----------
        _floating = new FloatingWindow(_ctx, _registry);
        if (_settingsStore.Current.ShowFloatingWindow)
        {
            _floating.ShowWithoutActivation();
        }

        // ---------- 热键 ----------
        ApplyHotKeys(showErrors: true);

        // ---------- 第二实例唤出 ----------
        StartShowWatcher();

        Log.Line("启动完成。");

        // ---------- 命令行直达 ----------
        HandleCommandLine();
    }

    /// <summary>自启模式下延迟初始化的秒数。登录后系统繁忙，等一会儿再抢资源。</summary>
    private const int StartupDelaySeconds = 8;

    /// <summary>
    /// 启动期收集到的警告。等托盘就绪后用气泡统一告知 —— **刻意不用模态对话框**。
    ///
    /// 为什么不弹框：开机时弹模态框既打断用户，又看起来像"启动失败"；
    /// 而实际上程序好好的，只是某个子功能没起来。气泡 + 日志足够了。
    /// </summary>
    private readonly List<string> _startupWarnings = new();

    /// <summary>
    /// 启动哨兵：在**任何初始化之前**，用最朴素的方式记一行"我要启动了"。
    ///
    /// 为什么不能只靠主日志：
    ///   主日志要 `Log.Open` 之后才存在。而"开机自启到底有没有把进程拉起来"这个问题，
    ///   恰恰发生在 Log.Open 之前 —— exe 根本没被拉起、或者起来瞬间就崩了。
    ///   那种情况下主日志一片空白，看起来像"程序从没启动过"，完全无从下手。
    ///   这里直接 append 纯文本，不依赖 Log 的任何状态。
    /// </summary>
    private static void WriteStartupSentinel(string[] args)
    {
        try
        {
            var path = AppPaths.StartupSentinelFile;

            // 行数上限：超过 64 KB 就只留最后 100 行，免得哨兵自己变成垃圾场。
            if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024)
            {
                var tail = File.ReadAllLines(path).TakeLast(100);
                File.WriteAllLines(path, tail, new UTF8Encoding(false));
            }

            var line = string.Join('\t',
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                $"pid={Environment.ProcessId}",
                $"args=[{string.Join(' ', args)}]",
                $"cwd={Environment.CurrentDirectory}");

            File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // 哨兵自己失败绝不能影响启动
        }
    }

    /// <summary>
    /// 启动剪贴板监听，失败就退避重试几次再放弃。
    ///
    /// 为什么要重试：这个失败**大多不是永久的** —— 开机瞬间、系统刚从休眠唤醒、
    /// 或别的程序正占着剪贴板服务，都会让 AddClipboardFormatListener 短暂失败，
    /// 过一两秒再试就好了。
    /// 原来一次失败就弹模态框宣告"历史功能不可用"，既吓人又不准
    /// （程序其它功能明明都好好的），而且开机时弹框看起来就像"启动失败"。
    /// </summary>
    private void StartClipboardWithRetry()
    {
        var delays = new[] { 0, 800, 2000, 4000 };

        for (var i = 0; i < delays.Length; i++)
        {
            if (delays[i] > 0)
            {
                Thread.Sleep(delays[i]);
                Log.Line($"剪贴板监听第 {i + 1} 次尝试…");
            }

            try
            {
                if (_clipboard!.Start())
                {
                    if (i > 0)
                    {
                        Log.Line($"剪贴板监听在第 {i + 1} 次尝试成功（前 {i} 次失败，已自愈）。");
                    }

                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"剪贴板监听启动异常（第 {i + 1} 次）", ex);
            }
        }

        // 全部失败：只记日志 + 托盘气泡，不弹模态框。
        Log.Error($"剪贴板监听启动失败（已重试 {delays.Length} 次）。历史功能不可用，其余功能不受影响。");
        _startupWarnings.Add("剪贴板监听没起来，历史功能暂时不可用（其它功能正常）。");
    }

    /// <summary>
    /// 命令行直接开关开机自启：`--autostart [status|on|off]`（不带值 = status）。
    ///
    /// 为什么要这个，而不是只留托盘菜单里那个勾选框：
    ///   1. 验收时一条命令就能验，不必去托盘菜单里一层层点，也不必肉眼比对注册表；
    ///   2. 「写 / 删注册表」是改动系统状态的动作，命令行可脚本化、可回滚，
    ///      而且每次都留日志，比让人在 GUI 里点更不容易出错；
    ///   3. status 是纯只读查询，零副作用，随时可以跑。
    ///
    /// 这里在 UI 初始化之前就处理完并退出，所以它相当于一个独立的命令行小工具，
    /// 不会顺手把托盘和悬浮窗也拉起来。
    /// </summary>
    private static bool TryRunAutoStart(string[] args, out int exitCode)
    {
        exitCode = 0;

        var index = Array.FindIndex(args, a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        // 后面跟的如果不是另一个参数，就当作动作；什么都不跟 = status（最安全的默认）。
        var action = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : "status";

        SelfTest.AttachParentConsole();

        // 改注册表必须留痕：这次操作要能在日志里查到。
        try
        {
            Log.Open(AppPaths.LogFile);
        }
        catch
        {
            // 日志开不了不影响功能
        }

        Console.WriteLine();
        Console.WriteLine("桌面工具箱 · 开机自启");
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine($"程序路径 : {AutoStart.ExecutablePath}");
        Console.WriteLine("注册表项 : HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run");
        Console.WriteLine("值名     : 桌面工具箱");
        Console.WriteLine("--------------------------------------------------");

        switch (action.ToLowerInvariant())
        {
            case "status":
            {
                // 不只报"有没有"，还要报"写的是什么"。
                // 排障时最关键的一句话就是"注册表里那条命令到底指向哪个 exe"——
                // 程序被移动过之后，自启失效的全部原因就在这个字符串上。
                var registered = AutoStart.ReadRegisteredCommand(out var readError);
                if (registered is null)
                {
                    Console.WriteLine("当前状态 : 未开启");
                    if (readError is not null)
                    {
                        Console.WriteLine($"（读取时出错：{readError}）");
                    }
                }
                else
                {
                    Console.WriteLine("当前状态 : 已开启（下次登录会自动启动）");
                    Console.WriteLine($"注册内容 : {registered}");
                    Console.WriteLine(AutoStart.PointsAtCurrentExe(registered)
                        ? "路径比对 : 一致 —— 自启会启动当前这个 exe"
                        : "路径比对 : 不一致 ← 注册的是别的路径，自启可能起不来或启动了旧版本");
                }

                Console.WriteLine();
                Console.WriteLine("（只读查询，没有改动任何东西）");
                return true;
            }

            case "on":
            case "off":
            {
                var want = action.Equals("on", StringComparison.OrdinalIgnoreCase);
                Log.Line($"命令行 --autostart {action}：{(want ? "写入" : "删除")}启动项。");

                if (AutoStart.Set(want, out var error))
                {
                    Console.WriteLine(want
                        ? "结果     : 已写入启动项，下次登录会自动启动。"
                        : "结果     : 已删除启动项，不再自动启动。");
                }
                else
                {
                    Console.WriteLine($"结果     : 失败 —— {error}");
                    exitCode = 1;
                }

                return true;
            }

            default:
                Console.WriteLine($"认不出这个动作：{action}");
                Console.WriteLine("用法：--autostart status | on | off");
                exitCode = 2;
                return true;
        }
    }

    private void HandleCommandLine()
    {
        var args = Environment.GetCommandLineArgs();

        if (args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            Log.Line("命令行参数 --settings：直接打开设置窗口。");
            // 走 Dispatcher 队列而不是直接调：此刻 OnStartup 还没返回，
            // 消息循环尚未开始跑，排到队列末尾等一轮更稳。
            Dispatcher.BeginInvoke(new Action(OpenSettings));
        }

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--open", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var toolId = args[i + 1];
            if (toolId.StartsWith("--", StringComparison.Ordinal))
            {
                Log.Error("--open 后面没跟工具 Id。");
                continue;
            }

            Log.Line($"命令行参数 --open {toolId}：直接打开该工具。");
            Dispatcher.BeginInvoke(new Action(() => _ctx?.InvokeTool(toolId)));
        }
    }

    private void StartShowWatcher()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWatcher = new CancellationTokenSource();
        var token = _showWatcher.Token;

        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_showEvent.WaitOne(TimeSpan.FromMilliseconds(500)))
                    {
                        Dispatcher.BeginInvoke(new Action(() => _floating?.ShowWithoutActivation()));
                    }
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "Toolbox.ShowWatcher",
        };

        thread.Start();
    }

    // ---------------------------------------------------------------- 热键

    /// <summary>
    /// 主热键被别的程序占用时的备选链，按顺序尝试。
    ///
    /// 为什么必须有这个（这是本机实测逼出来的，不是过度设计）：
    /// 实测本机 Win+Shift+&lt;字母&gt; 有 10 个已被第三方程序占用 ——
    /// A C F M P R S T V W（其中 S 是 Windows 自带的截图快捷键）。
    /// 而工具箱原来挑的 4 个默认键（V / C / A / T）**正好全撞上**，
    /// 结果是一装好就有 5 个热键里 4 个是坏的。用户没做错任何事，不该承担这个后果。
    ///
    /// 所以：默认键保留最顺手的那个（V=粘贴、C=转换、A=AI、T=翻译，语义最好），
    /// 被占用时自动降一级到备选。备选全部取自本机实测空闲的组合：
    ///   Win+Alt 空闲 = A C E H I J L N O P Q S U V X Z
    ///   Win+Shift 空闲 = B D E G H I J K L N O Q U X Y Z
    ///
    /// key = 工具声明的默认组合键（不是用户改过的），value = 依次尝试的备选。
    /// </summary>
    private static readonly Dictionary<string, string[]> HotKeyFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        // ⚠️ 这组分配不是手配的，是**算出来的**（见 artifacts\probe-hotkey\SolveHotKeys.cs）。
        //
        // 为什么改成算：工具从 5 个涨到 11 个后，手配热键连续撞了三次
        //   （见 DECISIONS 坑 33）—— 每加一个工具都要在脑子里跟 20 多个键核对一遍，
        //   人做不到可靠。把约束写成程序让它求解，就不会再漏。
        //
        // 约束（全部由那个脚本断言过）：
        //   ① 每个工具的默认键互不相同
        //   ② 所有降级键互不相同（跨工具全局唯一）
        //   ③ 任何降级键都**不等于**任何工具的默认键 —— 否则 A 降级时会去抢 B 的默认键
        //   ④ 所有键都落在**实测空闲**的字母上（工具箱未运行时测）
        ["Win+Shift+V"] = ["Win+Alt+A", "Win+Shift+U"], // 快捷剪贴板
        ["Win+Shift+X"] = ["Win+Alt+C", "Win+Shift+K"], // 图片裁剪
        ["Win+Shift+C"] = ["Win+Alt+E", "Win+Shift+G"], // 格式转换
        ["Win+Shift+A"] = ["Win+Alt+I", "Win+Shift+J"], // 快捷 AI
        ["Win+Shift+T"] = ["Win+Alt+J", "Win+Shift+Y"], // 翻译选中
        ["Win+Alt+S"] = ["Win+Alt+L", "Win+Shift+D"],   // 快捷截图
        ["Win+Alt+T"] = ["Win+Alt+N", "Win+Shift+H"],   // OCR 截图识字（T 本机被占）
        ["Win+Alt+H"] = ["Win+Alt+O", "Win+Shift+Q"],   // 哈希校验
        ["Win+Alt+Q"] = ["Win+Alt+U", "Win+Shift+N"],   // 二维码
        ["Win+Alt+P"] = ["Win+Alt+V", "Win+Shift+Z"],   // 窗口置顶
        ["Win+Alt+R"] = ["Win+Alt+Z", "Win+Shift+E"],   // 批量重命名（R 本机被占）
        ["Win+Alt+X"] = ["Win+Shift+B", "Ctrl+Alt+X"],  // 运行命令
        ["Win+Alt+G"] = ["Win+Shift+I", "Ctrl+Alt+G"],   // 批量打包（G 空闲；备选也取空闲字母）
    };

    /// <summary>按设置重新注册全部热键，返回逐条结果（注册失败必须可见，不能静默）。</summary>
    internal IReadOnlyList<SettingsWindow.HotKeyResult> ApplyHotKeys(bool showErrors = false)
    {
        var results = new List<SettingsWindow.HotKeyResult>();
        if (_hotKeys is null || _registry is null || _settingsStore is null)
        {
            return results;
        }

        foreach (var tool in _registry.Tools)
        {
            // ★ 关掉的工具**不注册热键** —— 否则它虽然不出现在界面上，
            //   但按了热键照样弹出来，用户会觉得"关不掉"。
            //
            //   注意这里要**主动 Unregister 一次**：用户可能是"这次启动才关掉它"的，
            //   而上一次运行注册过的热键在本次进程里还没被注册，
            //   所以只需跳过即可；但为了设置窗口里那条状态显示正确，
            //   仍然给它一条"因工具已关闭而不注册"的结果。
            if (!ToolGate.IsEnabled(_settingsStore.Current, tool.Id))
            {
                results.Add(new SettingsWindow.HotKeyResult(
                    tool.Id, "", null, "工具已关闭 —— 不注册热键")
                {
                    Disabled = true,
                });

                // 额外热键也要一并记上，否则设置窗口里那一行会停在旧状态
                foreach (var (name, _) in tool.ExtraHotKeys)
                {
                    results.Add(new SettingsWindow.HotKeyResult(
                        $"{tool.Id}.{name}", "", null, "工具已关闭 —— 不注册热键")
                    {
                        Disabled = true,
                    });
                }

                continue;
            }

            RegisterOne(tool.Id, tool.DefaultHotKey, results);

            // 额外热键（比如快捷 AI 的「翻译选中」一键直达）
            foreach (var (name, defaultSpec) in tool.ExtraHotKeys)
            {
                RegisterOne($"{tool.Id}.{name}", defaultSpec, results);
            }
        }

        if (showErrors)
        {
            var failures = results.Where(r => r.Error is not null).ToList();

            // 「被占用自动降级」和「用户主动禁用」是两件完全不同的事，绝不能混在一条里报。
            var changed = results.Where(r => r.Note is not null && !r.Disabled).ToList();
            var disabled = results.Where(r => r.Disabled).ToList();

            var blocks = new List<string>();

            if (changed.Count > 0)
            {
                var lines = changed.Select(c => $"· {DescribeTool(c.ToolId)}（{c.Spec}）：{c.Note}");
                blocks.Add("以下热键的默认组合键已被别的程序占用，工具箱自动换成了别的键：\n\n"
                           + string.Join("\n", lines)
                           + "\n\n想固定成自己顺手的组合键，可以在「设置」里改。");
            }

            if (failures.Count > 0)
            {
                var lines = failures.Select(f => $"· {DescribeTool(f.ToolId)}（{f.Spec}）：{f.Error}");
                blocks.Add("以下热键没能注册成功，多半是被别的程序占用了：\n\n"
                           + string.Join("\n", lines)
                           + "\n\n可以在「设置」里改成别的组合键。");
            }

            // 禁用是用户自己选的，不需要弹窗打断他 —— 记一行日志就够了。
            // 开机时多一个弹窗，本身就是"看起来像启动失败"的嫌疑源。
            foreach (var d in disabled)
            {
                Log.Line($"热键未注册（用户已关闭）：{DescribeTool(d.ToolId)} —— {d.Note}");
            }

            if (blocks.Count > 0)
            {
                var text = string.Join("\n\n--------------------------------\n\n", blocks);
                Log.Error(text.Replace("\n", " "));
                Dispatcher.BeginInvoke(new Action(() =>
                    MessageBox.Show(text, "桌面工具箱 · 热键",
                        MessageBoxButton.OK, MessageBoxImage.Warning)));
            }
        }

        return results;
    }

    /// <summary>
    /// 把 "ai.translateSelection" 说成人话：「快捷 AI · 翻译选中」。
    /// （早先直接用 _registry.Find(整个 id)，对带点的额外热键永远找不到，
    ///   通知里就会裸奔出一串 "ai.translateSelection"。）
    /// </summary>
    private string DescribeTool(string toolId) => ToolNaming.Describe(_registry, toolId);

    private void RegisterOne(string fullId, string defaultSpec, List<SettingsWindow.HotKeyResult> results)
    {
        if (_hotKeys is null || _settingsStore is null)
        {
            return;
        }

        _hotKeys.Unregister(fullId);

        var hasEntry = _settingsStore.Current.HotKeys.TryGetValue(fullId, out var configured);
        var custom = configured?.Trim() ?? "";

        // ★ 这个条目是**用户自己填的**，还是**上次自动降级写进来的**？
        //
        // 这一行是修掉一个真实缺陷的关键。不区分的话，"自动降级"的结果会被当成
        // "用户的选择"，于是默认键重新空闲后也永远回不去（详见 Settings.HotKeyOrigins 的说明）。
        //
        // 条目不存在 ⇒ 用户从没配过 ⇒ 按"非用户"处理（可自由降级 / 回默认键）。
        var origin = _settingsStore.Current.HotKeyOrigins.TryGetValue(fullId, out var o) ? o : null;
        var isAuto = string.Equals(origin, "auto", StringComparison.OrdinalIgnoreCase);

        // ★ 空字符串 = 用户明确要求"这个工具不要热键"。
        //
        // 必须和"没配过"区分开：
        //   没配过（条目不存在）  → 用工具自带的默认键
        //   配成空（条目存在且空）→ 就是不注册
        // 用户可能更习惯从悬浮窗 / 托盘打开工具，而不是记一串热键 —— 这是合理需求，
        // 不该逼他随便挑一个键占着（那还会去跟别的程序抢）。
        //
        // ⚠️ 空的条目如果是 auto 写进来的，按"没配过"处理 —— 自动逻辑不会写空串，
        //    真出现只可能是被外部工具改坏了，回默认键比继续禁用更符合用户预期。
        if (hasEntry && custom.Length == 0 && !isAuto)
        {
            Log.Line($"热键已禁用（用户设置为空）：{fullId} —— 请从悬浮窗或托盘打开。");
            results.Add(new SettingsWindow.HotKeyResult(
                fullId, "", null, "已禁用 —— 不占用任何热键，请从悬浮窗 / 托盘打开")
            {
                Disabled = true,
            });
            return;
        }

        // auto 写进来的条目不算"用户改过" ⇒ 先按默认键试，默认键不行再降级。
        var isCustom = hasEntry && custom.Length > 0 && !isAuto;

        // ★ 再收一道：**值恰好等于默认键**的条目也不算"用户改过"。
        //
        //   为什么（实测抓到的真问题）：
        //     设置窗口保存时，只要框里有内容就一律标成 Origins="user"。
        //     而用户完全可能（或历史上某次操作）把框里的值**原样保存**一遍 ——
        //     值就是工具自带的默认键，却被打上了"用户亲手指定"的标记。
        //
        //     后果：这个键一旦被别的程序占用，按设计"用户指定的键被占用时
        //     只报错、不自动换" —— 于是**工具悄悄失去热键**，
        //     而用户从没主动这么设过，界面上也看不出问题。
        //
        //   判据很干净：值与默认键相同 ⇒ 语义上就是"没改过" ⇒ 清掉标记、走默认+降级。
        var isSameAsDefault = isCustom
                              && !string.IsNullOrWhiteSpace(defaultSpec)
                              && string.Equals(custom, defaultSpec.Trim(), StringComparison.OrdinalIgnoreCase);

        if (isSameAsDefault)
        {
            _settingsStore.Current.HotKeys.Remove(fullId);
            _settingsStore.Current.HotKeyOrigins.Remove(fullId);
            _settingsStore.Save();
            Log.Line($"热键条目与默认键相同，已按「未改过」处理：{fullId} → {defaultSpec}（可继续自动降级）");
            isCustom = false;
        }

        var spec = isCustom ? custom : defaultSpec;

        if (string.IsNullOrWhiteSpace(spec))
        {
            results.Add(new SettingsWindow.HotKeyResult(fullId, "", null));
            return;
        }

        var result = _hotKeys.Register(fullId, spec);

        // 默认键这次成功了 → 把上次"自动降级"留下的痕迹清掉。
        //
        // 不清的话会留一个**看起来像用户配置的**残留条目：它虽然被 isAuto 正确忽略，
        // 但设置窗口读的是 HotKeys 里的值，会显示那个早就没在用的备选键。
        // 清掉之后设置窗口显示的就是真正的默认键，界面与行为一致。
        if (result.Success && !isCustom && isAuto)
        {
            _settingsStore.Current.HotKeys.Remove(fullId);
            _settingsStore.Current.HotKeyOrigins.Remove(fullId);
            _settingsStore.Save();
            Log.Line($"热键回到默认键：{fullId} → {spec}（上次的自动降级已失效，占用已解除）。");
        }

        // 用户自己指定的组合键被占用 → 只报错，不自动改。
        // 那是他明确做出的选择，悄悄换掉比报错更让人困惑。
        //
        // 工具自带的默认键被占用 → 自动往备选链降一级。
        // 用户什么都没选，却拿到一个「热键按了没反应」的工具，这才是真的糟。
        if (!result.Success && !isCustom
            && HotKeyFallbacks.TryGetValue(defaultSpec, out var chain))
        {
            foreach (var alt in chain)
            {
                if (!_hotKeys.Register(fullId, alt).Success)
                {
                    continue;
                }

                // 降级结果写回设置，并且**必须标明这是自动写的**（HotKeyOrigins = "auto"）。
                //
                // 不写回的话，设置窗口里显示的仍是那个没生效的默认键，
                // 用户会以为「设置里明明写着 Win+Shift+V，怎么按都没反应」。
                // 但不标 origin 的话，这次降级会被下次启动当成"用户的选择"，
                // 从此再也回不到默认键 —— 那是这个字段存在的原因。
                _settingsStore.Current.HotKeys[fullId] = alt;
                _settingsStore.Current.HotKeyOrigins[fullId] = "auto";
                _settingsStore.Save();

                var note = isAuto
                    ? $"默认键 {defaultSpec} 仍被别的程序占用，继续沿用备选键 {alt}"
                    : $"默认键 {spec} 已被别的程序占用，已自动改用 {alt}";
                Log.Line($"热键自动降级：{fullId} → {alt}（原 {defaultSpec} 被占用，已标记为自动降级）");
                results.Add(new SettingsWindow.HotKeyResult(fullId, alt, null, note));
                return;
            }
        }

        results.Add(new SettingsWindow.HotKeyResult(fullId, spec, result.Error));

        if (result.Success)
        {
            Log.Line($"热键已注册：{fullId} → {spec}");
        }
        else
        {
            Log.Error($"热键注册失败：{fullId} → {spec}（{result.Error}）");
        }
    }

    private void OnHotKeyPressed(string fullId)
    {
        try
        {
            // 记一行触发日志。热键这条链路是「用户按了没反应」类问题的高发区，
            // 而它跨越了 消息循环 → 注册表映射 → 工具调用 三段，
            // 出了问题光看代码很难定位。有这行日志，一眼就能判断
            // 「消息根本没到」还是「到了但工具没起来」。
            Log.Line($"热键触发：{fullId}");

            // "ai.translateSelection" 这种要路由到工具的 InvokeExtra
            var dot = fullId.IndexOf('.');
            if (dot > 0)
            {
                var toolId = fullId[..dot];
                var extraName = fullId[(dot + 1)..];
                var tool = _registry?.Find(toolId);

                if (tool is not null)
                {
                    IDisposable? suppress = null;
                    if (tool.OpensBigWindow && _floating is not null)
                    {
                        suppress = _floating.Suppress();
                    }

                    try
                    {
                        tool.InvokeExtra(extraName);
                    }
                    finally
                    {
                        suppress?.Dispose();
                    }

                    return;
                }
            }

            _ctx?.InvokeTool(fullId);
        }
        catch (Exception ex)
        {
            Log.Exception($"热键触发工具失败：{fullId}", ex);
        }
    }

    private void OpenSettings()
    {
        try
        {
            var window = new SettingsWindow(_ctx!, _registry!, () => ApplyHotKeys(false), ApplyToolSwitches);
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            Log.Exception("打开设置窗口失败", ex);
        }
    }

    /// <summary>
    /// 应用"功能开关"：让启停**立刻生效**（不用重启工具箱）。
    ///
    /// 做三件事，缺一不可：
    ///   ① **停掉**被关闭的工具（调它的 Stop，让它释放剪贴板监听 / 热键 / 窗口）；
    ///   ② **启动**被打开、但还没启动过的工具；
    ///   ③ 重新注册热键（关掉的不注册、打开的要注册）并重画悬浮窗与托盘菜单。
    ///
    /// ⚠️ 只做 ② 不做 ① 是最容易犯的错：那样"关闭"只是不再新启动，
    ///    已经跑起来的仍占着剪贴板监听 —— 用户会发现"关了还在抓我的剪贴板"。
    /// </summary>
    internal void ApplyToolSwitches()
    {
        if (_registry is null || _ctx is null || _settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Current;

        foreach (var tool in _registry.Tools)
        {
            var shouldRun = ToolGate.IsEnabled(settings, tool.Id);

            if (!shouldRun)
            {
                // ① 关掉：停它（重复 Stop 是安全的，工具自己会兜）
                if (_toolsRunning.Remove(tool.Id))
                {
                    try
                    {
                        tool.Stop();
                        Log.Line($"工具已停止（用户关闭）：{tool.Id}（{tool.Name}）");
                    }
                    catch (Exception ex)
                    {
                        Log.Exception($"停止工具失败：{tool.Id}", ex);
                    }
                }

                continue;
            }

            // ② 打开：只在"还没跑"的时候启动，避免重复 Start 出两份监听
            if (_toolsRunning.Add(tool.Id))
            {
                try
                {
                    tool.Start(_ctx);
                    Log.Line($"工具已启动（用户开启）：{tool.Id}（{tool.Name}）");
                }
                catch (Exception ex)
                {
                    _toolsRunning.Remove(tool.Id);
                    Log.Exception($"启动工具失败：{tool.Id}", ex);
                }
            }
        }

        // ③ 热键 / 悬浮窗 / 托盘同步
        ApplyHotKeys(false);

        _floating?.BuildButtons();
        RefreshTrayToolsMenu();

        // 托盘图标本身也要跟总开关同步
        _tray?.SetToolsEnabled(settings.ToolsEnabled);
    }

    /// <summary>
    /// 加载插件目录里的工具插件，并把它们注册进 registry。
    ///
    /// 设计要点：
    ///   · **失败不影响启动** —— 插件坏了只是少一个工具，
    ///     绝不能让整个工具箱起不来（那是最糟的失败模式）；
    ///   · 失败原因**收集起来**，在启动完成后用托盘气泡统一说 ——
    ///     用模态框会打断开机（见 `_startupWarnings` 的说明）；
    ///   · 插件的工具与内置工具**完全同权**：同样受功能开关管、
    ///     同样参与热键注册与悬浮窗显示，不搞特殊。
    /// </summary>
    private void LoadPlugins()
    {
        if (_registry is null)
        {
            return;
        }

        try
        {
            Tools.Store.PluginLoader.EnsureResolver();

            var ids = _registry.Tools.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var report = Tools.Store.PluginLoader.LoadAll(ids);

            foreach (var tool in report.Loaded)
            {
                _registry.Add(tool);
            }

            if (report.Loaded.Count > 0)
            {
                Log.Line($"插件加载完成：成功 {report.Loaded.Count} 个。");
            }

            foreach (var err in report.Errors)
            {
                _startupWarnings?.Add($"插件问题：{err}");
            }
        }
        catch (Exception ex)
        {
            // 插件系统本身出问题也不能拖垮启动
            Log.Exception("插件加载流程失败", ex);
            _startupWarnings?.Add($"插件加载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 重新扫描插件（用户刚下载/删除了插件时调用）。
    ///
    /// ⚠️ 已删除的插件**无法真正卸载**（.NET 默认上下文不支持卸载程序集），
    ///    所以这里只做"新增"：删掉的插件要重启工具箱才真正消失。
    ///    这一点在界面上要如实告诉用户，不能说"已卸载"。
    /// </summary>
    internal void ReloadPlugins()
    {
        if (_registry is null)
        {
            return;
        }

        var before = _registry.Tools.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        LoadPlugins();

        var added = _registry.Tools
            .Where(t => !before.Contains(t.Id))
            .ToList();

        if (added.Count > 0)
        {
            // 新插件要立刻能用：启动它 + 重算界面
            foreach (var tool in added)
            {
                if (!ToolGate.IsEnabled(_settingsStore!.Current, tool.Id))
                {
                    continue;
                }

                try
                {
                    tool.Start(_ctx!);
                    _toolsRunning.Add(tool.Id);
                }
                catch (Exception ex)
                {
                    Log.Exception($"启动新插件失败：{tool.Id}", ex);
                }
            }

            ApplyHotKeys(false);
            _floating?.BuildButtons();
            RefreshTrayToolsMenu();

            Log.Line($"已热加载 {added.Count} 个新插件。");
        }
    }

    /// <summary>
    /// 正在运行的工具 Id（避免重复 Start / 漏掉 Stop）。
    /// </summary>
    private readonly HashSet<string> _toolsRunning = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 重填托盘的「所有工具」子菜单。
    ///
    /// **只列启用中的工具** —— 关掉的工具不该出现在任何入口里
    /// （否则用户点开一个已关闭的工具，会得到"找不到工具"或一个空窗口）。
    /// 热键提示也一并显示，方便用户记不住键时来这儿看。
    /// </summary>
    private void RefreshTrayToolsMenu()
    {
        if (_tray is null || _registry is null || _settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Current;

        var entries = _registry.Tools
            .Where(t => ToolGate.IsEnabled(settings, t.Id))
            .Select(t =>
            {
                string hotKey;

                if (settings.HotKeys.TryGetValue(t.Id, out var hk))
                {
                    // 空串 = 用户主动禁用热键（不是"没配过"）
                    hotKey = hk.Length > 0 ? hk : "";
                }
                else
                {
                    hotKey = t.DefaultHotKey;
                }

                return (t.Name, t.Id, hotKey);
            });

        _tray.FillToolsMenu(entries, id => _ctx!.InvokeTool(id));
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 一个界面异常不该把整个工具箱带崩——它要常驻一整天
        Log.Exception("未处理异常（UI 线程，已兜住）", e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Line("正在退出…");

        try
        {
            _registry?.StopAll();
        }
        catch (Exception ex)
        {
            Log.Exception("停止工具时出错", ex);
        }

        _hotKeys?.Dispose();
        _tray?.Dispose();
        _clipboard?.Dispose();
        _focus?.Dispose();

        _showWatcher?.Cancel();
        _showEvent?.Dispose();

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        Log.Line("已退出。");
        base.OnExit(e);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
