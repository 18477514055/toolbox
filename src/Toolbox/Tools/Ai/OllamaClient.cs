using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Toolbox.Core;

namespace Toolbox.Tools.Ai;

/// <summary>
/// 本地 Ollama 的极简客户端。零第三方依赖：HttpClient + System.Text.Json 就够了。
///
/// 三条来自交接文档的要求：
///   - **base url 和模型名不硬编码**（设置项，以后换模型/换云 API 不用改代码）；
///   - **必须流式**：7B 模型在核显上首 token 要几秒到十几秒，不流式用户会以为卡死；
///   - **Ollama 没启动时要给明确提示**，不是让用户对着超时发呆。
/// </summary>
internal sealed class OllamaClient : IAiClient
{
    /// <summary>
    /// 全进程共用一个 HttpClient。
    ///
    /// 原来是每个实例 new 一个 —— 表面上看不出问题，但 HttpClient 每次 new 都自带一个
    /// 连接池，而且 Dispose 不会立刻关闭底层 socket（会进入 TIME_WAIT）。翻译长文要按块
    /// 连续发几十个请求，每个请求都建一个客户端，socket 会被迅速耗干，表现成"翻到一半
    /// 突然连不上"。静态复用是微软文档里明确的写法。
    ///
    /// 超时设成无限：7B 模型在核显上首 token 要等十几秒，用固定超时会误杀正常请求。
    /// 真正的"该放弃"由调用方的 CancellationToken 决定（探活那一处必须自己带超时）。
    /// </summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    public OllamaClient(string baseUrl, string model)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Model = model;
    }

    public string BaseUrl { get; }

    public string Model { get; }

    /// <summary>Ollama 没启动 / 地址不对时的统一中文提示（静态形式，给不需要实例的地方用）。</summary>
    public const string NotRunningMessageText =
        "连不上本地 Ollama。请先启动 Ollama（托盘里的羊驼图标），再重试。";

    /// <summary>接口要求的实例形式。</summary>
    public string NotRunningMessage => NotRunningMessageText;

    public string DisplayName => $"本地 Ollama · {Model}";

    /// <summary>探活 + 取模型清单。失败时返回明确的中文原因。</summary>
    public async Task<(bool Ok, string Message, List<string> Models)> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync($"{BaseUrl}/api/tags", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Ollama 返回 {(int)response.StatusCode}，地址可能是错的：{BaseUrl}", new List<string>());
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var models = ParseModelNames(json);

            var hasTarget = models.Any(m => m.Equals(Model, StringComparison.OrdinalIgnoreCase)
                                         || m.StartsWith(Model + ":", StringComparison.OrdinalIgnoreCase));

            if (models.Count == 0)
            {
                return (false, "Ollama 在跑，但一个模型都没有。请先 `ollama pull " + Model + "`。", models);
            }

            if (!hasTarget)
            {
                return (false,
                    $"Ollama 在跑，但没找到模型「{Model}」。现有：{string.Join("、", models)}",
                    models);
            }

            return (true, $"连接正常，模型「{Model}」已就位。", models);
        }
        catch (TaskCanceledException)
        {
            return (false, "连接 Ollama 超时。", new List<string>());
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
        {
            return (false, NotRunningMessage, new List<string>());
        }
        catch (Exception ex)
        {
            return (false, $"连接 Ollama 失败：{ex.Message}", new List<string>());
        }
    }

    private static List<string> ParseModelNames(string json)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models))
            {
                return list;
            }

            foreach (var m in models.EnumerateArray())
            {
                if (m.TryGetProperty("name", out var name) && name.GetString() is { } s)
                {
                    list.Add(s);
                }
            }
        }
        catch
        {
            // 解析失败就当没有模型，交给上层给提示
        }

        return list;
    }

    /// <summary>
    /// 流式生成。每产出一小段就 yield 一次，界面据此实时显示。
    /// 用 stream:true —— 这是"用户知道它还在干活"的唯一可靠手段。
    /// </summary>
    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string prompt,
        string? systemPrompt,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["prompt"] = prompt,
            ["stream"] = true,
            ["options"] = new Dictionary<string, object?>
            {
                // 翻译/总结不需要发散，温度压低更稳
                ["temperature"] = 0.2,
            },
        };

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            payload["system"] = systemPrompt;
        }

        var body = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/generate")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Ollama 返回 {(int)response.StatusCode}：{Trim(text)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? piece = null;
            var done = false;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("response", out var r))
                {
                    piece = r.GetString();
                }

                if (doc.RootElement.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True)
                {
                    done = true;
                }
            }
            catch (JsonException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(piece))
            {
                yield return piece;
            }

            if (done)
            {
                break;
            }
        }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
