// 生成桌面工具箱的图标（多分辨率 .ico + 预览 PNG）。
// 用 .NET 的 System.Drawing 手绘，不引第三方库（与项目零依赖的取向一致）。
//
// 设计：一个圆角方形的"工具箱"，里面是 2×2 的四个小色块 —— 对应"一个外壳挂多个小工具"。
// 配色取自项目自带的 UI（SettingsWindow 里用的那套 GitHub 风格色板）：
//   主色 #2970D1（蓝，也是截图选区描边色）
//   背景 #1F2329（深灰，项目正文色）
// 这样图标和程序界面是同一套视觉语言，不是随便找来的图。

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

const string OutDir = @"C:\Users\24239\Desktop\DeepSeek-Workspace\04-其他项目\桌面工具箱\artifacts\icon";

// 要生成的尺寸。Windows 会按场景挑：
//   16 = 任务栏/托盘小图标   32 = 桌面(100%)   48 = 桌面(150%)/Alt+Tab
//   64 = 中图标              128/256 = 大图标与"超大图标"视图
int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };

Directory.CreateDirectory(OutDir);

// ---------------------------------------------------------------- 绘制单张

static Bitmap Render(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);

    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    // 所有尺寸都是按 256 的设计稿等比缩放 —— 保证各尺寸观感一致
    float s = size / 256f;

    // ---- 外框：深灰圆角方块（模拟工具箱外壳）----
    var body = new RectangleF(16 * s, 16 * s, 224 * s, 224 * s);
    float radius = 44 * s;

    using (var path = RoundedRect(body, radius))
    using (var brush = new LinearGradientBrush(
               body,
               Color.FromArgb(255, 0x2B, 0x31, 0x3A),   // 上: #2B313A
               Color.FromArgb(255, 0x16, 0x19, 0x1E),   // 下: #16191E
               LinearGradientMode.Vertical))
    {
        g.FillPath(brush, path);

        // 一道细描边让它在浅色桌面上也有轮廓
        using var pen = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, 2.5f * s));
        g.DrawPath(pen, path);
    }

    // ---- 提手（工具箱上方的小把手）----
    // 小尺寸下画提手会糊成一团，24px 以下省略
    if (size >= 24)
    {
        using var pen = new Pen(Color.FromArgb(255, 0x4A, 0x86, 0xD8), Math.Max(1.4f, 9f * s));
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        g.DrawArc(pen, 88 * s, 6 * s, 80 * s, 60 * s, 180, 180);
    }

    // ---- 2×2 四个工具色块 ----
    // 颜色与项目 UI 语义对应：
    //   蓝 #2970D1 = 剪贴板/主色     绿 #12A150 = 成功态（AI）
    //   橙 #D97A06 = 警告/转换       青 #2AA9C7 = 截图
    Color[] colors =
    {
        Color.FromArgb(255, 0x29, 0x70, 0xD1), // 左上 蓝
        Color.FromArgb(255, 0x12, 0xA1, 0x50), // 右上 绿
        Color.FromArgb(255, 0xD9, 0x7A, 0x06), // 左下 橙
        Color.FromArgb(255, 0x2A, 0xA9, 0xC7), // 右下 青
    };

    // 2x2 格子的几何：内边距 + 间隙
    float pad = 52 * s;
    float gap = 20 * s;
    float cell = (224 * s - pad * 2 - gap) / 2f;
    float left = 16 * s + pad;
    float top = 16 * s + pad;
    float cellRadius = Math.Max(1f, 16 * s);

    for (int i = 0; i < 4; i++)
    {
        float cx = left + (i % 2) * (cell + gap);
        float cy = top + (i / 2) * (cell + gap);
        var rect = new RectangleF(cx, cy, cell, cell);

        using var p = RoundedRect(rect, cellRadius);
        using var b = new SolidBrush(colors[i]);
        g.FillPath(b, p);
    }

    // ---- 高光：左上角一抹斜向白光，让图标有体积感 ----
    if (size >= 32)
    {
        using var hl = new LinearGradientBrush(
            new RectangleF(0, 0, size, size),
            Color.FromArgb(38, 255, 255, 255),
            Color.FromArgb(0, 255, 255, 255),
            LinearGradientMode.ForwardDiagonal);
        using var clip = RoundedRect(body, radius);
        var old = g.Clip;
        g.SetClip(clip);
        g.FillRectangle(hl, 0, 0, size, size);
        g.Clip = old;
    }

    return bmp;
}

// 圆角矩形路径
static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    var path = new GraphicsPath();

    // 半径不能超过半边长，否则图形会自交
    radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
    float d = radius * 2;

    if (radius <= 0.01f)
    {
        path.AddRectangle(r);
        return path;
    }

    path.AddArc(r.X, r.Y, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

// ---------------------------------------------------------------- 写出多分辨率 .ico
//
// .ico 结构：6 字节头 + 每张 16 字节目录项 + 各张的 PNG 数据。
// 用 PNG 而不是 BMP：Vista 以后都支持，且 256×256 必须是 PNG 才规范。
// （全 BMP 的写法在 256 尺寸上会让部分程序显示不出来。）

var frames = new List<(int Size, byte[] Png)>();
foreach (var sz in sizes)
{
    using var bmp = Render(sz);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    frames.Add((sz, ms.ToArray()));

    // 顺便存一份 PNG 供肉眼预览（尤其是 256 那张）
    if (sz is 256 or 48)
    {
        bmp.Save(Path.Combine(OutDir, $"toolbox-{sz}.png"), ImageFormat.Png);
    }
}

var icoPath = Path.Combine(OutDir, "toolbox.ico");
using (var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write))
using (var w = new BinaryWriter(fs))
{
    // ICONDIR
    w.Write((ushort)0);              // reserved
    w.Write((ushort)1);              // type = 1 (icon)
    w.Write((ushort)frames.Count);   // image count

    // 数据区起始偏移 = 6 + 16*n
    int offset = 6 + 16 * frames.Count;

    // ICONDIRENTRY × n
    foreach (var (sz, png) in frames)
    {
        w.Write((byte)(sz >= 256 ? 0 : sz)); // 0 表示 256
        w.Write((byte)(sz >= 256 ? 0 : sz));
        w.Write((byte)0);                    // 调色板数
        w.Write((byte)0);                    // reserved
        w.Write((ushort)1);                  // color planes
        w.Write((ushort)32);                 // bits per pixel
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }

    foreach (var (_, png) in frames)
    {
        w.Write(png);
    }
}

Console.WriteLine($"已生成图标：{icoPath}");
Console.WriteLine($"  包含尺寸：{string.Join(", ", sizes)}");
Console.WriteLine($"  文件大小：{new FileInfo(icoPath).Length:N0} 字节");

// 自检：读回来确认帧数和尺寸都对
using (var icon = new Icon(icoPath))
{
    Console.WriteLine($"  回读验证：默认尺寸 {icon.Width}×{icon.Height} —— OK");
}
