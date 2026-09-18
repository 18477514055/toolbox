using Toolbox.Core;
using Toolbox.Shell;

namespace Toolbox.Tools.Clipboard;

/// <summary>
/// 工具 1：快捷剪贴板。
///
/// 它要做的事只有三件：**听、存、给回去**。
/// 核心的"听"（ClipboardListener + 读取顺序 + 重试）直接复用 P0 探针验证过的实现，
/// 一行都不重写——那些分支是踩了三个坑换来的。
/// </summary>
internal sealed class ClipboardTool : IToolboxTool
{
    private ToolboxContext? _ctx;
    private HistoryStore? _store;
    private ClipboardPanel? _panel;

    public string Id => "clipboard";

    public string Name => "快捷剪贴板";

    public string Glyph => "📋";

    public string DefaultHotKey => "Win+Shift+V";

    public string Description => "查历史复制的文本 / 图片 / 文件";

    public bool OpensBigWindow => false;

    public HistoryStore? Store => _store;

    public void Start(ToolboxContext ctx)
    {
        _ctx = ctx;

        _store = new HistoryStore(ctx.SettingsStore);
        _store.Load();

        ctx.Clipboard.Captured += OnCaptured;

        Log.Line($"快捷剪贴板已启动，历史 {_store.Count} 条。");
    }

    private void OnCaptured(ClipReadResult result)
    {
        // 注意：这个回调在**剪贴板监听线程**上，必须切回 UI 线程再碰界面
        try
        {
            var entry = _store?.Add(result);

            if (entry is null)
            {
                return;
            }

            Log.Line($"归档：{Describe(entry)}");

            _ctx?.Dispatcher.BeginInvoke(new Action(() => _panel?.RefreshList()));
        }
        catch (Exception ex)
        {
            Log.Exception("归档剪贴板内容失败", ex);
        }
    }

    private static string Describe(ClipEntry e) => e.Kind switch
    {
        ClipKind.Text => $"文本 {e.TextLength} 字（{e.Bytes} B）{(e.Volatile is null ? "" : " · 已摘要化")}",
        ClipKind.Image => $"图片 {e.ImageWidth}x{e.ImageHeight}（{e.Bytes / 1024} KB）",
        ClipKind.Files => $"文件 x{e.Files?.Count}（{e.Bytes} B）",
        _ => e.Kind.ToString(),
    };

    public void Invoke()
    {
        if (_ctx is null || _store is null)
        {
            return;
        }

        if (_panel is { IsVisible: true })
        {
            _panel.Hide();
            return;
        }

        _panel ??= new ClipboardPanel(_ctx, _store);
        _panel.ShowPanel();
    }

    public void Stop()
    {
        if (_ctx is not null)
        {
            _ctx.Clipboard.Captured -= OnCaptured;
        }

        _panel?.Close();
        _panel = null;
    }
}
