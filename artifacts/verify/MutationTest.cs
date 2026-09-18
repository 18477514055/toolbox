using System.Windows;
using System.IO;
using System.Text;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

// ============================================================================
// 变异测试：把坐标换算换回"旧算法"，验证新增的自检用例**真的会变红**。
//
// 一条永远通过的断言等于没有断言（本项目的 DECISIONS 也反复强调"先证明尺子对"）。
// 这里把旧算法（全局单一系数 1.2，即"多屏混合 DPI"场景下的错误系数）
// 代进同一批用例，断言它必须失败 —— 以此证明用例有鉴别力。
// ============================================================================

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"[通过] {name}"); }
    else { fail++; Console.WriteLine($"[失败] {name}" + (detail.Length > 0 ? $"\n        {detail}" : "")); }
}

Console.WriteLine("==================================================");
Console.WriteLine("变异测试：证明新增用例能抓住旧算法");
Console.WriteLine("==================================================");
Console.WriteLine();

// 旧算法：一个全局比值，且在多屏混合 DPI 场景下等于 1.2（见反例推导）
static Int32Rect OldAlgorithm(
    double lx, double ly, double lw, double lh,
    double globalScale, int bw, int bh)
{
    var x = (int)Math.Round(lx * globalScale);
    var y = (int)Math.Round(ly * globalScale);
    var w = (int)Math.Round(lw * globalScale);
    var h = (int)Math.Round(lh * globalScale);
    w = Math.Max(1, Math.Min(w, bw - x));
    h = Math.Max(1, Math.Min(h, bh - y));
    return new Int32Rect(x, y, w, h);
}

int NewAlgorithm(
    double lx, double ly, double lw, double lh,
    double scale, int bw, int bh)
{
    var r = Toolbox.Core.ScreenCoordinateMapper.ToPixelRect(lx, ly, lw, lh, scale, scale, bw, bh);
    return (r.X, r.Y, r.Width, r.Height).GetHashCode();
}

// 主屏 150% + 副屏 100%（副屏在右）：物理 3840 宽，逻辑 3200 宽
const double GlobalScale = 1.2;   // = 3840 / 3200
const double PrimaryScale = 1.5;  // 主屏真实系数

// 用户在主屏上框选：逻辑 (100,100,200x100)
// 正确物理结果 = (150,150,300x150)
var correct = Toolbox.Core.ScreenCoordinateMapper.ToPixelRect(
    100, 100, 200, 100, PrimaryScale, PrimaryScale, 3840, 1080);
Check("新算法：主屏框选 → (150,150,300x150)",
    correct.X == 150 && correct.Y == 150 && correct.Width == 300 && correct.Height == 150,
    $"实际 ({correct.X},{correct.Y},{correct.Width}x{correct.Height})");

var old = OldAlgorithm(100, 100, 200, 100, GlobalScale, 3840, 1080);
Check("★ 旧算法：同一输入 → 必然算错（证明用例有鉴别力）",
    !(old.X == 150 && old.Y == 150 && old.Width == 300 && old.Height == 150),
    $"旧算法给出 ({old.X},{old.Y},{old.Width}x{old.Height})，与正确的 (150,150,300x150) 不符" +
    $" —— 水平方向偏 {150 - old.X} px，宽度差 {300 - old.Width} px。");

// 单屏场景下两者必须一致（证明修复对现有用户零影响）
var newSingle = Toolbox.Core.ScreenCoordinateMapper.ToPixelRect(
    300, 200, 400, 300, 1.0, 1.0, 1920, 1080);
var oldSingle = OldAlgorithm(300, 200, 400, 300, 1.0, 1920, 1080);
Check("单屏 100%：新旧算法结果完全一致（对现有环境零影响）",
    newSingle.X == oldSingle.X && newSingle.Y == oldSingle.Y
    && newSingle.Width == oldSingle.Width && newSingle.Height == oldSingle.Height,
    $"新 ({newSingle.X},{newSingle.Y},{newSingle.Width}x{newSingle.Height}) vs " +
    $"旧 ({oldSingle.X},{oldSingle.Y},{oldSingle.Width}x{oldSingle.Height})");

Console.WriteLine();
Console.WriteLine("--------------------------------------------------");
Console.WriteLine($"变异测试结果：{pass}/{pass + fail} 通过");
Console.WriteLine("--------------------------------------------------");
Console.WriteLine();
Console.WriteLine("结论：新增用例对『全局单一系数』这个错误实现是**敏感的**（会红），");
Console.WriteLine("      同时对单屏 100%（本机现状）**不敏感**（结果一致）——");
Console.WriteLine("      这正是想要的：抓住缺陷，却不制造误报。");

return fail == 0 ? 0 : 1;
