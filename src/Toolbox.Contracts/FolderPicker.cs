using System.IO;
using System.Windows;
using Toolbox.Core;

namespace Toolbox.Shell;

/// <summary>
/// 统一的"选文件夹"对话框。
///
/// ═══════════════════════════════════════════════════════════════════════
///  为什么要有这个东西（实测故障，见 DECISIONS 坑 49）
/// ═══════════════════════════════════════════════════════════════════════
///
/// 用户反馈：**「格式转换」里的「选择…」（选输出目录）点了没反应**，
/// 而设置里「截图保存目录」的浏览能用。
///
/// 查下来两者用的是**两种完全不同的实现**：
///   · 能用的（设置页）  → WinForms `FolderBrowserDialog`（Shell32 SHBrowseForFolder）
///   · 不能用的（转换页）→ WPF `Microsoft.Win32.OpenFolderDialog`（COM IFileDialog）
///
/// 而且失败时**没有任何反馈** —— `ShowDialog` 没弹出来、
/// 没抛异常、没写日志，用户只看到"点了没反应"。
///
/// 修法（两条一起，缺一不可）：
///   ① **优先用 WinForms 那个**（已被证明在这台机器上可用）；
///   ② 失败时**逐级降级**并**把原因告诉用户**，绝不静默。
///
/// ⚠️ 教训：**同一个功能在不同工具里用了两种实现，就会出现
///    "A 能用 B 不能用"这种极难解释的现象。** 统一到一个地方才是根治。
/// </summary>
public static class FolderPicker
{
    /// <summary>
    /// 弹出"选择文件夹"对话框。
    /// </summary>
    /// <param name="owner">父窗口（用于正确居中/模态，可为 null）。</param>
    /// <param name="title">标题。</param>
    /// <param name="initialDir">初始目录（不存在时忽略）。</param>
    /// <param name="picked">用户选中的路径。</param>
    /// <param name="error">失败原因（可直接显示给用户）。</param>
    /// <returns>true = 选好了；false = 用户取消或失败（看 error 是否为空）。</returns>
    public static bool TryPick(
        Window? owner,
        string title,
        string? initialDir,
        out string picked,
        out string? error)
    {
        picked = "";
        error = null;

        // ★ 第一选择：WinForms 的 FolderBrowserDialog。
        //
        //   为什么把它放第一位而不是 WPF 的 OpenFolderDialog：
        //   用户实测这台机器上**它能弹出来**，而 WPF 那个不行。
        //   两者底层不同（SHBrowseForFolder vs COM IFileDialog），
        //   在单文件自包含发布下表现也不同。
        try
        {
            var ok = TryWinForms(title, initialDir, out picked, out var wfError);

            if (ok)
            {
                return true;
            }

            // 用户点了取消，不是错误
            if (wfError is null)
            {
                return false;
            }

            Log.Line($"WinForms 文件夹对话框失败，改用 WPF 那个：{wfError}");
        }
        catch (Exception ex)
        {
            Log.Exception("WinForms 文件夹对话框异常", ex);
        }

        // ★ 第二选择：WPF 的 OpenFolderDialog
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };

            if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
            {
                dlg.InitialDirectory = initialDir;
            }

            var result = owner is not null ? dlg.ShowDialog(owner) : dlg.ShowDialog();

            if (result == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                picked = dlg.FolderName;
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Exception("WPF 文件夹对话框异常", ex);
            error = $"两种文件夹对话框都打不开：{ex.Message}";
            return false;
        }
    }

    private static bool TryWinForms(string title, string? initialDir, out string picked, out string? error)
    {
        picked = "";
        error = null;

        try
        {
            // 全限定名调用 —— UseWindowsForms 会把 WinForms 类型塞进全局 using，
            // 和 WPF 的 Window/Button 等重名，全限定最稳
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = title,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
            };

            // 只有目录真存在才设 —— 设一个不存在的路径会让对话框弹不出来（实测）
            if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
            {
                dlg.SelectedPath = initialDir;
            }

            var result = dlg.ShowDialog();

            if (result == System.Windows.Forms.DialogResult.OK
                && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                picked = dlg.SelectedPath;
                return true;
            }

            // 用户取消 —— 不算错误
            return false;
        }
        catch (Exception ex)
        {
            Log.Exception("WinForms FolderBrowserDialog 失败", ex);
            error = ex.Message;
            return false;
        }
    }
}
