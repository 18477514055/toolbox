using System.Net.Http;
using System.Text.Json;
using Toolbox.Core;

namespace Toolbox.Tools.Ai;

/// <summary>从 AI 后端查到的模型条目。</summary>
/// <param name="Id">模型名（远端返回的原始 id，原样保留）。</param>
/// <param name="OwnedBy">附加信息：远端是组织名，Ollama 是体积。可能为空。</param>
public sealed record ModelInfo(string Id, string? OwnedBy);

/// <summary>查模型的结果。</summary>
public sealed record ModelQueryResult(
    bool Ok,
    IReadOnlyList<ModelInfo> Models,
    string? Error,
    string? Hint)
{
    public static ModelQueryResult Fail(string error, string? hint = null)
        => new(false, Array.Empty<ModelInfo>(), error, hint);
}

/// <summary>
/// 查询 AI 后端有哪些可用模型 —— 不用用户手敲模型名。
///
/// ═══════════════════════════════════════════════════════════════════════
///  为什么值得做（用户要求）
/// ═══════════════════════════════════════════════════════════════════════
///
/// 现在的流程是：用户去服务商网站翻文档，找到模型名，
/// 再一个字一个字敲进设置里 —— 敲错一个字符就只会得到
/// "模型不存在"，而错误信息完全指不到"名字打错了"。
///
/// 有了"查一下"按钮：填好地址和 Key，点一下，下拉框里直接选。
///
/// ⚠️ 关键设计：**两种后端要用两个不同的接口**，不能混为一谈。
///
///   · **远端（OpenAI 兼容）** → `GET {base}/models`
///     这是 OpenAI 的约定（`GET /v1/models`）。绝大多数兼容服务都实现它：
///     DeepSeek / 硅基流动 / 智谱 / Moonshot / 阿里百炼 / OneAPI 网关……
///     ⚠️ 但有少数服务**没实现**（或需要特殊权限），所以失败时要给可操作的提示，
///        而不是一句"查询失败"。
///
///   · **本地 Ollama** → `GET {base}/api/tags`
///     这是 Ollama 自己的接口（不是 OpenAI 那套）。
///     顺带还能从返回里读出模型大小，显示出来帮用户判断"这个模型多大"。
///
/// 把两者分开写，是因为它们的**协议、路径、返回结构**全都不一样；
/// 用同一个方法硬套会写出一堆 if，而且很容易在某一侧出错。
/// </summary>
public static class ModelDiscovery
{
    /// <summary>复用一个 HttpClient（理由见 RemoteAiClient 的说明：连接池复用）。</summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>
    /// 查远端（OpenAI 兼容）可用模型。
    /// </summary>
    /// <param name="apiUrl">用户填的地址（可能带/不带 /v1，可能带 /chat/completions）。</param>
    /// <param name="apiKey">API Key。</param>
    public static async Task<ModelQueryResult> QueryRemoteAsync(
        string apiUrl,
        string apiKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            return ModelQueryResult.Fail("还没填 API 地址。");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ModelQueryResult.Fail("还没填 API Key。");
        }

        var url = BuildModelsUrl(apiUrl);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                return ClassifyHttpError((int)resp.StatusCode, body, url);
            }

            var models = ParseOpenAiModels(body);

            if (models.Count == 0)
            {
                return ModelQueryResult.Fail(
                    "接口通了，但返回里没有 model 列表。",
                    "这个服务可能没有实现 GET /models。可以手动填模型名 —— "
                    + "常见的如 deepseek-chat、qwen-plus、gpt-4o-mini。");
            }

            Log.Line($"远端模型查询成功：{models.Count} 个（{url}）");

            // 按名字排序，顺带把 embedding/whisper 这类非对话模型排后面
            return new ModelQueryResult(true, SortForChat(models), null, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ModelQueryResult.Fail(
                "查询超时（20 秒）。",
                "地址可能填错了，或者网络不通。检查一下能不能在浏览器里打开这个地址。");
        }
        catch (HttpRequestException ex)
        {
            Log.Exception("查询远端模型失败（网络）", ex);
            return ModelQueryResult.Fail(
                $"连不上：{ex.Message}",
                "检查地址是否正确（要能在外网访问），以及本机网络/代理是否正常。");
        }
        catch (Exception ex)
        {
            Log.Exception("查询远端模型失败", ex);
            return ModelQueryResult.Fail($"查询失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 查本地 Ollama 有哪些模型（`/api/tags`）。
    /// </summary>
    public static async Task<ModelQueryResult> QueryOllamaAsync(string baseUrl, CancellationToken ct)
    {
        var url = BuildOllamaTagsUrl(baseUrl);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                return ModelQueryResult.Fail(
                    $"Ollama 返回 {(int)resp.StatusCode}。",
                    "确认 Ollama 正在运行（托盘里应有它的图标）。");
            }

            var models = ParseOllamaModels(body);

            if (models.Count == 0)
            {
                return ModelQueryResult.Fail(
                    "Ollama 在运行，但一个模型都没有。",
                    "先用 `ollama pull qwen2.5:7b` 拉一个模型下来。");
            }

            Log.Line($"Ollama 模型查询成功：{models.Count} 个");
            return new ModelQueryResult(true, models, null, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ModelQueryResult.Fail("查询超时。", "确认 Ollama 正在运行。");
        }
        catch (HttpRequestException ex)
        {
            return ModelQueryResult.Fail(
                $"连不上 Ollama：{ex.Message}",
                "确认地址（默认 http://127.0.0.1:11434）以及 Ollama 是否已启动。");
        }
        catch (Exception ex)
        {
            Log.Exception("查询 Ollama 模型失败", ex);
            return ModelQueryResult.Fail($"查询失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把用户填的地址规范成 `…/models`。
    ///
    /// 要容错这几种写法（用户凭什么知道该填哪种）：
    ///   https://api.deepseek.com            → …/v1/models
    ///   https://api.deepseek.com/v1         → …/v1/models
    ///   https://api.deepseek.com/v1/        → …/v1/models
    ///   https://api.deepseek.com/v1/chat/completions → …/v1/models
    ///   https://xxx/v1/models               → 原样
    /// </summary>
    public static string BuildModelsUrl(string apiUrl)
    {
        var url = (apiUrl ?? "").Trim().TrimEnd('/');

        if (url.Length == 0)
        {
            return "";
        }

        // 已经带 /models 就别重复加
        if (url.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        // 用户很可能是把"/chat/completions"整条填进来了 —— 砍掉它
        const string chatSuffix = "/chat/completions";
        if (url.EndsWith(chatSuffix, StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^chatSuffix.Length].TrimEnd('/');
        }

        // 已经带 /v1（或别的版本号）就直接接 /models，否则补 /v1
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith("/v2", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith("/v3", StringComparison.OrdinalIgnoreCase))
        {
            return url + "/models";
        }

        return url + "/v1/models";
    }

    /// <summary>把 Ollama 地址规范成 `…/api/tags`。</summary>
    public static string BuildOllamaTagsUrl(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');

        if (url.Length == 0)
        {
            url = "http://127.0.0.1:11434";
        }

        // 用户可能填了 http://host:11434/api 或 /api/generate —— 剥掉
        var idx = url.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
        {
            url = url[..idx];
        }
        else if (url.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^4];
        }

        return url + "/api/tags";
    }

    /// <summary>
    /// 解析 OpenAI 兼容的 `/models` 返回：`{ "data": [ { "id": "...", "owned_by": "..." } ] }`
    /// </summary>
    public static List<ModelInfo> ParseOpenAiModels(string json)
    {
        var list = new List<ModelInfo>();

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idProp))
                {
                    continue;
                }

                var id = idProp.GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string? owner = null;
                if (item.TryGetProperty("owned_by", out var ownerProp))
                {
                    owner = ownerProp.GetString();
                }

                list.Add(new ModelInfo(id, owner));
            }
        }
        catch (JsonException ex)
        {
            Log.Exception("解析 /models 返回失败", ex);
        }

        return list;
    }

    /// <summary>
    /// 解析 Ollama 的 `/api/tags` 返回：`{ "models": [ { "name": "...", "size": 123 } ] }`
    /// </summary>
    public static List<ModelInfo> ParseOllamaModels(string json)
    {
        var list = new List<ModelInfo>();

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("models", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameProp))
                {
                    continue;
                }

                var name = nameProp.GetString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // 顺带读出体积，显示出来帮用户挑（本地模型动辄几 GB）
                string? note = null;
                if (item.TryGetProperty("size", out var sizeProp)
                    && sizeProp.TryGetInt64(out var size)
                    && size > 0)
                {
                    note = FormatSize(size);
                }

                list.Add(new ModelInfo(name, note));
            }
        }
        catch (JsonException ex)
        {
            Log.Exception("解析 /api/tags 返回失败", ex);
        }

        return list;
    }

    /// <summary>
    /// 排序：**尽量**把明显不能对话的模型排到后面。
    ///
    /// ⚠️ 这是**启发式**，不是判定 —— 它一定会漏。
    ///   第一版我只列了 embedding/whisper/tts 那几个词，
    ///   拿真实服务（硅基流动 95 个模型）一测就露馅了：
    ///   `BAAI/bge-large-zh-v1.5` 是向量模型，但名字里没有 "embedding"，
    ///   于是它被排到了**最前面**。
    ///
    ///   所以两点要记住：
    ///     ① 名单要按真实数据扩，不是凭印象列（下面这份就是实测后扩的）；
    ///     ② 界面**不许**说"前面的都能对话"，只能说"已把疑似不能对话的排后面" ——
    ///        把启发式包装成判定，是在给用户假的安全感。
    /// </summary>
    public static List<ModelInfo> SortForChat(IEnumerable<ModelInfo> models)
    {
        var list = models.ToList();

        return list
            .OrderBy(Rank)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 名字里出现这些词，基本可以断定**不是**拿来对话的。
    ///
    /// ⚠️ 只用于"排后面"，不用于"拦下来不让选" —— 判错了最多是排序不理想，
    ///    而拦下来会让用户用不了某些其实能用的模型。
    /// </summary>
    private static readonly string[] NonChatMarkers =
    {
        // 向量 / 重排
        "embedding", "embed", "rerank", "bge-", "bge_", "/bge", "mgte", "gte-", "bce-",
        "e5-", "jina-embed", "nomic-embed", "conan-embedding", "text2vec",

        // 语音识别 / 合成
        "whisper", "tts", "asr", "sensevoice", "cosyvoice", "captioner",

        // 图像 / 视频生成
        "image", "flux", "stable-diffusion", "dall-e", "kolora", "emo",
        "vidu", "svd", "animatediff", "sd3",

        // 其他专用任务
        "ocr", "moderation",
    };

    private static int Rank(ModelInfo m)
    {
        var id = m.Id.ToLowerInvariant();

        foreach (var marker in NonChatMarkers)
        {
            if (id.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
        }

        return 1;
    }

    /// <summary>
    /// 这个模型名能不能用来对话（启发式，只用于**提示**，不用于拦截）。
    /// </summary>
    public static bool LooksNonChat(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var id = modelId.ToLowerInvariant();

        return NonChatMarkers.Any(marker => id.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 用户填的模型名不在列表里时，猜几个他可能想填的。
    ///
    /// 为什么这是这个功能里最有价值的一环（实测）：
    ///   用户的设置里填的是 `Qwen3-8B`，
    ///   而硅基流动真实存在的名字是 `Qwen/Qwen3-8B` —— 少了组织前缀。
    ///   发真实请求会得到一句 `400 错误的请求`，完全看不出是名字写错了。
    ///
    ///   光说"你填的不在列表里"只解决了一半；
    ///   指出"是不是想填 Qwen/Qwen3-8B"才是把问题真正解决掉。
    /// </summary>
    /// <param name="models">查到的全部模型。</param>
    /// <param name="typed">用户当前填的名字。</param>
    public static List<string> SuggestFor(IReadOnlyList<ModelInfo> models, string? typed)
    {
        var suggestions = new List<string>();

        if (string.IsNullOrWhiteSpace(typed) || models.Count == 0)
        {
            return suggestions;
        }

        var needle = typed.Trim();

        // ① 先找"最后一段完全等于用户填的"（组织前缀漏填是最常见的错法）
        //    Qwen3-8B  → Qwen/Qwen3-8B
        foreach (var m in models)
        {
            var last = m.Id.Contains('/') ? m.Id.Split('/')[^1] : m.Id;

            if (string.Equals(last, needle, StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add(m.Id);
            }
        }

        if (suggestions.Count > 0)
        {
            return suggestions.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
        }

        // ② 退一步：把用户填的名字里的分隔符都去掉再比
        //    （qwen3-8b / Qwen3.8B / Qwen_3_8B 这类写法差异很常见）
        static string Normalize(string s) =>
            new(s.Where(char.IsLetterOrDigit).ToArray());

        var normNeedle = Normalize(needle);

        foreach (var m in models)
        {
            var last = m.Id.Contains('/') ? m.Id.Split('/')[^1] : m.Id;

            if (string.Equals(Normalize(last), normNeedle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Normalize(m.Id), normNeedle, StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add(m.Id);
            }
        }

        if (suggestions.Count > 0)
        {
            return suggestions.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
        }

        // ③ 还不中就给包含关系的（宽松匹配），只在前 6 个
        foreach (var m in models)
        {
            if (m.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add(m.Id);
            }
        }

        return suggestions
            .OrderBy(s => s.Length)                 // 短的更像"正主"
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }


    /// <summary>
    /// 把 HTTP 错误翻译成**能照做的话**。
    ///
    /// 这一步很关键：401/403/404 对用户来说都是"查询失败"，
    /// 但它们的原因完全不同，处理方式也完全不同。
    /// 不翻译的话，用户除了重试没有别的办法。
    /// </summary>
    private static ModelQueryResult ClassifyHttpError(int status, string body, string url)
    {
        var snippet = body.Length > 200 ? body[..200] + "…" : body;

        return status switch
        {
            401 => ModelQueryResult.Fail(
                "API Key 无效或已过期（401）。",
                "检查 Key 有没有复制完整（前后常带空格），或者去服务商后台重新生成一个。"),

            403 => ModelQueryResult.Fail(
                "没有权限访问这个接口（403）。",
                "这个 Key 可能没有列出模型的权限。可以手动填模型名试试。"),

            404 => ModelQueryResult.Fail(
                $"接口不存在（404）：{url}",
                "地址可能填错了。常见写法是 https://服务商域名/v1 "
                + "（本工具会自动补 /v1/models）。"),

            429 => ModelQueryResult.Fail(
                "请求太频繁（429）。",
                "等一会儿再点。"),

            _ => ModelQueryResult.Fail(
                $"服务返回 {status}。",
                string.IsNullOrWhiteSpace(snippet) ? null : $"返回内容：{snippet}"),
        };
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024 / 1024:F1} GB";
        }

        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024:F0} MB";
        }

        return $"{bytes / 1024.0:F0} KB";
    }
}
