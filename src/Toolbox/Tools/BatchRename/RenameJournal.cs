using System.Text;
using System.Text.Json;
using Toolbox.Core;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace Toolbox.Tools.BatchRename;

/// <summary>
/// 一次改名操作的记录（用于撤销）。
/// </summary>
internal sealed class RenameJournal
{
    public string Time { get; set; } = "";

    /// <summary>操作发生在哪个目录（显示用）。</summary>
    public string Directory { get; set; } = "";

    /// <summary>逐条改名记录。**撤销时按倒序执行**。</summary>
    public List<RenameMove> Moves { get; set; } = new();

    /// <summary>撤销过了吗（撤销一次后就不该再撤第二次）。</summary>
    public bool Undone { get; set; }

    public int Count => Moves.Count;
}

internal sealed class RenameMove
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>
/// 改名的"账本"：每次执行都把"谁改成了谁"落盘，供撤销用。
///
/// 为什么必须落盘而不是只放在内存里：
///   用户改错了名，很可能**过一会儿才想撤销**（甚至重启过工具箱）。
///   只放内存的话，程序一退就没得撤了 —— 而那正是最需要撤销的时候。
///
/// 存在数据目录下的 <c>rename-journal.json</c>，只保留**最近一次**操作。
/// 只留最近一次是刻意的：撤销两次、三次会让人搞不清当前到底处在哪个状态，
/// 而"撤销"这个功能的全部价值就在于**确定性**。
/// </summary>
internal static class RenameJournalStore
{
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(AppPaths.Root, "rename-journal.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static void Save(RenameJournal journal)
    {
        lock (Gate)
        {
            try
            {
                var json = JsonSerializer.Serialize(journal, Options);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));

                if (File.Exists(FilePath))
                {
                    File.Replace(temp, FilePath, null);
                }
                else
                {
                    File.Move(temp, FilePath);
                }

                Log.Line($"改名账本已保存：{journal.Count} 条 → {FilePath}");
            }
            catch (Exception ex)
            {
                // 账本存不下来**必须说清楚** —— 用户会以为还能撤销，实际不能
                Log.Exception("保存改名账本失败（本次操作将无法撤销）", ex);
            }
        }
    }

    public static RenameJournal? Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }

                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<RenameJournal>(json, Options);
            }
            catch (Exception ex)
            {
                Log.Exception("读取改名账本失败", ex);
                return null;
            }
        }
    }

    /// <summary>撤销后把账本标记掉（或删掉），避免重复撤销。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("清除改名账本失败", ex);
            }
        }
    }

    /// <summary>
    /// 撤销。**按倒序**执行改名，且逐条检查可行性。
    /// </summary>
    /// <returns>(成功撤销条数, 失败说明)。</returns>
    public static (int Undone, string Message) Undo(RenameJournal journal)
    {
        var ok = 0;
        var problems = new List<string>();

        // 倒序：若正序改过 a→b、b→c，倒着撤才不会互相踩
        for (var i = journal.Moves.Count - 1; i >= 0; i--)
        {
            var m = journal.Moves[i];

            try
            {
                if (!File.Exists(m.To))
                {
                    problems.Add($"找不到「{Path.GetFileName(m.To)}」（可能已被移走或删除）");
                    continue;
                }

                if (File.Exists(m.From))
                {
                    problems.Add($"「{Path.GetFileName(m.From)}」已存在，无法还原（不覆盖）");
                    continue;
                }

                File.Move(m.To, m.From);
                ok++;
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(m.To)}：{ex.Message}");
            }
        }

        var msg = ok == journal.Moves.Count
            ? $"已撤销 {ok} 个文件的改名。"
            : $"撤销了 {ok}/{journal.Moves.Count} 个。" +
              (problems.Count > 0 ? "未还原的：" + string.Join("；", problems.Take(3)) : "");

        return (ok, msg);
    }
}
