using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

// ============================================================================
// 「干净电脑」模拟器
//
// 目的：回答"能不能分享给别人"这个问题。做法是**真的把环境削到最低**再启动 exe，
//   而不是靠读文档下结论。
//
// 削掉的东西：
//   · PATH 只留 System32 / Windows（⇒ 找不到 dotnet、soffice、任何第三方程序）
//   · 删掉 DOTNET_* 全部变量、ProgramFiles 变量
//   · 工作目录设成 System32（模拟开机自启的真实情形）
//
// 保留的（这些在**任何** Windows 上都有，属于"天生就有"）：
//   SystemRoot / windir / SystemDrive / TEMP / USERPROFILE / LOCALAPPDATA /
//   APPDATA / ProgramData / COMPUTERNAME / USERNAME / NUMBER_OF_PROCESSORS
// ============================================================================

var exe = args.Length > 0
    ? args[0]
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "桌面工具箱", "Toolbox.exe");

Console.WriteLine("==================================================");
Console.WriteLine("「干净电脑」模拟测试");
Console.WriteLine("==================================================");
Console.WriteLine($"被测 exe : {exe}");
Console.WriteLine($"存在     : {File.Exists(exe)}");
Console.WriteLine();

if (!File.Exists(exe))
{
    Console.WriteLine("FAILED: exe 不存在");
    return 2;
}

// ---- 构造最小环境 ----
var minimal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["PATH"] = @"C:\Windows\System32;C:\Windows",
    ["SystemRoot"] = @"C:\Windows",
    ["windir"] = @"C:\Windows",
    ["SystemDrive"] = "C:",
    ["TEMP"] = Path.GetTempPath().TrimEnd('\\'),
    ["TMP"] = Path.GetTempPath().TrimEnd('\\'),
    ["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    ["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    ["ProgramData"] = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData",
    ["COMPUTERNAME"] = Environment.MachineName,
    ["USERNAME"] = Environment.UserName,
    ["NUMBER_OF_PROCESSORS"] = Environment.ProcessorCount.ToString(),
    ["PROCESSOR_ARCHITECTURE"] = "AMD64",
};

Console.WriteLine("--- 削减后的环境（这就是「干净电脑」看到的）---");
foreach (var kv in minimal)
{
    var v = kv.Key.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase)
         || kv.Key.Equals("USERNAME", StringComparison.OrdinalIgnoreCase)
        ? "(略)" : kv.Value;
    Console.WriteLine($"  {kv.Key,-24} = {v}");
}
Console.WriteLine();
Console.WriteLine("  已剔除：PATH 里的 dotnet / Program Files、全部 DOTNET_* 变量、ProgramFiles(x86)");
Console.WriteLine();

// ---- 逐个场景测试 ----
var scenarios = new (string Name, string Args, string WorkDir)[]
{
    ("① 命令行自检（最全面的功能探针）", "--selftest", @"C:\Windows\System32"),
    ("② 开机自启路径（--startup，真实开机 cwd）", "--autostart status", @"C:\Windows\System32"),
    ("③ 直接启动主程序（托盘 + 悬浮窗 + 热键）", "", @"C:\Windows\System32"),
};

var results = new List<(string Name, bool Ok, string Detail)>();

foreach (var (name, argStr, workDir) in scenarios)
{
    Console.WriteLine($"--- {name} ---");

    var psi = new ProcessStartInfo
    {
        FileName = exe,
        WorkingDirectory = workDir,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    foreach (var a in argStr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        psi.ArgumentList.Add(a);
    }

    // 清空继承来的环境，再放最小集合 —— 这才是"干净电脑"
    psi.EnvironmentVariables.Clear();
    foreach (var kv in minimal)
    {
        psi.EnvironmentVariables[kv.Key] = kv.Value;
    }

    using var p = new Process { StartInfo = psi };
    var sb = new StringBuilder();

    try
    {
        p.Start();
        sb.Append(p.StandardOutput.ReadToEnd());
        sb.Append(p.StandardError.ReadToEnd());

        // 无参数时要常驻（托盘程序），等 15 秒确认它没崩
        var expectExit = argStr.Length > 0;
        bool ok;
        string detail;

        if (expectExit)
        {
            if (!p.WaitForExit(120_000))
            {
                p.Kill(true);
                ok = false;
                detail = "超时未退出";
            }
            else
            {
                ok = p.ExitCode == 0;
                detail = $"exit={p.ExitCode}";
            }
        }
        else
        {
            // 常驻程序：等 15 秒，还活着就算成功，然后杀掉
            System.Threading.Thread.Sleep(15_000);
            if (p.HasExited)
            {
                ok = false;
                detail = $"不该退出却退出了 exit={p.ExitCode}";
            }
            else
            {
                ok = true;
                detail = "启动后持续运行 15 秒未崩溃";
                p.Kill(true);
            }
        }

        Console.WriteLine($"  {(ok ? "✅ 通过" : "❌ 失败")}  {detail}");

        // 把输出里的关键行挑出来
        var lines = sb.ToString().Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0
                        && (l.Contains("自检结果") || l.Contains("路径比对")
                            || l.Contains("当前状态") || l.Contains("错误") || l.Contains("异常")))
            .Take(6);

        foreach (var l in lines)
        {
            Console.WriteLine($"       {l}");
        }

        results.Add((name, ok, detail));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ❌ 异常：{ex.Message}");
        results.Add((name, false, ex.Message));
    }

    Console.WriteLine();
}

Console.WriteLine("--------------------------------------------------");
var passed = results.Count(r => r.Ok);
Console.WriteLine($"「干净电脑」模拟结果：{passed}/{results.Count} 通过");
foreach (var (name, ok, detail) in results)
{
    Console.WriteLine($"  {(ok ? "✅" : "❌")} {name} —— {detail}");
}
Console.WriteLine("--------------------------------------------------");

return passed == results.Count ? 0 : 1;
