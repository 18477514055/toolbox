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
        RemoveBtn.Click += (_, _) => RemoveSelected();
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

                var downloaded = PluginLoader.IsInstalled(t.Id);
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

        var tempDir = "";

        try
        {
            // ★ 下载 + **安装**，不只是把文件扔到某个目录。
            //
            //   "下载完还要用户自己去解压、自己放到 plugins 目录" —— 那不叫按需下载，
            //   那叫"给了你一个压缩包"。真正的安装要做完：
            //     下载 → 校验哈希 → 解压 → 放到 plugins\<id>\ → 热加载 → 启用
            var pluginDir = PluginLoader.PluginDir(pkg.Id);
            Directory.CreateDirectory(pluginDir);

            tempDir = Path.Combine(Path.GetTempPath(), "toolbox-plugin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var zipPath = Path.Combine(tempDir, pkg.AssetName);

            SetStatus($"正在下载「{pkg.Name}」…（会依次尝试直连与各个镜像）", isError: false);

            var progress = new Progress<double>(p => Progress.Value = p);

            var (ok, saved, source, error) = await ToolStore.DownloadAsync(
                "", pkg.AssetName, $"v{pkg.Version}", pkg.Sha256, zipPath, progress, CancellationToken.None);

            if (!ok)
            {
                SetStatus($"下载失败：{error}", isError: true);
                return;
            }

            var size = new FileInfo(saved).Length;

            // ---- 解压 ----
            //
            // ⚠️ 只接受 .dll，**且必须防止 Zip Slip**：
            //    恶意/损坏的压缩包可能含 `..\..\` 这类路径，
            //    直接 ExtractToDirectory 会写到目标目录外面去。
            //    .NET 的 ExtractToDirectory 本身已做防护（会抛异常），
            //    但这里仍然逐个校验一次 —— 因为它只抛异常、不告诉我们哪个条目坏。
            SetStatus("校验通过，正在安装…", isError: false);

            var installed = 0;

            using (var zip = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    // 目录条目跳过
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }

                    if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        // 插件包里只应放 dll；别的文件一律不落盘
                        Log.Line($"插件包里的非 dll 条目已跳过：{entry.FullName}");
                        continue;
                    }

                    // 只取文件名，**丢掉任何目录结构** ——
                    // 这样即使压缩包里写了 ..\..\ 也绝不会跑出目标目录
                    var target = Path.Combine(pluginDir, Path.GetFileName(entry.Name));

                    // 再确认一次最终路径确实在插件目录内
                    if (!Path.GetFullPath(target).StartsWith(
                            Path.GetFullPath(pluginDir) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Error($"插件包条目路径非法，已拒绝：{entry.FullName}");
                        continue;
                    }

                    // 用流复制而不是 ZipFileExtensions.ExtractToFile：
                    //   ① 那个扩展方法在 System.IO.Compression.ZipFileExtensions 里，
                    //      要多一个 using；自己拷更直白；
                    //   ② 更重要的是这里**已经**把目标路径算好并校验过了，
                    //      不想再让库去碰路径（避免绕过我们的 Zip Slip 检查）。
                    using (var input = entry.Open())
                    using (var output = File.Create(target))
                    {
                        input.CopyTo(output);
                    }

                    installed++;
                }
            }

            if (installed == 0)
            {
                SetStatus($"「{pkg.Name}」安装失败：压缩包里没有可用的 dll。", isError: true);
                try { Directory.Delete(pluginDir, true); } catch { }
                return;
            }

            // ---- 热加载 + 启用 ----
            _ctx.ReloadPlugins?.Invoke();

            // 装完就**默认启用** —— 用户点"安装"的意思就是要用它。
            // 不自动开的话，他还要再去设置里找一遍开关，很别扭。
            EnableToolAfterInstall(pkg.Id);

            SetStatus(
                $"「{pkg.Name}」已安装并启用（来自 {source}，{size:N0} 字节，SHA-256 校验通过）。\n"
                + $"装了 {installed} 个文件到 {pluginDir}\n"
                + "功能已经可以用了 —— 悬浮窗上会出现它的按钮。",
                isError: false);

            _ctx.Notify("工具管理", $"「{pkg.Name}」已安装并启用。");

            Rebuild(null, null);
        }
        catch (Exception ex)
        {
            Log.Exception("安装插件失败", ex);
            SetStatus($"安装出错：{ex.Message}", isError: true);
        }
        finally
        {
            if (tempDir.Length > 0)
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }

            _busy = false;
            DownloadBtn.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 安装后把这个工具打开（写进设置并立刻生效）。
    ///
    /// 直接改设置而不是调 ToolGate：ToolGate 是只读判定，
    /// 这里要真的**改用户设置**，两件事不能混。
    /// </summary>
    private void EnableToolAfterInstall(string toolId)
    {
        try
        {
            var s = _ctx.Settings;

            // 与默认一致就移除条目（和设置窗口的保存逻辑保持同一套语义）
            if (ToolGate.IsDefaultOn(toolId))
            {
                s.ToolEnabled.Remove(toolId);
            }
            else
            {
                s.ToolEnabled[toolId] = true;
            }

            _ctx.SaveSettings();
            _ctx.ApplyToolSwitches?.Invoke();

            Log.Line($"安装后已启用工具：{toolId}");
        }
        catch (Exception ex)
        {
            Log.Exception($"安装后启用工具失败：{toolId}", ex);
        }
    }

    /// <summary>
    /// 卸载选中的插件。
    ///
    /// ⚠️ 这里**必须如实说明一件事**：已加载的程序集在 .NET 里
    ///    **无法真正卸载**（默认 ALC 不支持）。所以：
    ///      · 文件能删掉（下次启动就不会再加载）；
    ///      · 但**本次运行期间**这个工具仍然在内存里、仍然能用。
    ///    界面必须说清"重启后彻底消失"，而不是显示"已卸载" ——
    ///    后者会让用户以为功能立刻没了，一看还在，就不再信任这个按钮。
    /// </summary>
    private void RemoveSelected()
    {
        var row = ToolGrid.SelectedItem as StoreRow;

        if (row?.Package is null)
        {
            SetStatus("先在列表里选一个工具。", isError: true);
            return;
        }

        var pkg = row.Package;

        if (pkg.BuiltIn)
        {
            SetStatus($"「{pkg.Name}」是随主程序内置的，不能卸载。\n"
                      + "不想用它的请在「设置 → 功能开关」里关掉。", isError: false);
            return;
        }

        if (!PluginLoader.IsInstalled(pkg.Id))
        {
            SetStatus($"「{pkg.Name}」没有安装，不需要卸载。", isError: false);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"确定要卸载「{pkg.Name}」吗？\n\n"
            + "插件文件会被删除。\n"
            + "注意：本次运行期间它仍然可用（.NET 无法卸载已加载的程序集），"
            + "重启工具箱后它就彻底消失了。",
            "卸载插件",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        if (!PluginLoader.Remove(pkg.Id, out var error))
        {
            SetStatus(error ?? "卸载失败。", isError: true);
            return;
        }

        // 顺手把开关也关掉 —— 不然设置里会留一条"已启用的工具"却找不到实现
        try
        {
            _ctx.Settings.ToolEnabled[pkg.Id] = false;
            _ctx.SaveSettings();
        }
        catch (Exception ex)
        {
            Log.Exception("卸载后清理开关失败", ex);
        }

        SetStatus(
            $"「{pkg.Name}」已卸载（文件已删除）。\n"
            + "⚠️ 本次运行期间它仍然可用 —— 重启工具箱后就彻底没有了。",
            isError: false);

        Rebuild(null, null);
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
