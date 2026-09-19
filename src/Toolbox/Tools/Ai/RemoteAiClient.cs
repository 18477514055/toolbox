using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Toolbox.Core;

namespace Toolbox.Tools.Ai;

/// <summary>
/// 远端 AI 后端：任何 **OpenAI 兼容** 的 /v1/chat/completions 接口。
///
/// 为什么要支持它（这是"分享工具箱"的关键一步）：
///   本地 Ollama 要求对方装软件 + 拉 4.7 GB 模型，门槛太高。
///   而 OpenAI 兼容接口现在几乎是行业事实标准 —— OpenAI、DeepSeek、月之暗面、
///   智谱、通义、各类自建网关（One-API / vLLM / LM Studio / Ollama 自己）都提供。
///   用户填个地址 + Key 就能用，工具箱本身零安装。
///
/// 数据流向要跟用户说清楚：用这个后端时，待处理的文本会**发到那台服务器**，
/// 不再"数据不出这台机器"。面板上会明确标出来，不让用户在不知情的情况下上传。
/// </summary>
internal sealed class RemoteAiClient : IAiClient
{
    /// <summary>
    /// 复用一个静态 HttpClient（不是每次 new）。
    /// HttpClient 每次 new 都会新建连接池，短时间内多次请求会耗尽 socket（TIME_WAIT 堆积）；
    /// 而翻译长文会分块连续请求很多次，这个坑一定会踩到。
    /// </summary>
    private static readonly HttpClient Http = new()
    {
        // 远端不像本地模型那样"首 token 必然慢"，但网络故障可能永久挂住。
        // 所以这里给**有界**超时，而不是 Ollama 那种无限等。
        Timeout = TimeSpan.FromMinutes(5),
    };

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;

    public RemoteAiClient(string baseUrl, string apiKey, string model)
    {
        _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        _apiKey = (apiKey ?? "").Trim();
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model.Trim();
    }

    public string DisplayName => $"远端 API · {_model}";

    public string NotRunningMessage =>
        $"连不上远端 AI 服务（{_baseUrl}）。请检查：\n"
        + "① 网络是否通；② 地址填对了吗（填到 /v1 为止，例如 https://api.deepseek.com/v1）；\n"
        + "③ API Key 是否有效、余额是否够。";

    public async Task<(bool Ok, string Message, List<string> Models)> CheckAsync(CancellationToken ct)
    {
        if (_baseUrl.Length == 0)
        {
            return (false,
                "还没填远端 API 地址。去「设置 → 快捷 AI」里填上，或者切回本地 Ollama。",
                new List<string>());
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint("models"));
            ApplyAuth(req);

            using var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                return (false,
                    $"远端服务返回 {(int)resp.StatusCode}（{resp.ReasonPhrase}）：{Brief(body)}",
                    new List<string>());
            }

            var names = ParseModelNames(body);

            // ★ 连上了还要看**用户填的模型名在不在列表里**，否则是"假绿灯"。
            //
            //   为什么（实测抓到的）：用户设置里填的是 `Qwen3-8B`，
            //   服务真实存在的名字是 `Qwen/Qwen3-8B`（少了组织前缀）。
            //   原来的实现报"连接正常，远端提供 95 个模型（当前使用 Qwen3-8B）"
            //   —— 全绿；可真去用会得到一句 `400 错误的请求`，
            //   而那句话完全看不出是模型名写错了。
            //
            // ⚠️ 但这里**不能返回 Ok=false**。
            //   面板把 Ok=false 当硬拦截（AiPanel 里 !ok 直接 return，AI 就彻底不能用了），
            //   而有些网关的 /models 只返回**部分**模型（按 key 权限裁剪、分页、
            //   或干脆没实现）—— 那种情况下用户填的名字其实可用，却被我拦死。
            //   **假红灯比假绿灯更糟：它把本来能用的功能整个禁掉了。**
            //
            //   所以 Ok 仍表示"连得上"，可疑之处写进消息（⚠ 前缀），
            //   由界面据此显示成黄色而不是绿色。
            if (names.Count > 0 && !names.Any(n =>
                    string.Equals(n, _model, StringComparison.OrdinalIgnoreCase)))
            {
                var suggestions = ModelDiscovery.SuggestFor(
                    names.Select(n => new ModelInfo(n, null)).ToList(), _model);

                var message = suggestions.Count > 0
                    ? $"⚠ 连得上，但模型名「{_model}」不在这个服务返回的 {names.Count} 个模型里，"
                      + $"真去用多半会报错。\n   是不是想填：{string.Join("、", suggestions)}\n"
                      + "   （点「查找模型」可以直接从列表里选，不用手敲。）"
                    : $"⚠ 连得上，但模型名「{_model}」不在这个服务返回的 {names.Count} 个模型里，"
                      + "真去用多半会报错。点「查找模型」从列表里挑一个。\n"
                      + "   （也可能是这个服务没把全部模型列出来 —— 那就无需改动。）";

                return (true, message, names);
            }

            var models = names;
            var msg = models.Count > 0
                ? $"连接正常，远端提供 {models.Count} 个模型，模型名「{_model}」确认存在。"
                : $"连接正常（远端没返回模型清单，仍会尝试用 {_model}）。";

            return (true, msg, models);
        }
        catch (OperationCanceledException)
        {
            // 两种情况都归到"探活没通"，只是措辞不同：
            //   调用方给探活套了短超时（设置窗口 / 面板），到点就掐 —— 这是绝大多数情况；
            //   剩下的是 HttpClient 自己那 5 分钟到了，说明对方连 TCP 都没接上。
            return (false,
                ct.IsCancellationRequested
                    ? "探活超时：网络不通，或者地址填错了。"
                    : "探活超时：远端服务 5 分钟没有响应。",
                new List<string>());
        }
        catch (Exception ex)
        {
            return (false, $"{NotRunningMessage}\n\n（{ex.Message}）", new List<string>());
        }
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string prompt,
        string? systemPrompt,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<Dictionary<string, string>>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new Dictionary<string, string>
            {
                ["role"] = "system",
                ["content"] = systemPrompt!,
            });
        }

        messages.Add(new Dictionary<string, string>
        {
            ["role"] = "user",
            ["content"] = prompt,
        });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = messages,
            ["stream"] = true,
            // 翻译/总结不需要发散，温度压低更稳
            ["temperature"] = 0.2,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint("chat/completions"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), new UTF8Encoding(false), "application/json"),
        };
        ApplyAuth(req);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"远端服务返回 {(int)resp.StatusCode}：{Brief(err)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break; // 流结束
            }

            // SSE：只关心 data: 行，其余（event: / 空行 / 注释）跳过
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0)
            {
                continue;
            }

            if (data == "[DONE]")
            {
                yield break;
            }

            var piece = ExtractDelta(data);
            if (!string.IsNullOrEmpty(piece))
            {
                yield return piece;
            }
        }
    }

    /// <summary>
    /// 把用户填的地址补成完整的 OpenAI 端点。
    /// 填 "https://api.deepseek.com" 或 "https://api.deepseek.com/v1" 都能用 ——
    /// 让用户去记"要不要带 /v1"是没必要的负担。
    /// </summary>
    internal string Endpoint(string path)
    {
        var b = _baseUrl;
        if (!b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            b += "/v1";
        }

        return $"{b}/{path}";
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        if (_apiKey.Length > 0)
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        }
    }

    /// <summary>从一条 SSE 的 JSON 里取增量文本。取不到就返回 null（不抛）。</summary>
    private static string? ExtractDelta(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            if (!choices[0].TryGetProperty("delta", out var delta))
            {
                return null;
            }

            return delta.TryGetProperty("content", out var content) ? content.GetString() : null;
        }
        catch
        {
            // 半行 / 非预期结构：跳过这一条，不要因为一行坏数据把整段翻译打断
            return null;
        }
    }

    private static List<string> ParseModelNames(string json)
    {
        var list = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return list;
            }

            foreach (var m in data.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
                {
                    list.Add(s);
                }
            }
        }
        catch
        {
            // 清单解析失败不影响主流程
        }

        return list;
    }

    private static string Brief(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
