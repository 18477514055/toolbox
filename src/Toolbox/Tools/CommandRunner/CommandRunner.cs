using System.Diagnostics;
using System.IO;
using System.Text;
using Toolbox.Core;
// 别名会遮蔽 System.IO 下的其它类型（FileShare 等），所以上面显式 using System.IO;
using File = System.IO.File;
using Path = System.IO.Path;
using Directory = System.IO.Directory;

namespace Toolbox.Tools.CommandRunner;

/// <summary>要交给哪种 shell。</summary>
internal enum ShellKind
{
    /// <summary>命令提示符（cmd.exe）</summary>
    Cmd,

    /// <summary>PowerShell（优先 pwsh，退到 Windows PowerShell）</summary>
    PowerShell,
}

/// <summary>一次命令执行的结果。</summary>
internal sealed record CommandResult(
    bool Success,
    int ExitCode,
    string Output,
    string Interpreter,
    long ElapsedMs,
    string? Error)
{
    public static CommandResult Fail(string error, string interpreter = "")
        => new(false, -1, "", interpreter, 0, error);
}

/// <summary>
/// 运行一条命令，把输出拿回来。
///
/// ⚠️⚠️ **这是整个工具箱里唯一一个"执行任意命令"的功能，安全上必须讲清楚**：
///
///   它与"工具箱自己做事"有**本质区别**：
///     · 别的工具（截图、哈希、改名）都是**有限的、可预期的**动作，
///       用户能预判它会干什么；
///     · 而"执行命令"的能力上不封顶 —— 输入什么就执行什么，
///       可以删文件、改注册表、下载东西。
///
///   所以本实现刻意做了三件事，**它们不是可选的**：
///     ① **绝不自动执行** —— 必须用户亲手点「执行」，或明确按 Ctrl+Enter；
///        回车是"换行"而不是"执行"（多行命令很常见，回车执行会误触）。
///     ② **执行前把"到底要跑什么"原样显示出来**，包括最终拼给 shell 的参数，
///        让用户能核对，而不是"我输的和我跑的可能是两回事"。
///     ③ 结果**完整回显**（含退出码），失败也要看到 stderr，
///        不做任何"看起来好像成功了"的美化。
///
///   另外：这里**不**做"危险命令拦截"。理由 ——
///     黑名单式的拦截永远是漏的（`rm` 能写成 `r''m`、`del` 能写成 `d^el`……），
///     给了用户"它帮我挡住了"的错觉反而更危险。
///     正确的做法是**把能力边界和后果如实告诉用户**（见 README），
///     让用户自己决定跑什么 —— 这本来就是他自己电脑上的 shell。
///
/// 输出采集的坑：中文 Windows 的 cmd 默认代码页是 936（GBK），
/// PowerShell 输出则常是 UTF-8 或系统 ANSI。编码搞错会满屏乱码。
/// 这里的做法见 <see cref="BuildStartInfo"/>：显式指定编码并尽量统一。
/// </summary>
internal static class CommandRunner
{
    /// <summary>
    /// 静态构造：注册代码页 provider。
    ///
    /// ⚠️ 这一句在 .NET Core / .NET 5+ 上是**必需**的：
    ///   框架默认只带 UTF-8 / UTF-16 / ASCII 等少数编码，
    ///   `Encoding.GetEncoding(936)` 这类"老代码页"会直接抛
    ///   `NotSupportedException: No data is available for encoding 936`。
    ///   中文 Windows 上 cmd 的输出恰恰就是 936 —— 不注册就必然乱码。
    /// </summary>
    static CommandRunner()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception ex)
        {
            Log.Exception("注册代码页 provider 失败（中文输出可能乱码）", ex);
        }
    }

    /// <summary>单条命令的默认超时。跑太久多半是卡住了（比如等待输入）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>输出最多保留多少字符（防止一个死循环命令刷爆内存）。</summary>
    private const int MaxOutputChars = 400_000;

    /// <summary>
    /// 代码页探测结果的缓存（按 shell 分别缓存）。
    /// 代码页在一次运行期间不会变，没必要每执行一条命令就探一次。
    /// </summary>
    private static readonly Dictionary<ShellKind, Encoding> EncodingCache = new();

    /// <summary>
    /// 找到可用的 shell 路径。
    ///
    /// PowerShell 优先 `pwsh`（PowerShell 7+，UTF-8 友好），
    /// 找不到就退到系统自带的 `powershell.exe`（5.1，任何 Windows 都有）。
    /// </summary>
    public static string ResolveInterpreter(ShellKind kind)
    {
        if (kind == ShellKind.Cmd)
        {
            // ComSpec 指向真正的 cmd.exe；取不到就用系统目录拼一个
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            if (!string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec))
            {
                return comspec;
            }

            return Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }

        // PowerShell：先找 pwsh（PATH 里 / 常见安装位置）
        var pwsh = FindOnPath("pwsh.exe");
        if (pwsh is not null)
        {
            return pwsh;
        }

        // 退到 Windows PowerShell（Win7 起自带，一定有）
        var winPs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");

        if (File.Exists(winPs))
        {
            return winPs;
        }

        // 最后碰运气
        return FindOnPath("powershell.exe") ?? "powershell.exe";
    }

    private static string? FindOnPath(string exe)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // 路径非法就跳过
                }
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    /// <summary>
    /// 拼出最终要执行的命令行参数（**给用户看的那一份**）。
    ///
    /// 用户要求"把到底要跑什么显示出来" —— 这里返回的就是那个字符串。
    /// </summary>
    public static string DescribeCommandLine(ShellKind kind, string userCommand, string? workingDir)
    {
        var exe = ResolveInterpreter(kind);
        var sb = new StringBuilder();

        sb.Append('"').Append(exe).Append('"');

        // 说明用的是哪一代 PowerShell（5.1 与 7+ 语法/默认编码都不同，值得标出来）
        if (kind == ShellKind.PowerShell)
        {
            var isPwsh = Path.GetFileNameWithoutExtension(exe)
                .Equals("pwsh", StringComparison.OrdinalIgnoreCase);
            sb.Append(isPwsh ? "  （PowerShell 7+）" : "  （Windows PowerShell 5.1）");
        }

        sb.Append('\n');
        sb.Append("  参数: ").Append(string.Join(' ', BuildArguments(kind, userCommand)));

        if (!string.IsNullOrWhiteSpace(workingDir))
        {
            sb.Append('\n').Append("  工作目录: ").Append(workingDir);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 构造传给 shell 的参数。
    ///
    /// **用 ArgumentList 而不是手拼字符串**：命令行里出现引号、空格、中文时，
    /// 手拼几乎必错（这是本项目在格式转换那边已经踩过的坑，
    /// 见 LibreOfficeConverter 的注释）。ArgumentList 由 .NET 负责转义。
    /// </summary>
    private static List<string> BuildArguments(ShellKind kind, string userCommand)
    {
        var args = new List<string>();

        if (kind == ShellKind.Cmd)
        {
            // /d 跳过 AutoRun（有些机器上 AutoRun 会插入别的东西，让结果不可预期）
            // /c 执行完就退出
            args.Add("/d");
            args.Add("/c");
            args.Add(userCommand);
        }
        else
        {
            // -NoProfile：不加载用户的 profile —— 否则同一句话在不同机器上行为不同，
            //              而且 profile 里的报错会混进输出里干扰判断
            // -NonInteractive：遇到需要输入的提示直接失败，而不是**默默挂住**等输入
            // -Command：执行后面的字符串
            args.Add("-NoProfile");
            args.Add("-NonInteractive");
            args.Add("-Command");
            args.Add(userCommand);
        }

        return args;
    }

    /// <summary>
    /// 执行命令（异步，带超时）。
    /// </summary>
    public static async Task<CommandResult> RunAsync(
        ShellKind kind,
        string userCommand,
        string? workingDir,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(userCommand))
        {
            return CommandResult.Fail("命令是空的。");
        }

        var interpreter = ResolveInterpreter(kind);
        var psi = new ProcessStartInfo
        {
            FileName = interpreter,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in BuildArguments(kind, userCommand))
        {
            psi.ArgumentList.Add(a);
        }

        if (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir))
        {
            psi.WorkingDirectory = workingDir;
        }

        // ---- 输出编码 ----
        //
        // 这是最容易出乱码的地方，而且**不能想当然**。
        //
        // 我第一版的做法是"给命令前面加 chcp 65001，读取端固定按 UTF-8 解"，
        // 结果中文全是乱码（自检抓到：实得 `���Ĳ���`）。原因有两层：
        //   ① `chcp 65001 >nul &` 这半句本身会干扰 cmd 的输出（实测把 banner 都带出来了）；
        //   ② 更要紧的：**机器的默认控制台代码页不一定是 936**。
        //      本机实测 `chcp` 就报 **65001**（已经是 UTF-8），
        //      而我却按"一定是 936"去处理 —— 假设错了，怎么调都是乱的。
        //
        // 正确做法：**先问系统当前代码页，再按它去解码**。
        // 代码页只在执行前查一次（查询本身很便宜），随后用它构造匹配的 Encoding。
        var consoleEncoding = ResolveConsoleEncoding(kind);

        psi.StandardOutputEncoding = consoleEncoding;
        psi.StandardErrorEncoding = consoleEncoding;

        var sw = Stopwatch.StartNew();

        try
        {
            using var process = new Process { StartInfo = psi };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) { return; }
                lock (stdout)
                {
                    if (stdout.Length < MaxOutputChars) { stdout.AppendLine(e.Data); }
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) { return; }
                lock (stderr)
                {
                    if (stderr.Length < MaxOutputChars) { stderr.AppendLine(e.Data); }
                }
            };

            if (!process.Start())
            {
                return CommandResult.Fail("无法启动 shell 进程。", interpreter);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout ?? DefaultTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // 超时或用户取消：**必须把进程杀掉**，否则它会一直挂着
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // 杀不掉也没别的办法
                }

                sw.Stop();

                var reason = ct.IsCancellationRequested ? "已取消。" : "执行超时（命令可能卡在等待输入）。";

                return new CommandResult(false, -1,
                    CombineOutput(stdout, stderr), interpreter, sw.ElapsedMilliseconds, reason);
            }

            // 让异步读取把最后几行吐完
            process.WaitForExit();
            sw.Stop();

            var output = CombineOutput(stdout, stderr);
            var exit = process.ExitCode;

            Log.Line($"命令执行完成：{kind} 退出码={exit}，耗时 {sw.ElapsedMilliseconds} ms");

            return new CommandResult(exit == 0, exit, output, interpreter, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            Log.Exception("执行命令失败", ex);
            return CommandResult.Fail($"执行失败：{ex.Message}", interpreter);
        }
    }

    private static string CombineOutput(StringBuilder stdout, StringBuilder stderr)
    {
        string so, se;

        lock (stdout) { so = stdout.ToString(); }
        lock (stderr) { se = stderr.ToString(); }

        var sb = new StringBuilder();

        if (so.Length > 0)
        {
            sb.Append(so.TrimEnd());
        }

        if (se.Length > 0)
        {
            if (sb.Length > 0) { sb.Append('\n'); }
            sb.Append("──── 错误输出 ────\n").Append(se.TrimEnd());
        }

        if (sb.Length == 0)
        {
            sb.Append("（命令没有产生任何输出）");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 强制 shell 用 UTF-8 输出。
    ///
    /// 为什么需要：中文 Windows 的默认代码页是 GBK，
    /// 而我们读取端固定按 UTF-8 解 —— 不统一就会满屏乱码。
    /// 与其在读取端猜编码（猜错更难查），不如在命令端直接定死。
    /// </summary>
    /// <summary>
    /// 查出 shell 实际输出用的编码。
    ///
    /// 做法：直接跑一句"把当前代码页写进 stdout"的命令，读回来。
    /// 为什么不用 <c>Console.OutputEncoding</c> 或猜：
    ///   · 那是**我们自己进程**的控制台编码，跟子进程 cmd 的不一定相同；
    ///   · 猜（"中文 Windows 一定是 936"）实测就是错的 —— 本机是 65001。
    /// 问一次最可靠，代价是几十毫秒，而且只在每次执行命令时查一次。
    ///
    /// 查不到时退到系统 ANSI 代码页（中文 Windows 上是 936/GBK），
    /// 这个兜底在绝大多数中文机器上都对。
    /// </summary>
    private static Encoding ResolveConsoleEncoding(ShellKind kind)
    {
        lock (EncodingCache)
        {
            if (EncodingCache.TryGetValue(kind, out var cached))
            {
                return cached;
            }
        }

        Encoding resolved;

        try
        {
            var interpreter = ResolveInterpreter(kind);
            var psi = new ProcessStartInfo
            {
                FileName = interpreter,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (kind == ShellKind.Cmd)
            {
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("chcp");
            }
            else
            {
                // PowerShell：用 .NET API 直接问，不依赖任何外部命令
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("[Console]::OutputEncoding.CodePage");
            }

            // 探测本身**不能**指定 StandardOutputEncoding —— 那正是我们要问的东西。
            // 用 Latin1 去解（输出是纯数字/ASCII，任何编码都解得对）。
            psi.StandardOutputEncoding = Encoding.Latin1;

            using var probe = Process.Start(psi);
            if (probe is null)
            {
                return FallbackEncoding();
            }

            var text = probe.StandardOutput.ReadToEnd();
            probe.WaitForExit(5000);

            var digits = new string(text.Where(char.IsDigit).ToArray());

            if (digits.Length > 0 && int.TryParse(digits, out var cp) && cp > 0)
            {
                resolved = Encoding.GetEncoding(cp);
                Log.Line($"命令输出编码：代码页 {cp}（{resolved.WebName}）");
            }
            else
            {
                resolved = FallbackEncoding();
                Log.Line($"代码页探测没拿到数字（原文「{text.Trim()}」），改用兜底编码 {resolved.WebName}");
            }
        }
        catch (Exception ex)
        {
            Log.Exception("探测控制台代码页失败，改用系统 ANSI", ex);
            resolved = FallbackEncoding();
        }

        lock (EncodingCache)
        {
            EncodingCache[kind] = resolved;
        }

        return resolved;
    }

    /// <summary>兜底编码：系统 ANSI 代码页（中文 Windows 上是 936/GBK）。</summary>
    private static Encoding FallbackEncoding()
    {
        try
        {
            // 让 .NET 在 .NET Core 上也能用非 UTF-8 编码（需要注册 CodePages provider）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(0);   // 0 = 系统 ANSI 代码页
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}
