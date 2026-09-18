using Toolbox.Shell;

namespace Toolbox.Tools.Ai;

/// <summary>
/// AI 后端接口。
///
/// 为什么要抽这一层（这是"工具箱能不能分享给别人用"的关键）：
///   原来 AI 工具硬绑 Ollama —— 对方想用就得先装 Ollama、再拉一个 4.7 GB 的模型。
///   对"分享一个工具箱"来说这个门槛太高了。
///   抽一层之后用户可以自己选：
///     · 本地 Ollama      —— 数据不出这台机器，但需要装；
///     · 远端 OpenAI 兼容 —— 填个地址 + Key 就能用，零安装。
///   两种后端的差别只落在这一层的实现里，上面的面板完全不用改。
/// </summary>
internal interface IAiClient
{
    /// <summary>给人看的后端描述，显示在面板上（如「本地 Ollama · qwen2.5:7b」）。</summary>
    string DisplayName { get; }

    /// <summary>连不上时的统一中文提示。绝不返回英文堆栈。</summary>
    string NotRunningMessage { get; }

    /// <summary>探活 + 取可用模型清单。失败时给出明确的中文原因。</summary>
    Task<(bool Ok, string Message, List<string> Models)> CheckAsync(CancellationToken ct);

    /// <summary>流式生成，每产出一小段就 yield 一次（界面据此实时显示）。</summary>
    IAsyncEnumerable<string> GenerateStreamAsync(string prompt, string? systemPrompt, CancellationToken ct);
}

/// <summary>
/// 按设置创建当前的 AI 后端。
///
/// 单独放一个工厂而不是让面板自己 new：面板有 3 处需要客户端（流式、探活、状态显示），
/// 分散 new 的话，将来加第三种后端就要改 3 个地方，很容易漏。
/// </summary>
internal static class AiClientFactory
{
    public static bool IsRemote(Settings s) =>
        string.Equals(s.AiBackend, "remote", StringComparison.OrdinalIgnoreCase);

    public static IAiClient Create(Settings s) =>
        IsRemote(s)
            ? new RemoteAiClient(s.RemoteApiUrl, s.RemoteApiKey, s.RemoteModel)
            : new OllamaClient(s.OllamaUrl, s.OllamaModel);
}
