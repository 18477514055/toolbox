using System.IO;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using Path = System.IO.Path;
using File = System.IO.File;
using MemoryStream = System.IO.MemoryStream;

namespace Toolbox.Tools.Convert;

/// <summary>
/// 图片互转：完全靠 WPF 自带的编解码器，**零第三方依赖、不开外部进程**。
/// 这是所有转换里最快也最可靠的一条路，所以永远优先用它。
/// </summary>
internal sealed class ImageConverter : IConverter
{
    public string Name => "内置图片转换";

    public bool IsAvailable(out string? reason)
    {
        reason = null;
        return true; // WPF 自带，永远可用
    }

    public bool CanConvert(string sourcePath, string targetFormat)
    {
        if (!Formats.IsImage(sourcePath))
        {
            return false;
        }

        return targetFormat.ToLowerInvariant() switch
        {
            "png" or "jpg" or "jpeg" or "bmp" or "tiff" or "gif" => true,
            _ => false,
        };
    }

    public Task<ConvertResult> ConvertAsync(
        string sourcePath,
        string targetFormat,
        string outputDir,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                using var input = File.OpenRead(sourcePath);
                var decoder = BitmapDecoder.Create(input,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                frame.Freeze();

                BitmapEncoder encoder;
                var ext = targetFormat.ToLowerInvariant();

                switch (ext)
                {
                    case "jpg" or "jpeg":
                        encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                        ext = "jpg";
                        break;
                    case "bmp":
                        encoder = new BmpBitmapEncoder();
                        break;
                    case "tiff":
                        encoder = new TiffBitmapEncoder();
                        break;
                    case "gif":
                        encoder = new GifBitmapEncoder();
                        break;
                    default:
                        encoder = new PngBitmapEncoder();
                        ext = "png";
                        break;
                }

                // JPG / BMP / GIF 不支持透明通道，先转成 Bgr24，否则编码会抛异常。
                // 注意 target 声明成 BitmapSource 而不是 BitmapFrame ——
                // FormatConvertedBitmap 不是 BitmapFrame，但两者都是 BitmapSource，
                // BitmapFrame.Create(BitmapSource) 两个都能吃。
                BitmapSource target = frame;
                if (ext is "jpg" or "bmp" or "gif")
                {
                    var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgr24, null, 0);
                    converted.Freeze();
                    target = converted;
                }

                encoder.Frames.Add(BitmapFrame.Create(target));

                var baseName = Path.GetFileNameWithoutExtension(sourcePath);
                var outputPath = FileNaming.UniquePath(Path.Combine(outputDir, $"{baseName}.{ext}"));

                using (var output = File.Create(outputPath))
                {
                    encoder.Save(output);
                }

                var info = new FileInfo(outputPath);
                return new ConvertResult(true, outputPath,
                    $"{target.PixelWidth}×{target.PixelHeight}，{info.Length / 1024} KB");
            }
            catch (OperationCanceledException)
            {
                return new ConvertResult(false, null, "已取消");
            }
            catch (Exception ex)
            {
                Log.Exception($"图片转换失败：{sourcePath}", ex);
                return new ConvertResult(false, null, $"图片转换失败：{ex.Message}");
            }
        }, ct);
    }
}
