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

/// <summary>
/// 探活结果该显示成什么颜色。
///
/// ⚠️ 放成一处，而不是在每个调用点各写一遍判断。
///   这正是本轮刚踩过的坑（见 DECISIONS 坑 49）：
///   "选目录"这个功能在 4 个地方各自 new 了不同的对话框，
///   于是一处坏、三处好，用户看到的现象完全无法解释。
///
/// 为什么需要"警告色"这个中间档：
///   Ok=true 只有两种情况 —— 真的全好，或者"连上了但模型名可疑"。
///   后者不该是绿的（用户会以为一切正常），也不该是红的
///   （红在面板里是硬拦截，会把一个其实能用的 AI 功能禁掉）。
///   所以给一个黄色：告诉你"这里有点不对，但不挡你路"。
/// </summary>
internal static class AiCheckUi
{
    /// <summary>可疑但不拦路的提示，消息里带这个前缀。</summary>
    public const string WarnPrefix = "⚠";

    /// <summary>该消息是不是"警告级"（连上了，但有地方可疑）。</summary>
    public static bool IsWarning(string? message) =>
        !string.IsNullOrEmpty(message)
        && message.TrimStart().StartsWith(WarnPrefix, StringComparison.Ordinal);

    /// <summary>探活结果对应的文字颜色。</summary>
    public static System.Windows.Media.Color ColorFor(bool ok, string? message) => ok switch
    {
        false => System.Windows.Media.Color.FromRgb(0xE0, 0x3B, 0x3B),   // 红：真不通
        true when IsWarning(message)
            => System.Windows.Media.Color.FromRgb(0xD9, 0x7A, 0x06),      // 黄：能用但可疑
        _ => System.Windows.Media.Color.FromRgb(0x12, 0xA1, 0x50),        // 绿：一切正常
    };
}

