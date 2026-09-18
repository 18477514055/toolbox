using System.Runtime.InteropServices;
using Toolbox.Core;
using Path = System.IO.Path;

namespace Toolbox.Tools.Convert;

/// <summary>
/// 备选后端：Microsoft Office 的 COM 自动化。
///
/// 只做**一件事**——PDF → docx。理由：这是 LibreOffice 做不到的那一格
///（soffice 不能可靠读 PDF），而 Word 2013 以后能打开 PDF 并转成可编辑文档。
/// 其余转换一律优先走 LibreOffice：更快、不弹窗、不会留下僵死的 WINWORD 进程。
///
/// ⚠️ COM 自动化的固有毛病（交接文档已列为风险）：慢、会弹窗、可能留僵尸进程。
/// 所以这里全程 try/catch，失败就给明确中文提示，绝不静默。
/// </summary>
internal sealed class OfficeComConverter : IConverter
{
    public string Name => "Microsoft Office";

    // Word 的 FileFormat 常量
    private const int WdFormatXmlDocument = 12;

    public bool IsAvailable(out string? reason)
    {
        if (Type.GetTypeFromProgID("Word.Application") is null)
        {
            reason = "没检测到 Microsoft Word，PDF → Word 这一格不可用（其余转换不受影响）。";
            return false;
        }

        reason = null;
        return true;
    }

    public bool CanConvert(string sourcePath, string targetFormat)
        => Formats.IsPdf(sourcePath)
           && string.Equals(targetFormat, "docx", StringComparison.OrdinalIgnoreCase);

    public Task<ConvertResult> ConvertAsync(
        string sourcePath,
        string targetFormat,
        string outputDir,
        CancellationToken ct)
    {
        // COM 必须在 STA 线程上跑，而调用方在后台线程池里
        return Task.Run(() =>
        {
            // ★ 开工前先记下"Word 是不是本来就在跑"。
            //
            //   Word 的 COM 服务器是单实例的：用户已经开着 Word 时，我们 CreateInstance
            //   拿到的其实是**他那个实例**。这时候如果照旧在收尾里 Quit()，就会把他正在
            //   编辑、可能还没保存的文档一起关掉 —— 这是数据损失，不是小毛病。
            //   所以：本来就在跑 → 只关我们打开的那份文档，绝不 Quit；
            //         本来没跑   → 这个进程是我们拉起来的，收尾可以放心 Quit。
            var preExisting = WordProcessIds();

            return RunSta(() =>
            {
                dynamic? word = null;
                dynamic? document = null;

                try
                {
                    var type = Type.GetTypeFromProgID("Word.Application");
                    if (type is null)
                    {
                        return new ConvertResult(false, null,
                            "没检测到 Microsoft Word，无法把 PDF 转成 Word 文档。");
                    }

                    ct.ThrowIfCancellationRequested();

                    var baseName = Path.GetFileNameWithoutExtension(sourcePath);
                    var outputPath = FileNaming.UniquePath(Path.Combine(outputDir, baseName + ".docx"));

                    word = Activator.CreateInstance(type);
                    if (word is null)
                    {
                        return new ConvertResult(false, null, "启动 Word 失败。");
                    }

                    // 不显示界面、不弹对话框——否则会卡在那里等用户点确定
                    word.Visible = false;
                    word.DisplayAlerts = 0;

                    // 位置参数写法：动态调用 COM 时命名参数容易在运行时绑定上出问题
                    document = word.Documents.Open(sourcePath, false, true, false);

                    ct.ThrowIfCancellationRequested();

                    document.SaveAs2(outputPath, WdFormatXmlDocument);

                    return new ConvertResult(true, outputPath, "由 Word 转换");
                }
                catch (OperationCanceledException)
                {
                    return new ConvertResult(false, null, "已取消");
                }
                catch (Exception ex)
                {
                    Log.Exception($"Office 转换失败：{sourcePath}", ex);
                    return new ConvertResult(false, null,
                        $"用 Word 转换失败：{ex.Message}\n"
                        + "（PDF 转 Word 依赖 Office，失败时可以先试着用 LibreOffice 转成 PDF 之外的格式，或换一台装了 Word 的机器。）");
                }
                finally
                {
                    try
                    {
                        // 只关我们打开的这一份（0 = 不保存改动），
                        // 用户的其它文档不受影响 —— 所以这一步不管怎样都做。
                        document?.Close(0);
                    }
                    catch
                    {
                        // 关不掉就算了
                    }

                    if (preExisting.Count == 0)
                    {
                        try
                        {
                            word?.Quit();
                        }
                        catch
                        {
                            // 同上
                        }
                    }
                    else
                    {
                        Log.Line("Word 本来就在运行，跳过 Quit（否则会关掉用户自己的文档）。");
                    }

                    ReleaseCom(document);
                    ReleaseCom(word);
                }
            }, preExisting);
        }, ct);
    }

    // ---------------------------------------------------------------- Word 进程

    private static HashSet<int> WordProcessIds()
    {
        var ids = new HashSet<int>();

        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("WINWORD"))
            {
                using (p)
                {
                    ids.Add(p.Id);
                }
            }
        }
        catch
        {
            // 拿不到进程列表不影响转换本身
        }

        return ids;
    }

    /// <summary>
    /// 只杀**这次转换期间新冒出来**的 WINWORD。
    ///
    /// 这是超时后唯一能做的补救：COM 调用卡在 Word 进程里时，
    /// `Thread.Join` 的超时只是让**我们**不等了，那个 STA 线程连同 Word 进程还活着
    /// （.NET Core 起就没有 Thread.Abort 了，没法从外部掐断）。
    /// 结果就是 finally 里的 Quit 永远执行不到，任务管理器里留下一个看不见的 WINWORD。
    ///
    /// 所以按"开工前快照"做差集，只动新的那些 —— 用户自己的 Word 一个都不碰。
    /// </summary>
    private static int KillNewWordProcesses(HashSet<int> preExisting)
    {
        var killed = 0;

        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("WINWORD"))
            {
                using (p)
                {
                    if (preExisting.Contains(p.Id))
                    {
                        continue;
                    }

                    try
                    {
                        p.Kill();
                        killed++;
                    }
                    catch
                    {
                        // 已经自己退了 / 权限不够，都无所谓
                    }
                }
            }
        }
        catch
        {
            // 同上
        }

        return killed;
    }

    private static void ReleaseCom(object? comObject)
    {
        try
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
        catch
        {
            // .NET 里 COM 释放失败不值得让整个转换失败
        }
    }

    /// <summary>在一个临时 STA 线程上执行（COM 组件要求 STA）。</summary>
    private static T RunSta<T>(Func<T> func, HashSet<int> preExisting)
    {
        T result = default!;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Toolbox.OfficeCom",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // 给足时间：Word 打开一个大 PDF 可能要几十秒
        if (!thread.Join(TimeSpan.FromMinutes(5)))
        {
            var killed = KillNewWordProcesses(preExisting);

            Log.Line(killed > 0
                ? $"Word 转换超时，已清理 {killed} 个僵死 WINWORD 进程。"
                : "Word 转换超时，但没有发现需要清理的 WINWORD 进程。");

            var hint = killed > 0
                ? "（已经把它留下的 Word 进程清理掉了。）"
                : "";

            return (T)(object)new ConvertResult(false, null,
                $"Word 转换超时（超过 5 分钟）。这份 PDF 可能太大或结构太复杂，"
                + $"建议改用 LibreOffice 转成其它格式。{hint}");
        }

        if (error is not null)
        {
            throw error;
        }

        return result;
    }
}
