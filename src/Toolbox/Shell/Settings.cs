using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Path = System.IO.Path;
using File = System.IO.File;

namespace Toolbox.Shell;

/// <summary>
/// 全部设置项。设计原则：**能配置的东西一律给默认值，程序不依赖设置文件也能跑**。
/// 设置文件损坏 / 不存在 / 是旧版本，都必须能正常启动（见 SettingsStore.Load）。
/// </summary>
public sealed class Settings
{
    // ---------------- 热键 ----------------

    /// <summary>工具 Id → 热键描述，如 "clipboard" → "Win+Shift+V"。</summary>
    public Dictionary<string, string> HotKeys { get; set; } = new();

    /// <summary>
    /// 工具 Id → **这个热键是谁定的**（"auto" = 工具箱自动降级写进来的）。
    ///
    /// 为什么必须有这个字段（这是一个真实修掉的缺陷）：
    ///   原来"默认键被占用 → 自动降级"会把结果直接写进 <see cref="HotKeys"/>，
    ///   而注册逻辑把「<see cref="HotKeys"/> 里有条目」一律理解成**用户自己改过**。
    ///   于是那次降级摇身一变成了"用户的选择"，带来两个后果：
    ///     ① 占用者退出、默认键重新空闲后，工具箱**永远不会回去用默认键**；
    ///     ② 以后这个备选键被占用时，只会报错、不会降级（因为"用户改过的键不自动换"）。
    ///   用户会莫名其妙地卡在一个更差的备选键上，而且界面上看不出这是被自动改的。
    ///
    /// DECISIONS.md 坑 22 已经写明「同一个字段别承载两种语义」——
    /// 这里正是同一个错误的另一个实例：**系统的兜底动作**混进了**用户意图**所在的字段。
    ///
    /// 语义（条目不存在 = 用户没动过 ⇒ 按默认键处理）：
    ///   "user" = 用户在设置里填的；
    ///   "auto" = 工具箱降级写进来的，**随时可以而且应该**重新尝试默认键。
    /// </summary>
    public Dictionary<string, string> HotKeyOrigins { get; set; } = new();

    // ---------------- 工具开关（模块化） ----------------

    /// <summary>
    /// ⭐ **总开关**：工具箱整体是否启用。
    ///
    /// 关掉之后：
    ///   · **所有**工具都不再运行（不注册热键、不启动后台监听、托盘/悬浮窗也不显示入口）；
    ///   · 但**程序本身还在跑**（托盘图标保留），随时可以再打开 ——
    ///     这跟"退出"是两回事，用户不用重新启动程序。
    ///
    /// 为什么要有总开关而不是让用户一个个关：
    ///   12 个工具挨个关太麻烦；而"我今天只想用它做一件事"是真实需求。
    ///   总开关是**一键回到安静状态**的入口。
    /// </summary>
    public bool ToolsEnabled { get; set; } = true;

    /// <summary>
    /// 工具 Id → 是否启用。
    ///
    /// 语义（与 <see cref="HotKeys"/> 一致的写法，避免"同一个字段承载两种语义"）：
    ///   · **条目不存在** = 用工具自带的默认值（见 <c>ToolDefaults</c>）；
    ///   · 条目为 true  = 用户显式开启；
    ///   · 条目为 false = 用户显式关闭。
    ///
    /// 为什么用"不存在 = 默认"而不是把默认值直接写进去：
    ///   ① 首次运行时能按"常用 5 个默认开、其余默认关"来给初始状态，
    ///      而不用在代码里硬编码一批初始值；
    ///   ② 以后调整默认策略时，**没表过态的用户会自动跟随新默认**，
    ///      而表过态的（字典里有条目）保持不动 —— 这才是符合直觉的行为。
    /// </summary>
    public Dictionary<string, bool> ToolEnabled { get; set; } = new();

    // ---------------- 通用 ----------------

    public bool StartWithWindows { get; set; }
    public bool Paused { get; set; }

    // ---------------- 悬浮窗 ----------------

    public bool ShowFloatingWindow { get; set; } = true;

    /// <summary>位置按「工作区百分比」存，不存绝对像素——否则外接屏拔了会跑到屏幕外。</summary>
    public double FloatingX { get; set; } = 0.97;
    public double FloatingY { get; set; } = 0.55;

    public double FloatingOpacity { get; set; } = 0.62;

    /// <summary>打开裁剪/转换这类大窗口时临时隐藏悬浮窗。</summary>
    public bool HideFloatingWhenToolOpen { get; set; } = true;

    public bool FloatingCollapsed { get; set; }

    /// <summary>悬浮窗上显示哪些按钮（工具 Id 列表）。空 = 全部显示。</summary>
    public List<string> FloatingButtons { get; set; } = new();

    // ---------------- 剪贴板历史 ----------------

    public int MaxEntries { get; set; } = 2000;
    public long MaxImageBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>排错用：显示工具箱自己写入的条目（默认不显示，见规则③）。</summary>
    public bool ShowToolWritten { get; set; }

    // ---------------- AI ----------------

    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
    public string OllamaModel { get; set; } = "qwen2.5:7b";

    /// <summary>
    /// AI 后端："ollama"（默认，本地）或 "remote"（远端 OpenAI 兼容 API）。
    ///
    /// 存在的意义是「能不能把工具箱分享给别人」：
    /// 本地后端要求对方装 Ollama + 拉 4.7 GB 模型，门槛太高；
    /// 远端后端只要填地址 + Key 就能用，工具箱本身零安装。
    /// </summary>
    public string AiBackend { get; set; } = "ollama";

    /// <summary>
    /// 远端 API 地址。填到 /v1 为止即可，例如 https://api.deepseek.com/v1 。
    /// 不带 /v1 也会自动补上 —— 不该让用户去记这种细节。
    /// </summary>
    public string RemoteApiUrl { get; set; } = "";

    /// <summary>远端 API Key。存本地配置文件里，不上传、不外发。</summary>
    public string RemoteApiKey { get; set; } = "";

    /// <summary>远端模型名，例如 deepseek-chat / gpt-4o-mini / qwen-plus。</summary>
    public string RemoteModel { get; set; } = "";

    /// <summary>整窗口读取时默认只取主内容区（少带导航/广告噪声）。</summary>
    public bool AiMainContentOnly { get; set; } = true;

    /// <summary>单次送模型的字数上限，超了分块。</summary>
    public int AiChunkChars { get; set; } = 3500;

    // ---------------- 格式转换 ----------------

    public string? LibreOfficePath { get; set; }
    public string? LastConvertDir { get; set; }
    public string? LastConvertFormat { get; set; }

    // ---------------- 图片 ----------------

    public string? LastImageDir { get; set; }

    // ---------------- 快捷截图 ----------------

    /// <summary>
    /// 截图保存目录。留空则默认保存到 <see cref="Core.AppPaths.ScreenshotDir"/>
    /// （数据目录下的 screenshots 子目录，永远可写）。
    /// 用户可在设置里改成任意目录（比如「桌面」或某个网盘同步文件夹）。
    /// </summary>
    public string? ScreenshotOutputDir { get; set; }

    // ---------------- 批量重命名 ----------------

    /// <summary>上次用的改名目录（省得每次重新点一路）。</summary>
    public string? LastRenameDir { get; set; }

    // ---------------- 哈希校验 ----------------

    /// <summary>哈希工具上次选的算法（"SHA256" / "MD5"），省得每次重选。</summary>
    public string? LastHashAlgorithm { get; set; }

    // ---------------- 二维码 ----------------

    /// <summary>二维码图片的默认保存目录（留空 = 数据目录下的 qrcodes\）。</summary>
    public string? QrOutputDir { get; set; }

    // ---------------- 批量打包 ----------------


    /// <summary>压缩包的默认输出目录（留空 = 和文件放在一起）。</summary>

    public string? ArchiveOutputDir { get; set; }


    /// <summary>生成二维码时的默认边长（像素）。</summary>
    public int QrSize { get; set; } = 512;
}

/// <summary>
/// 设置的读写。刻意做成「**读失败就用默认值继续跑**」——
/// 设置文件坏掉导致整个工具箱打不开，是绝对不能接受的失败模式。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    public SettingsStore()
    {
        Current = Load();
    }

    public Settings Current { get; private set; }

    public string FilePath => Core.AppPaths.SettingsFile;

    private static Settings Load()
    {
        try
        {
            var path = Core.AppPaths.SettingsFile;
            if (!File.Exists(path))
            {
                return new Settings();
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Settings();
            }

            return JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings();
        }
        catch (Exception ex)
        {
            Core.Log.Exception("读取设置失败，已回退到默认设置", ex);
            return new Settings();
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var path = Core.AppPaths.SettingsFile;
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options), new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    File.Replace(temp, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception ex)
            {
                Core.Log.Exception("保存设置失败", ex);
            }
        }
    }
}
