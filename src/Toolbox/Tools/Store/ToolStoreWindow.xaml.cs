using System.IO;
using System.Windows;
using Toolbox.Core;
using Toolbox.Shell;
// 别名会遮蔽 System.IO 下的其它类型，所以上面显式 using System.IO;
using File = System.IO.File;
using Path = System.IO.Path;
using Directory = System.IO.Directory;

namespace Toolbox.Tools.Store;

/// <summary>清单列表里的一行。</summary>
internal sealed class StoreRow
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string DefaultHotKey { get; init; } = "";

    /// <summary>"随主程序" / "可从仓库下载"。</summary>
    public string Origin { get; init; } = "";

    /// <summary>当前状态（已启用 / 已下载 / 可下载 …）。</summary>
    public string Status { get; init; } = "";

    public bool BuiltIn { get; init; }

    public ToolPackage? Package { get; init; }
}

/// <summary>
/// 工具管理窗口 —— **按需下载**的入口。
///
/// 现状与规划要说清楚（避免让人误以为已经能下"不存在的工具"）：
///   · 目前 12 个工具**都已经在主程序里**，所以这个窗口的作用是
///     "让你看到清单、知道有哪些工具、并检查仓库连通性"；
///   · 下载链路（多镜像 + SHA-256 校验）**已经实现并实测通过**，
///     等以后把工具拆成独立包发布时，直接就能用 —— 不用再改这里。
///
/// 为什么现在就把链路做出来：**拆包是下一轮的事，但仓库与下载通道必须先打通并验证**，
/// 否则下一轮会同时面对"架构重构"和"网络不通"两个问题，很难定位。
/// </summary>
internal sealed partial class ToolStoreWindow : Window
{
    private readonly ToolboxContext _ctx;
    private ToolManifest _manifest;
    private bool _busy;

    public ToolStoreWindow(ToolboxContext ctx)
    {
        _ctx = ctx;
        _manifest = ToolStore.BuiltInManifest();

        InitializeComponent();

        RefreshBtn.Click += async (_, _) => await RefreshAsync();
        DownloadBtn.Click += async (_, _) => await DownloadSelectedAsync();
        OpenRepoBtn.Click += (_, _) => OpenRepo();
        CloseBtn.Click += (_, _) => Close();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        // 先用内置清单把界面填上（保证**离线也有内容**，不是一片空白）
        Rebuild(manifestSource: null, error: null);
    }

    // ---------------------------------------------------------------- 刷新

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        RefreshBtn.IsEnabled = false;
        SourceText.Text = "正在拉取清单（会依次尝试直连与各个镜像）…";
        SourceText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x8A, 0x94, 0xA6));

        try
        {
            var (manifest, source, error) = await ToolStore.FetchManifestAsync(CancellationToken.None);

            if (manifest is not null)
            {
                _manifest = manifest;
                Rebuild(source, null);
                SetStatus($"清单已更新（来自 {source}），共 {manifest.Tools.Count} 个工具。", isError: false);
            }
            else
            {
                // 拉不到**不是致命错误** —— 内置清单仍然可用。
                // 要把这件事说清楚，否则用户会以为"工具管理坏了"。
                Rebuild(null, error);
                SetStatus("拉不到最新清单，正在使用**内置清单**（功能不受影响）。\n"
                          + "可能原因：网络不通、或仓库还没有 tools.json。", isError: true);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("刷新工具清单失败", ex);
            SetStatus($"刷新失败：{ex.Message}", isError: true);
        }
        finally
        {
            _busy = false;
            RefreshBtn.IsEnabled = true;
        }
    }

    private void Rebuild(string? manifestSource, string? error)
    {
        var rows = new List<StoreRow>();

        foreach (var t in _manifest.Tools)
        {
            // 当前是否启用（用 ToolGate 判定，与悬浮窗/热键保持同一套判据）
            var enabled = ToolGate.IsEnabled(_ctx.Settings, t.Id);

            string status;
            string origin;

            if (t.BuiltIn)
            {
                origin = "随主程序";
                status = enabled ? "已启用" : "已关闭（可在设置里开）";
            }
            else
            {
                origin = string.IsNullOrWhiteSpace(t.AssetName) ? "清单未提供文件" : "可下载";

                var downloaded = IsDownloaded(t.Id);
                status = downloaded ? "已下载" : "未下载";
            }

            rows.Add(new StoreRow
            {
                Id = t.Id,
                Name = t.Name,
                Description = t.Description,
                DefaultHotKey = t.DefaultHotKey,
                Origin = origin,
                Status = status,
                BuiltIn = t.BuiltIn,
                Package = t,
            });
        }

        ToolGrid.ItemsSource = rows;

        SourceText.Text = manifestSource is null
            ? (error is null
                ? $"当前使用**内置清单**（随主程序打包，离线可用）：{_manifest.Tools.Count} 个工具。"
                : $"拉取失败，已回退到**内置清单**：{_manifest.Tools.Count} 个工具。")
            : $"清单来源：**{manifestSource}**，{_manifest.Tools.Count} 个工具。";

        SourceText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x1F, 0x23, 0x29));

        // 下载按钮只在"选中了一个可下载条目"时可用
        DownloadBtn.IsEnabled = rows.Any(r => !r.BuiltIn && !string.IsNullOrWhiteSpace(r.Package?.AssetName));
    }

    /// <summary>这个工具是否已经下载到本地（放在数据目录的 tools\ 下）。</summary>
    private static bool IsDownloaded(string toolId)
    {
        try
        {
            var dir = Path.Combine(AppPaths.Root, "tools", toolId);
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any();
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 下载

    private async Task DownloadSelectedAsync()
    {
        if (_busy)
        {
            return;
        }

        var row = ToolGrid.SelectedItem as StoreRow;

        if (row?.Package is null)
        {
            SetStatus("先在列表里选一个工具。", isError: true);
            return;
        }

        var pkg = row.Package;

        // 内置工具不需要下载 —— 说清楚而不是静默什么都不做
        if (pkg.BuiltIn || string.IsNullOrWhiteSpace(pkg.AssetName))
        {
            SetStatus($"「{pkg.Name}」是随主程序内置的，不需要下载。\n"
                      + "想用它请到「设置 → 功能开关」里打开。", isError: false);
            return;
        }

        _busy = true;
        DownloadBtn.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;

        try
        {
            var dir = Path.Combine(AppPaths.Root, "tools", pkg.Id);
            Directory.CreateDirectory(dir);

            var savePath = Path.Combine(dir, pkg.AssetName);

            SetStatus($"正在下载「{pkg.Name}」…（会依次尝试直连与各个镜像）", isError: false);

            var progress = new Progress<double>(p => Progress.Value = p);

            var (ok, saved, source, error) = await ToolStore.DownloadAsync(
                "", pkg.AssetName, $"v{pkg.Version}", pkg.Sha256, savePath, progress, CancellationToken.None);

            if (ok)
            {
                var size = new FileInfo(saved).Length;
                SetStatus($"「{pkg.Name}」下载完成（来自 {source}，{size:N0} 字节），SHA-256 校验通过。",
                    isError: false);
                _ctx.Notify("工具管理", $"「{pkg.Name}」下载完成。");
            }
            else
            {
                SetStatus($"下载失败：{error}", isError: true);
            }

            Rebuild(null, null);
        }
        catch (Exception ex)
        {
            Log.Exception("下载工具失败", ex);
            SetStatus($"下载出错：{ex.Message}", isError: true);
        }
        finally
        {
            _busy = false;
            DownloadBtn.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenRepo()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"https://github.com/{ToolStore.RepoOwner}/{ToolStore.RepoName}")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Exception("打开仓库失败", ex);
        }
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(isError
            ? System.Windows.Media.Color.FromRgb(0xE0, 0x3B, 0x3B)
            : System.Windows.Media.Color.FromRgb(0x1F, 0x23, 0x29));
    }
}
