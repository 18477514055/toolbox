using Path = System.IO.Path;

namespace Toolbox.Tools.Convert;

internal sealed record ConvertResult(bool Success, string? OutputPath, string Message);

/// <summary>
/// 转换后端策略接口。
///
/// 交接文档 §五·工具3 明确要求做成可插拔：
/// LibreOffice 与 Office 各实现一份，按文件类型自动挑。
/// 这样**任一路径出问题都能切另一条**，而不是把用户堵死。
/// </summary>
internal interface IConverter
{
    /// <summary>后端名字，显示在结果里（"LibreOffice" / "Office" / "内置图片"）。</summary>
    string Name { get; }

    /// <summary>依赖是否就绪。没就绪时上层要给明确提示，而不是等它报错。</summary>
    bool IsAvailable(out string? reason);

    bool CanConvert(string sourcePath, string targetFormat);

    Task<ConvertResult> ConvertAsync(
        string sourcePath,
        string targetFormat,
        string outputDir,
        CancellationToken ct);
}

/// <summary>格式判定与可选目标格式。</summary>
internal static class Formats
{
    public static readonly string[] ImageExts =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    private static readonly string[] WordExts =
        { ".doc", ".docx", ".odt", ".rtf", ".txt", ".html", ".htm", ".md" };

    private static readonly string[] SheetExts =
        { ".xls", ".xlsx", ".ods", ".csv" };

    private static readonly string[] SlideExts =
        { ".ppt", ".pptx", ".odp" };

    public static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();

    public static bool IsImage(string path) => ImageExts.Contains(Ext(path));

    public static bool IsPdf(string path) => Ext(path) == ".pdf";

    public static bool IsWord(string path) => WordExts.Contains(Ext(path));

    public static bool IsSheet(string path) => SheetExts.Contains(Ext(path));

    public static bool IsSlide(string path) => SlideExts.Contains(Ext(path));

    public static bool IsDocument(string path) => IsWord(path) || IsSheet(path) || IsSlide(path);

    public static bool IsSupported(string path)
        => IsImage(path) || IsPdf(path) || IsDocument(path);

    /// <summary>
    /// 这类文件可以转成什么。
    ///
    /// 刻意不做"什么都支持"的假象：PDF 只能转 docx（要靠 Office），
    /// 因为 soffice --convert-to **不能可靠读 PDF**（DECISIONS.md 已实测确认）。
    /// 与其让用户转半天报个看不懂的错，不如一开始就说清楚。
    /// </summary>
    public static List<string> TargetsFor(string path)
    {
        if (IsImage(path))
        {
            return new List<string> { "png", "jpg", "bmp", "tiff", "gif", "pdf" };
        }

        if (IsPdf(path))
        {
            return new List<string> { "docx" };
        }

        if (IsWord(path))
        {
            return new List<string> { "pdf", "docx", "txt", "html", "odt" };
        }

        if (IsSheet(path))
        {
            return new List<string> { "pdf", "csv", "xlsx", "html" };
        }

        if (IsSlide(path))
        {
            return new List<string> { "pdf", "pptx" };
        }

        return new List<string>();
    }

    /// <summary>这个目标格式是不是必须靠外部后端（LibreOffice / Office）。</summary>
    public static bool NeedsExternalBackend(string sourcePath, string targetFormat)
    {
        if (!IsImage(sourcePath))
        {
            return true;
        }

        // 图片转 pdf 走 LibreOffice，其余图片格式 WPF 自己就能编
        return string.Equals(targetFormat, "pdf", StringComparison.OrdinalIgnoreCase);
    }

    public static string Describe(string path)
    {
        var ext = Ext(path);
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" => "图片",
            ".pdf" => "PDF",
            ".doc" or ".docx" or ".odt" or ".rtf" or ".txt" or ".html" or ".htm" or ".md" => "文档",
            ".xls" or ".xlsx" or ".ods" or ".csv" => "表格",
            ".ppt" or ".pptx" or ".odp" => "演示",
            _ => "未知",
        };
    }
}
