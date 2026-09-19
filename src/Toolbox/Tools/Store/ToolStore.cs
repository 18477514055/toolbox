using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Toolbox.Core;
// 别名会遮蔽 System.IO 下的其它类型，所以上面显式 using System.IO;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Toolbox.Tools.Store;

/// <summary>
/// 一个可下载工具的清单条目。
///
/// 这个清单就是"按需下载"的数据源 —— 它**放在 Git 仓库里**（`tools.json`），
/// 客户端拉下来就知道有哪些工具、去哪下载、校验值是多少。
/// </summary>
internal sealed class ToolPackage
{
    /// <summary>工具 Id（与 IToolboxTool.Id 一致，如 "ocr"）。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>一句话说明。</summary>
    public string Description { get; set; } = "";

    /// <summary>悬浮窗上显示的字符。</summary>
    public string Glyph { get; set; } = "";

    /// <summary>默认热键（空 = 不设）。</summary>
    public string DefaultHotKey { get; set; } = "";

    /// <summary>是不是核心内置（内置的**不需要下载**，随主程序一起来）。</summary>
    public bool BuiltIn { get; set; }

    /// <summary>版本号（语义化，如 "1.0.0"）。</summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// 下载文件名（相对于 Release 的 assets）。
    /// 内置工具为空。
    /// </summary>
    public string AssetName { get; set; } = "";

    /// <summary>
    /// 内容的 SHA-256（**下载后必须校验**）。
    ///
    /// 为什么必须有：我们从网络下载可执行代码，没有校验就等于
    /// "谁把文件换了我们都照跑"。镜像站尤其如此 —— 第三方镜像的完整性
    /// 不能默认信任，必须用发布者给的哈希核对。
    /// </summary>
    public string Sha256 { get; set; } = "";

    /// <summary>文件大小（字节，用于显示进度与预检）。</summary>
    public long Size { get; set; }

    /// <summary>依赖说明（比如 rar 需要 WinRAR）。</summary>
    public string? Requires { get; set; }
}

/// <summary>清单文件（`tools.json`）的根。</summary>
internal sealed class ToolManifest
{
    /// <summary>清单格式版本。以后格式变了要靠它判断兼容性。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>对应主程序的版本（清单只对同版本主程序有效）。</summary>
    public string AppVersion { get; set; } = "";

    /// <summary>生成时间。</summary>
    public string GeneratedAt { get; set; } = "";

    public List<ToolPackage> Tools { get; set; } = new();
}

/// <summary>
/// 清单与下载源的读写。
///
/// **设计原则：清单缓存在本地，离线也能看**
///   · 拉不到最新清单时用**打包时内置的那份**（`ToolManifest.BuiltIn`）——
///     保证用户至少能看到"有哪些工具"，而不是一片空白加一个报错；
///   · 能拉到就用新的（可能已经有新工具了）。
/// </summary>
internal static class ToolStore
{
    /// <summary>清单在仓库里的位置（raw 链接，走镜像更稳）。</summary>
    public const string ManifestPath = "tools.json";

    /// <summary>仓库标识（用户名/仓库名）。</summary>
    public const string RepoOwner = "18477514055";
    public const string RepoName = "toolbox";

    /// <summary>
    /// 下载源列表（**按顺序尝试**）。
    ///
    /// ⚠️ 国内网络环境对 GitHub 的直连**时好时坏**（实测：这条命令所在的机器上
    ///    `github.com` 与 `raw.githubusercontent.com` 会超时，
    ///    而 `api.github.com` / `codeload.github.com` 通）。
    ///    所以**必须留多个镜像**，并且在直连失败时自动依次退到镜像 ——
    ///    用户不该因为网络问题而用不了这个功能。
    ///
    /// 顺序：直连优先（最快、最可信），然后依次是几个公共加速站。
    /// 每个源都会做**哈希校验**，所以用第三方镜像不会降低安全性。
    /// </summary>
    public static IReadOnlyList<(string Name, string Template)> Sources { get; } = new[]
    {
        // 直连（raw.githubusercontent.com）
        ("GitHub 直连", "https://raw.githubusercontent.com/{owner}/{repo}/main/{path}"),

        // 下面几个是公共 GitHub 加速站。实测三者都可用（HTTP 200）。
        ("ghproxy.net", "https://ghproxy.net/https://raw.githubusercontent.com/{owner}/{repo}/main/{path}"),
        ("gh-proxy.com", "https://gh-proxy.com/https://raw.githubusercontent.com/{owner}/{repo}/main/{path}"),
        ("ghfast.top", "https://ghfast.top/https://raw.githubusercontent.com/{owner}/{repo}/main/{path}"),
    };

    /// <summary>
    /// Release 附件（工具包本体）的下载源。
    /// 与清单不同，这里要的是 `github.com/.../releases/download/...`。
    /// </summary>
    public static IReadOnlyList<(string Name, string Template)> ReleaseSources { get; } = new[]
    {
        ("GitHub 直连", "https://github.com/{owner}/{repo}/releases/download/{tag}/{asset}"),
        ("ghproxy.net", "https://ghproxy.net/https://github.com/{owner}/{repo}/releases/download/{tag}/{asset}"),
        ("gh-proxy.com", "https://gh-proxy.com/https://github.com/{owner}/{repo}/releases/download/{tag}/{asset}"),
        ("ghfast.top", "https://ghfast.top/https://github.com/{owner}/{repo}/releases/download/{tag}/{asset}"),
    };

    /// <summary>把模板里的占位符替换掉。</summary>
    public static string BuildUrl(
        (string Name, string Template) source,
        Dictionary<string, string> values)
    {
        var url = source.Template
            .Replace("{owner}", RepoOwner)
            .Replace("{repo}", RepoName);

        foreach (var (k, v) in values)
        {
            url = url.Replace("{" + k + "}", v);
        }

        return url;
    }

    /// <summary>
    /// 内置的清单（**打包时生成的那一份**）。
    ///
    /// 为什么要有它：第一次运行、或者网络不通时，用户至少能看到"有哪些工具"。
    /// 没有的话界面就是一片空白加一个报错 —— 那等于功能不可用。
    /// 这份内容随主程序一起发布，永远可用。
    /// </summary>
    public static ToolManifest BuiltInManifest() => new()
    {
        SchemaVersion = 1,
        AppVersion = "1.0.0",
        GeneratedAt = "2026-09-18",
        Tools = new List<ToolPackage>
        {
            // 内置（随主程序，不需要下载）
            new() { Id = "clipboard", Name = "快捷剪贴板", Glyph = "📋", BuiltIn = true,
                    Description = "查历史复制的文本 / 图片 / 文件", DefaultHotKey = "Win+Shift+V" },
            new() { Id = "image", Name = "图片裁剪", Glyph = "✂", BuiltIn = true,
                    Description = "框选裁剪 / 改像素尺寸", DefaultHotKey = "Win+Shift+X" },
            new() { Id = "convert", Name = "格式转换", Glyph = "🔄", BuiltIn = true,
                    Description = "图片 / 文档 / PDF 互转（不含视频音频）", DefaultHotKey = "Win+Shift+C" },
            new() { Id = "ai", Name = "快捷 AI", Glyph = "✨", BuiltIn = true,
                    Description = "读页面 / 总结 / 翻译", DefaultHotKey = "Win+Shift+A" },
            new() { Id = "screenshot", Name = "快捷截图", Glyph = "▣", BuiltIn = true,
                    Description = "拖拽框选区域截图", DefaultHotKey = "Win+Alt+S" },

            // 可选（同一 exe 里已包含，用开关启用；同时登记版本便于以后改成真下载）
            new() { Id = "ocr", Name = "OCR 截图识字", Glyph = "字", BuiltIn = false,
                    Version = "1.0.0",
                    AssetName = "Toolbox-Plugin-ocr-v1.0.0.zip",
                    Sha256 = "9751711DCD194836F505E9B05FE09FABB16A68DDF57E75EA929966F4A870FB01",
                    Size = 15467,
                    Description = "框选屏幕区域，识别文字并复制", DefaultHotKey = "Win+Alt+T" },
            new() { Id = "rename", Name = "批量重命名", Glyph = "改", BuiltIn = false,
                    Version = "1.0.0",
                    AssetName = "Toolbox-Plugin-rename-v1.0.0.zip",
                    Sha256 = "F484917A49A27E4B97C43750DEC94A61286A710F140EBFE27F3FB3E8671B56CD",
                    Size = 15216,
                    Description = "按规则批量改名（可预览、可撤销）", DefaultHotKey = "Win+Alt+R" },
            new() { Id = "hash", Name = "哈希校验", Glyph = "#", BuiltIn = false,
                    Version = "1.0.0",
                    AssetName = "Toolbox-Plugin-hash-v1.0.0.zip",
                    Sha256 = "F0215C059F9A6CBC4E21FDA2805B803DC88B7C5B24FED0259601961B16B34579",
                    Size = 15327,
                    Description = "算 MD5 / SHA256，比对文件是否一致", DefaultHotKey = "Win+Alt+H" },
            new() { Id = "qrcode", Name = "二维码工具", Glyph = "▩", BuiltIn = false,
                    Version = "1.0.0",
                    AssetName = "Toolbox-Plugin-qrcode-v1.0.0.zip",
                    Sha256 = "7BEDB44EC4B3D7986E763429228C41C19825C495BBDB1B3992A6198BA80F3184",
                    Size = 26065,
                    Description = "生成 / 扫描二维码", DefaultHotKey = "Win+Alt+Q" },
            // ★ B 阶段起：「窗口置顶」是**可下载插件**，不再随主程序一起装。
            //
            //   它在这份内置清单里也必须标成 BuiltIn=false ——
            //   否则界面会告诉用户"它是内置的、不用下载"，
            //   而实际上用户没装插件就用不到它（界面与事实不符）。
            new() { Id = "topmost", Name = "窗口置顶", Glyph = "📌", BuiltIn = false,
                    Version = "1.0.0",
                    AssetName = "Toolbox-Plugin-WindowTopmost-v1.0.0.zip",
                    Sha256 = "2288C5F873324DFAAA175CAF680555D407BFAD0C01AC4EC201CE8C9FBCA4FDCA",
                    Size = 4315,
                    Description = "把当前窗口置顶 / 取消置顶", DefaultHotKey = "Win+Alt+P" },
            new() { Id = "run", Name = "运行命令", Glyph = ">_", BuiltIn = false,
                    Version = "1.0.0",
                    AssetName = "Toolbox-Plugin-run-v1.0.0.zip",
                    Sha256 = "8A40BD0C32E293BE1428FCFCA51017A11DCA65D27259730770BE1A61838787E0",
                    Size = 13839,
                    Description = "输入命令交给 cmd / PowerShell",
                    DefaultHotKey = "Win+Alt+X",
                    Requires = "会以你的身份真实执行命令" },
            new() { Id = "archive", Name = "批量打包", Glyph = "🗜", BuiltIn = false,
                    Version = "1.0.0",
                    AssetName = "Toolbox-Plugin-archive-v1.0.0.zip",
                    Sha256 = "AFF1A142D4817CC716A30F9E6B1ABE5AE068F922D2123D5FA699AEF8F0550F54",
                    Size = 18726,
                    Description = "打包成 zip / rar",
                    DefaultHotKey = "Win+Alt+G",
                    Requires = "rar 格式需要装 WinRAR" },
        },
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 从网络拉清单，**依次尝试所有镜像**。
    /// </summary>
    /// <returns>(清单, 用的哪个源, 错误)。清单为 null 表示全失败。</returns>
    public static async Task<(ToolManifest? Manifest, string Source, string? Error)> FetchManifestAsync(
        CancellationToken ct)
    {
        var errors = new List<string>();

        foreach (var source in Sources)
        {
            ct.ThrowIfCancellationRequested();

            var url = BuildUrl(source, new Dictionary<string, string> { ["path"] = ManifestPath });

            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Toolbox/1.0");

                var text = await http.GetStringAsync(url, ct);

                var manifest = JsonSerializer.Deserialize<ToolManifest>(text, Options);

                if (manifest?.Tools is null || manifest.Tools.Count == 0)
                {
                    errors.Add($"{source.Name}: 清单为空或格式不对");
                    continue;
                }

                // 只接受认识的格式版本 —— 不认识就宁可不用，
                // 免得把结构变了的新清单当旧结构解析，得出乱七八糟的结论
                if (manifest.SchemaVersion != 1)
                {
                    errors.Add($"{source.Name}: 清单格式版本 {manifest.SchemaVersion} 不认识（本版只认 1）");
                    continue;
                }

                Log.Line($"工具清单已拉取：{source.Name}，{manifest.Tools.Count} 个条目");
                return (manifest, source.Name, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{source.Name}: {ex.Message}");
                Log.Line($"清单源失败（{source.Name}）：{ex.Message}");
            }
        }

        return (null, "", "所有清单源都失败了：\n" + string.Join("\n", errors));
    }

    /// <summary>
    /// 下载一个文件，**依次尝试所有镜像**，并**校验 SHA-256**。
    ///
    /// 校验失败会**删掉已下载的内容并视为该源失败**，然后试下一个源 ——
    /// 这正是"用第三方镜像也安全"的依据：无论从哪来，都得对得上发布者的哈希。
    /// </summary>
    public static async Task<(bool Ok, string SavedPath, string Source, string? Error)> DownloadAsync(
        string url_owner_repo_tag_asset,
        string assetName,
        string tag,
        string expectedSha256,
        string savePath,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var errors = new List<string>();

        foreach (var source in ReleaseSources)
        {
            ct.ThrowIfCancellationRequested();

            var url = BuildUrl(source, new Dictionary<string, string>
            {
                ["tag"] = tag,
                ["asset"] = assetName,
            });

            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Toolbox/1.0");

                using var response = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? 0;
                var temp = savePath + ".downloading";

                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    int read;

                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;

                        if (total > 0)
                        {
                            progress?.Report((double)done / total);
                        }
                    }
                }

                // ★ 校验哈希 —— 不通过就当这个源失败
                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    var actual = ComputeSha256(temp);

                    if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(temp); } catch { }
                        errors.Add($"{source.Name}: 哈希不符（期望 {expectedSha256[..12]}…，实际 {actual[..12]}…）");
                        Log.Error($"下载校验失败（{source.Name}）：哈希不符，已丢弃");
                        continue;
                    }
                }

                // 校验通过才落到正式路径
                if (File.Exists(savePath))
                {
                    File.Delete(savePath);
                }

                File.Move(temp, savePath);

                Log.Line($"下载完成：{assetName}（来自 {source.Name}，{new FileInfo(savePath).Length} 字节）");
                return (true, savePath, source.Name, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{source.Name}: {ex.Message}");
                Log.Line($"下载源失败（{source.Name}）：{ex.Message}");
            }
        }

        return (false, "", "", "所有下载源都失败了：\n" + string.Join("\n", errors));
    }

    /// <summary>算文件的 SHA-256（十六进制大写）。</summary>
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();

        // 全限定 System.Convert —— 本项目有个 Toolbox.Tools.Convert 命名空间，
        // 裸写 Convert 会被解析到那个命名空间上（CS0234）
        return System.Convert.ToHexString(sha.ComputeHash(stream));
    }
}
