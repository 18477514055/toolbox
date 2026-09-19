using Path = System.IO.Path;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Toolbox.Core;

/// <summary>
/// 运行时数据的统一位置。
///
/// 刻意不放在项目目录里：项目目录是「源码」，这里是「用户数据」，两者生命周期不同。
/// 源码可以随时被覆盖/重装，用户攒了半年的剪贴板历史不能跟着一起没。
/// </summary>
public static class AppPaths
{
    /// <summary>数据根目录名（%LOCALAPPDATA% 下的文件夹名 / 便携模式下的子目录名）。</summary>
    private const string FolderName = "桌面工具箱";

    /// <summary>便携模式的标记文件名。存在它 = 数据全放在 exe 旁边。</summary>
    private const string PortableMarker = "portable.txt";

    /// <summary>
    /// 运行时数据根目录。
    ///
    /// 两种模式：
    ///   · **常规**（默认）：%LOCALAPPDATA%\桌面工具箱 —— 用户数据与程序分离，
    ///     程序可以随时被替换/重装而历史不丢。
    ///   · **便携**（exe 旁边放一个 <c>portable.txt</c>，或用 <c>--portable</c> 启动）：
    ///     数据放在 exe 同目录的 <c>data\</c> 下。
    ///
    /// 为什么要加便携模式（"能不能分享给别人"的最后一环）：
    ///   把工具箱拷给同事时，他多半**不想让一个绿色小工具往自己
    ///   %LOCALAPPDATA% 里塞东西**——那是"安装"，不是"试用"。
    ///   给他一个便携包：数据全在文件夹里，不想要了**整个文件夹删掉就干净了**。
    ///   这对"免安装/绿色软件"是很重要的一条心理预期。
    ///
    /// ⚠️ 时序要求：必须在**任何** AppPaths 成员被访问之前决定好模式
    ///   （否则 SettingsFile / LogDir 等会先按常规模式建出来）。
    ///   所以由 App.OnStartup 的**第一行**调用 <see cref="InitializeMode"/>。
    /// </summary>
    public static string Root { get; private set; } = DefaultRoot();

    private static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FolderName);

    /// <summary>当前是不是便携模式（决定设置窗口里怎么显示"数据目录"）。</summary>
    public static bool IsPortable { get; private set; }

    /// <summary>
    /// 决定数据放在哪。**必须在启动最早期调用一次**。
    /// </summary>
    /// <param name="forcePortable">命令行显式要求便携（<c>--portable</c>）。</param>
    public static void InitializeMode(bool forcePortable)
    {
        var exeDir = ExeDirectory();

        // exe 旁边有 portable.txt ⇒ 便携（不需要命令行参数，双击就是便携）
        var markerExists = false;
        try
        {
            markerExists = exeDir is not null && File.Exists(Path.Combine(exeDir, PortableMarker));
        }
        catch
        {
            // 目录不可读就按常规模式
        }

        if (!forcePortable && !markerExists)
        {
            IsPortable = false;
            Root = DefaultRoot();
            return;
        }

        IsPortable = true;

        // 便携模式下数据放 exe 同级的 data\ 子目录（不污染 exe 所在文件夹本身）
        Root = exeDir is null
            ? DefaultRoot()          // 拿不到 exe 目录就退回常规模式，不能因此起不来
            : Path.Combine(exeDir, "data");
    }

    /// <summary>exe 所在目录。失败返回 null（调用方要能接受）。</summary>
    private static string? ExeDirectory()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return null;
            }

            return Path.GetDirectoryName(exe);
        }
        catch
        {
            return null;
        }
    }

    public static string ClipboardDir => Ensure(Path.Combine(Root, "clipboard"));
    public static string IndexFile => Path.Combine(ClipboardDir, "index.jsonl");
    public static string BlobDir => Ensure(Path.Combine(ClipboardDir, "blobs"));
    public static string ThumbDir => Ensure(Path.Combine(ClipboardDir, "thumbs"));
    public static string VolatileDir => Ensure(Path.Combine(ClipboardDir, "volatile"));

    public static string SettingsFile => Path.Combine(Ensure(Root), "settings.json");
    public static string LogDir => Ensure(Path.Combine(Root, "logs"));
    public static string LogFile => Path.Combine(LogDir, "toolbox.log");
    public static string ConvertTempDir => Ensure(Path.Combine(Root, "convert-temp"));

    /// <summary>截图默认保存目录（数据目录下的 screenshots 子目录，永远可写）。</summary>
    public static string ScreenshotDir => Ensure(Path.Combine(Root, "screenshots"));

    /// <summary>测试/自检产出。绝不写进用户档案目录，免得污染真实历史。</summary>
    public static string SelfTestDir => Ensure(Path.Combine(Root, "selftest"));

    /// <summary>
    /// 启动哨兵：每次启动尝试都往这里 append 一行，**独立于主日志**。
    ///
    /// 为什么非要有它 —— 这是"开机自启到底有没有把进程拉起来"的唯一可靠答案：
    ///   主日志要 `Log.Open` 之后才存在。而启动失败恰恰可能发生在它之前 ——
    ///   exe 根本没被拉起来、或者起来瞬间就崩了。那种情况下主日志一片空白，
    ///   看起来像"程序从没启动过"，排障时完全无从下手。
    ///   这里用最朴素的文件 append，不依赖 Log 的任何状态。
    /// </summary>
    public static string StartupSentinelFile => Path.Combine(Ensure(Root), "startup-sentinel.log");

    public static string Ensure(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // 建不出来就让调用方在使用时失败并给出明确报错，不在这里吞成静默
        }

        return dir;
    }
}
