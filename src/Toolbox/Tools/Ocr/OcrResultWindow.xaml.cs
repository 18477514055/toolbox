using System.Windows;
using System.Windows.Threading;
using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.Ocr;

/// <summary>
/// OCR 结果窗口。
///
/// 关键设计（沿用"结果必须可编辑"这条原则）：
///   **识别结果放在可编辑的文本框里**，而不是只读展示。
///   OCR 一定会有个别字不准，用户能直接改掉再复制，比"复制出来再找地方改"顺得多。
///   这和 AI 面板"让用户看到并编辑将要送出的文本"是同一个思路。
/// </summary>
internal sealed partial class OcrResultWindow : Window
{
    private readonly ToolboxContext _ctx;

    public OcrResultWindow(ToolboxContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        CopyBtn.Click += (_, _) => Copy(closeAfter: false);
        CopyCloseBtn.Click += (_, _) => Copy(closeAfter: true);
        CloseBtn.Click += (_, _) => Close();

        // Esc 关闭
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    /// <summary>把识别结果显示出来。</summary>
    public void ShowResult(OcrResult result)
    {
        if (result.Success)
        {
            ResultBox.Text = result.Text;
            StatusText.Text = $"识别到 {result.Lines.Count} 行 / {result.Text.Length} 字"
                              + $"（引擎：Windows 自带 OCR · {OcrEngineService.CurrentLanguageTag}）";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1F, 0x23, 0x29));

            // 自动复制一份：多数人就是想要"识别完直接粘"
            CopyToClipboard(silent: true);
            SaveHint.Text = "已复制到剪贴板";
            ClearHintLater();
        }
        else
        {
            ResultBox.Text = "";
            StatusText.Text = result.Error ?? "识别失败。";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE0, 0x3B, 0x3B));
        }

        Show();
        Activate();
        ResultBox.Focus();
    }

    private void Copy(bool closeAfter)
    {
        if (CopyToClipboard(silent: false) && closeAfter)
        {
            Close();
        }
    }

    /// <summary>
    /// 复制到剪贴板。
    /// ⚠️ 走 <see cref="ClipboardWriter"/>（带自污染标记）——
    /// 这是**用户主动要的**写剪贴板，属于规则③允许的范围，
    /// 而且打了标记 ⇒ 不会污染剪贴板历史。
    /// </summary>
    private bool CopyToClipboard(bool silent)
    {
        var text = ResultBox.Text;

        if (string.IsNullOrEmpty(text))
        {
            if (!silent)
            {
                SaveHint.Text = "没有内容可复制";
            }

            return false;
        }

        if (ClipboardWriter.SetText(text, out var error))
        {
            if (!silent)
            {
                SaveHint.Text = "已复制到剪贴板";
                ClearHintLater();
            }

            return true;
        }

        // 复制失败要说清楚，不能静默
        SaveHint.Text = $"复制失败：{error}";
        Log.Error($"OCR 结果复制到剪贴板失败：{error}");
        return false;
    }

    private void ClearHintLater()
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(1800);
            SaveHint.Text = "";
        }), DispatcherPriority.Background);
    }
}
