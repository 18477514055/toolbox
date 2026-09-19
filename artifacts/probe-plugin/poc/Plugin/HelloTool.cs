using System;
using Poc.Contracts;

namespace Poc.Plugin;

public sealed class HelloTool : IPocTool
{
    private IPocHost? _host;

    public string Id => "hello";
    public string Name => "示例工具";

    public void Attach(IPocHost host)
    {
        _host = host;
        var theme = host.GetSetting("theme");
        host.Log($"插件「{Name}」已接入，读到宿主的设置 theme={theme}");

        // ★ 直接开窗口会阻塞（POC 里没有消息循环），所以这里只验证"能构造出来"
        var win = new DemoWindow($"宿主告诉我：theme={theme}（这行字来自宿主设置）");
        host.Log($"插件自带的 XAML 窗口构造成功：Title=「{win.Title}」，Content={(win.Content is null ? "空 ★" : "已加载")}");
        win.Close();
    }

    public void Open() => _host?.Log("Open() 被调用");
}
