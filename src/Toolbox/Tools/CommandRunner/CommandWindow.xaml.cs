using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.CommandRunner;

/// <summary>
/// 运行命令窗口。
///
/// 交互上刻意做的三件事（安全相关，见 <see cref="CommandRunner"/> 的类注释）：
///   ① **回车 = 换行**，只有 **Ctrl+Enter** 才执行 —— 多行命令很常见，
///      回车直接执行会误触；而且这个功能执行的是任意命令，误触代价大。
///   ② 实时显示**将要执行的完整命令行**（含拼给 shell 的参数），供用户核对。
///   ③ 输出区**完整回显** stdout + stderr + 退出码，不美化。
/// </summary>
internal sealed partial class CommandWindow : Window
{
    private readonly ToolboxContext _ctx;
    private CancellationTokenSource? _cts;
    private bool _running;

    public CommandWindow(ToolboxContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        RunBtn.Click += async (_, _) => await RunAsync();
        CancelBtn.Click += (_, _) => _cts?.Cancel();
        CopyOutputBtn.Click += (_, _) => CopyOutput();
        CloseBtn.Click += (_, _) => Close();

        // 命令变化时实时更新"将要执行"的预览
        CommandBox.TextChanged += (_, _) => UpdatePreview();
        CommandBox.PreviewKeyDown += OnCommandKeyDown;

        Loaded += (_, _) =>
        {
            UpdateShellHint();
            UpdatePreview();
            CommandBox.Focus();
        };

        PreviewKeyDown += (_, e) =>
        {
            // Esc 只在**不在输入框里**的时候关闭窗口 ——
            // 否则用户想取消输入时会把整个窗口关掉，丢掉已经敲的命令
            if (e.Key == Key.Escape && !CommandBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private ShellKind SelectedShell()
        => ShellCombo.SelectedIndex == 1 ? ShellKind.PowerShell : ShellKind.Cmd;

    private void OnShellChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateShellHint();
        UpdatePreview();
    }

    /// <summary>
    /// 界面控件是否已经建好。
    ///
    /// ⚠️ 为什么需要这个判据（实测抓到的真 bug，见 DECISIONS 坑 48）：
    ///   XAML 里 shell 下拉框写了 `SelectedIndex="0"` 与
    ///   `SelectionChanged="OnShellChanged"`。两者组合的后果是 ——
    ///   **在 InitializeComponent() 解析 XAML 的过程中**，那个 ComboBox
    ///   一旦建好并设上 SelectedIndex，就立刻触发 OnShellChanged；
    ///   而此时排在 XAML **后面**的 PreviewText / ShellHint 仍然是 null。
    ///
    ///   于是 `PreviewText.Text = ...` 抛 NullReferenceException，
    ///   而 catch 块里**又**写了一次 `PreviewText.Text` ——
    ///   异常从 catch 里再次逃出，一路冒到工具的 Invoke()，
    ///   结果**整个窗口都打不开**（用户看到的就是"点了没反应"）。
    ///
    ///   修法：显式判"控件是否就绪"，而不是靠异常控制流。
    /// </summary>
    private bool PreviewControlsReady => PreviewText is not null && ShellHint is not null;

    private void UpdateShellHint()
    {
        // 控件还没建好就跳过 —— 少更新一次无害，抛异常会让窗口打不开
        if (ShellHint is null)
        {
            return;
        }

        try
        {
            var kind = SelectedShell();
            var exe = CommandRunner.ResolveInterpreter(kind);

            var note = kind == ShellKind.PowerShell
                ? (System.IO.Path.GetFileNameWithoutExtension(exe)
                       .Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                    ? "（PowerShell 7+）"
                    : "（Windows PowerShell 5.1）")
                : "";

            ShellHint.Text = $"{exe} {note}";
        }
        catch (Exception ex)
        {
            // ★ catch 里也不能假设控件存在 —— 这正是本次 bug 的成因：
            //   原来的 catch 直接写 ShellHint.Text，异常二次抛出逃走了
            if (ShellHint is not null)
            {
                ShellHint.Text = $"解析 shell 失败：{ex.Message}";
            }

            Log.Exception("刷新 shell 提示失败", ex);
        }
    }

    /// <summary>实时把"将要执行什么"显示给用户（可核对，这是安全设计的一部分）。</summary>
    private void UpdatePreview()
    {
        // 同上：InitializeComponent 期间可能还没建好
        if (PreviewText is null || CommandBox is null)
        {
            return;
        }

        try
        {
            var command = CommandBox.Text;

            if (string.IsNullOrWhiteSpace(command))
            {
                PreviewText.Text = "（还没输入命令）";
                return;
            }

            PreviewText.Text = CommandRunner.DescribeCommandLine(
                SelectedShell(), command, null);
        }
        catch (Exception ex)
        {
            // catch 里同样要判 null（本次 bug 的直接原因就在这里）
            if (PreviewText is not null)
            {
                PreviewText.Text = $"预览失败：{ex.Message}";
            }

            Log.Exception("刷新命令预览失败", ex);
        }
    }

    /// <summary>Ctrl+Enter 执行；单独回车留给换行。</summary>
    private async void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await RunAsync();
        }
    }

    private async Task RunAsync()
    {
        // 置忙必须在第一个 await 之前（见 DECISIONS 坑 19）
        if (_running)
        {
            return;
        }

        var command = CommandBox.Text;

        if (string.IsNullOrWhiteSpace(command))
        {
            StatusText.Text = "先输入命令。";
            return;
        }

        _running = true;
        RunBtn.IsEnabled = false;
        CancelBtn.IsEnabled = true;

        _cts = new CancellationTokenSource();

        var kind = SelectedShell();
        var kindName = kind == ShellKind.Cmd ? "cmd" : "PowerShell";

        StatusText.Text = $"正在执行（{kindName}）…";
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x8A, 0x94, 0xA6));
        OutputBox.Text = "";

        try
        {
            var result = await CommandRunner.RunAsync(kind, command, null, _cts.Token);

            OutputBox.Text = result.Output;

            if (result.Error is not null)
            {
                StatusText.Text = $"{result.Error}　耗时 {result.ElapsedMs} ms";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE0, 0x3B, 0x3B));
            }
            else if (result.Success)
            {
                StatusText.Text = $"执行完成，退出码 0，耗时 {result.ElapsedMs} ms";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x12, 0xA1, 0x50));
            }
            else
            {
                // 非 0 退出码**不是**程序出错，是命令自己返回了失败 ——
                // 要把这一点说清楚，否则用户会以为工具箱坏了
                StatusText.Text = $"命令执行完毕，但退出码是 {result.ExitCode}（命令自身返回了失败），"
                                  + $"耗时 {result.ElapsedMs} ms";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xD9, 0x7A, 0x06));
            }

            Log.Line($"运行命令：{kindName}，退出码 {result.ExitCode}，{result.ElapsedMs} ms");
        }
        catch (Exception ex)
        {
            Log.Exception("运行命令流程失败", ex);
            OutputBox.Text = ex.ToString();
            StatusText.Text = $"出错：{ex.Message}";
        }
        finally
        {
            _running = false;
            RunBtn.IsEnabled = true;
            CancelBtn.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CopyOutput()
    {
        var text = OutputBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 走 ClipboardWriter（带自污染标记）⇒ 不进剪贴板历史
        if (ClipboardWriter.SetText(text, out var err))
        {
            _ctx.Notify("运行命令", "输出已复制到剪贴板。");
        }
        else
        {
            StatusText.Text = $"复制失败：{err}";
        }
    }
}
