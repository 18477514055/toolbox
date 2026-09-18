using System;
using System.Linq;
using Toolbox.Tools.QrCode;

// ============================================================================
// 剩下的 3 个失败是"偶发"的（同一等级对不同内容有时过有时不过）
// ⇒ 指向**掩码选择**算错了：选了惩罚分不是最低的掩码。
//
// 掩码选错的后果：二维码本身数据是对的，但花纹不利于扫码器定位，
//   表现就是"有时扫得出来、有时扫不出来"——正是我们看到的症状。
//
// 验证方法：把 8 种掩码全试一遍，看**每一种**单独渲染能不能被解出。
//   如果某些掩码能解、我们选的那个不能解 ⇒ 就是选择逻辑的问题。
// ============================================================================

Console.WriteLine("=== 掩码选择诊断 ===");
Console.WriteLine();
Console.WriteLine("（本诊断只看惩罚分分布；能否解码由 VerifyQr 用 ZXing 验）");
Console.WriteLine();

foreach (var text in new[]
{
    "桌面工具箱 · 二维码功能测试",
    "这是一段比较长的中文内容，用来测试高版本二维码的编码是否正确无误。",
})
{
    foreach (var ecc in new[] { QrEcc.M, QrEcc.Q, QrEcc.H })
    {
        var m = QrEncoder.Encode(text, ecc);
        if (m is null) { continue; }

        Console.WriteLine($"\"{(text.Length > 14 ? text[..14] + "…" : text)}\"  {ecc}  版本{m.Version} 选中掩码={m.Mask}");
    }
}
