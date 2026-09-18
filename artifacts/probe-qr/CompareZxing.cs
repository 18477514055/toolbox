using System;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

// ============================================================================
// 决定性对照：拿 **ZXing 自己生成的矩阵** 和 **我生成的矩阵** 逐步比对。
//
// 如果掩码号、版本一致但矩阵不同 ⇒ 我们的数据位/ECC 位有错
// 如果掩码号就不一样            ⇒ 掩码选择逻辑不同（不一定是错，标准允许）
// ============================================================================

Console.WriteLine("=== 与 ZXing 生成的矩阵逐位对照 ===");
Console.WriteLine();

var cases = new (string Text, string EccName)[]
{
    ("hello", "M"),
    ("桌面工具箱 · 二维码功能测试", "M"),
    ("这是一段比较长的中文内容，用来测试高版本二维码的编码是否正确无误。", "L"),
};

foreach (var (text, eccName) in cases)
{
    var hints = new System.Collections.Generic.Dictionary<EncodeHintType, object>
    {
        [EncodeHintType.CHARACTER_SET] = "UTF-8",
        // ★ 关键：把版本**钉死**成我这边算出来的版本。
        //   否则 ZXing 默认会选更大的版本（它对"字符数 vs 字节数"的估算口径与我不同），
        //   尺寸不同就没法逐位比对 —— 那是比对方法的问题，不是编码有错。
        [EncodeHintType.QR_VERSION] = 0,   // 下面会逐个覆盖
        [EncodeHintType.ERROR_CORRECTION] = eccName switch
        {
            "L" => ZXing.QrCode.Internal.ErrorCorrectionLevel.L,
            "Q" => ZXing.QrCode.Internal.ErrorCorrectionLevel.Q,
            "H" => ZXing.QrCode.Internal.ErrorCorrectionLevel.H,
            _ => ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
        },
    };

    // 先算出我这边的版本
    var myEccPre = eccName switch
    {
        "L" => Toolbox.Tools.QrCode.QrEcc.L,
        "Q" => Toolbox.Tools.QrCode.QrEcc.Q,
        "H" => Toolbox.Tools.QrCode.QrEcc.H,
        _ => Toolbox.Tools.QrCode.QrEcc.M,
    };

    var payloadLen = System.Text.Encoding.UTF8.GetByteCount(text);
    var myVersion = Toolbox.Tools.QrCode.QrEncoder.FindVersion(payloadLen, myEccPre);

    hints[EncodeHintType.QR_VERSION] = myVersion;

    var writer = new QRCodeWriter();
    var zxMatrix = writer.encode(text, BarcodeFormat.QR_CODE, 0, 0, hints);

    var zxSize = zxMatrix.Width;

    // 我这边：按 ZXing 选出的版本/等级编码
    var myEcc = eccName switch
    {
        "L" => Toolbox.Tools.QrCode.QrEcc.L,
        "Q" => Toolbox.Tools.QrCode.QrEcc.Q,
        "H" => Toolbox.Tools.QrCode.QrEcc.H,
        _ => Toolbox.Tools.QrCode.QrEcc.M,
    };

    var mine = Toolbox.Tools.QrCode.QrEncoder.Encode(text, myEcc);
    if (mine is null) { Console.WriteLine("  (我这边编码失败)"); continue; }

    Console.WriteLine($"「{(text.Length > 12 ? text[..12] + "…" : text)}」 {eccName}");
    Console.WriteLine($"  ZXing 尺寸 {zxSize}（版本 {(zxSize - 17) / 4}）  我的尺寸 {mine.Size}（版本 {mine.Version}）");

    if (zxSize != mine.Size)
    {
        Console.WriteLine("  ★ 版本就不同 —— 先把版本选一致再比");
        Console.WriteLine();
        continue;
    }

    // 逐位比对
    var diff = 0;
    var firstDiff = "";

    for (var y = 0; y < zxSize; y++)
    {
        for (var x = 0; x < zxSize; x++)
        {
            var a = zxMatrix[x, y];
            var b = mine[x, y];

            if (a != b)
            {
                diff++;
                if (firstDiff.Length == 0) { firstDiff = $"({x},{y})"; }
            }
        }
    }

    Console.WriteLine($"  不同的模块数：{diff} / {zxSize * zxSize}"
                      + (diff == 0 ? "  ✅ 完全一致" : $"  （首个不同位置 {firstDiff}）"));

    // 若不同，看看是不是"整体反相"（掩码不同的一种常见表现）
    if (diff > 0)
    {
        var inverted = 0;
        for (var y = 0; y < zxSize; y++)
        {
            for (var x = 0; x < zxSize; x++)
            {
                if (zxMatrix[x, y] == mine[x, y]) { inverted++; }
            }
        }

        Console.WriteLine($"  若按『完全反相』比对，相同模块数 = {inverted} / {zxSize * zxSize}");
    }

    Console.WriteLine();
}

return 0;
