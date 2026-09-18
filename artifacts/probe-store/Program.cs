using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Toolbox.Tools.Store;

// ============================================================================
// 实测"按需下载"这条链路真的能走通：
//   ① 清单能从 GitHub 拉下来（含多个镜像的自动降级）
//   ② Release 附件能下载并通过 SHA-256 校验
//   ③ 哈希**故意写错**时必须被拒绝（证明校验真的在工作，不是摆设）
// ============================================================================

Console.WriteLine("=== 工具仓库链路实测 ===");
Console.WriteLine();

var temp = Path.Combine(Path.GetTempPath(), "toolstore-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);

try
{
    // ---- ① 内置清单自检 ----
    Console.WriteLine("① 内置清单（离线可用的那份）：");
    var builtin = ToolStore.BuiltInManifest();
    Console.WriteLine($"   SchemaVersion = {builtin.SchemaVersion}");
    Console.WriteLine($"   条目数        = {builtin.Tools.Count}");
    var dup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var dupFound = new List<string>();
    foreach (var t in builtin.Tools)
    {
        if (!dup.Add(t.Id)) { dupFound.Add(t.Id); }
    }
    Console.WriteLine($"   Id 重复       = {(dupFound.Count == 0 ? "无" : string.Join(",", dupFound))}");
    Console.WriteLine($"   内置条目      = {builtin.Tools.Count(t => t.BuiltIn)} / {builtin.Tools.Count}");
    Console.WriteLine();

    // ---- ② 从网络拉清单（多镜像降级）----
    Console.WriteLine("② 从仓库拉清单（会依次试各个镜像）：");
    var (manifest, source, error) = await ToolStore.FetchManifestAsync(CancellationToken.None);

    if (manifest is null)
    {
        Console.WriteLine($"   ❌ 全都失败：{error}");
        Console.WriteLine();
        Console.WriteLine("   （说明：仓库里还没有 tools.json —— 这是预期的，稍后会生成并提交）");
    }
    else
    {
        Console.WriteLine($"   ✅ 成功，来自：{source}");
        Console.WriteLine($"   条目数 = {manifest.Tools.Count}");
    }

    Console.WriteLine();

    // ---- ③ 下载 Release 附件并校验 ----
    Console.WriteLine("③ 下载 Release 附件（真实文件，走多镜像 + 哈希校验）：");

    // 先用一个已知真实存在的资产
    var assetName = "Toolbox-v1.0.zip";
    var tag = "v1.0";
    var savePath = Path.Combine(temp, assetName);

    // 先拿到官方给的 sha256（从 GitHub API 读，作为"发布者给的哈希"）
    using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) })
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Toolbox/1.0");

        var json = await http.GetStringAsync(
            "https://api.github.com/repos/18477514055/toolbox/releases/tags/v1.0");

        // 简单提取 digest 字段（避免引 Json 依赖）
        var idx = json.IndexOf("\"digest\":\"sha256:", StringComparison.Ordinal);
        string officialSha = "";

        if (idx > 0)
        {
            var start = idx + "\"digest\":\"sha256:".Length;
            var end = json.IndexOf('"', start);
            if (end > start)
            {
                officialSha = json.Substring(start, end - start).ToUpperInvariant();
            }
        }

        Console.WriteLine($"   GitHub 公布的 sha256 = {(officialSha.Length > 0 ? officialSha[..16] + "…" : "(没读到)")}");
        Console.WriteLine();

        if (officialSha.Length == 0)
        {
            Console.WriteLine("   读不到官方哈希，跳过下载测试");
        }
        else
        {
            // ③a 正确哈希 → 应该成功
            var progressCount = 0;
            var progress = new Progress<double>(_ => progressCount++);

            var t0 = DateTime.Now;
            var (ok, saved, usedSource, err) = await ToolStore.DownloadAsync(
                "", assetName, tag, officialSha, savePath, progress, CancellationToken.None);
            var elapsed = DateTime.Now - t0;

            if (ok)
            {
                var size = new FileInfo(saved).Length;
                var actual = ToolStore.ComputeSha256(saved);
                Console.WriteLine($"   ✅ 下载成功");
                Console.WriteLine($"      来源   = {usedSource}");
                Console.WriteLine($"      大小   = {size:N0} 字节");
                Console.WriteLine($"      耗时   = {elapsed.TotalSeconds:F1} 秒");
                Console.WriteLine($"      进度回调 = {progressCount} 次");
                Console.WriteLine($"      哈希复核 = {actual[..16]}… {(actual == officialSha ? "✅ 一致" : "❌ 不一致")}");
            }
            else
            {
                Console.WriteLine($"   ❌ 下载失败：{err}");
            }

            // ③b 故意给错的哈希 → 必须被拒绝
            Console.WriteLine();
            Console.WriteLine("   ⚠️ 变异测试：故意给一个错的哈希，必须被拒绝：");

            var badPath = Path.Combine(temp, "should-not-exist.zip");
            var wrongSha = new string('0', 64);

            var (ok2, saved2, src2, err2) = await ToolStore.DownloadAsync(
                "", assetName, tag, wrongSha, badPath, null, CancellationToken.None);

            var rejected = !ok2 && !File.Exists(badPath);
            Console.WriteLine($"      {(rejected ? "✅ 正确拒绝（且没留下垃圾文件）" : "❌ 竟然接受了！")}");
            if (!rejected)
            {
                Console.WriteLine($"      ok={ok2} 文件存在={File.Exists(badPath)}");
            }
        }
    }
}
finally
{
    try { Directory.Delete(temp, true); } catch { }
}

return 0;
