using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toolbox.Core;
using Toolbox.Tools.Ai;
using Toolbox.Tools.Convert;

namespace Toolbox.Shell;

internal sealed partial class SettingsWindow : Window
{
    private readonly ToolboxContext _ctx;
    private readonly ToolRegistry _registry;
    private readonly Func<IReadOnlyList<HotKeyResult>> _applyHotKeys;

    /// <summary>
    /// 保存后应用"功能开关"的回调（启停工具 + 重画悬浮窗）。
    ///
    /// 为什么用回调而不是让设置窗口自己去干：
    ///   启停工具要碰 ToolRegistry / FloatingWindow / TrayIcon，
    ///   而设置窗口是 Shell 层的一个纯界面 —— 把那些东西塞进来会让它知道太多。
    ///   与既有的 _applyHotKeys 是同一套写法，保持一致。
    /// </summary>
    private readonly Action _applyToolSwitches;

    private readonly Dictionary<string, TextBox> _hotKeyBoxes = new();
    private readonly Dictionary<string, TextBlock> _hotKeyStatus = new();

    /// <summary>
    /// 一条热键的注册结果。
    /// Error 有值 = 没注册上；Note 有值 = 注册上了，但用的不是原本那个键
    /// （默认键被别的程序占用，已自动降级）。这两种都必须让用户看见，不能静默。
    /// </summary>
    internal sealed record HotKeyResult(string ToolId, string Spec, string? Error, string? Note = null)
    {
        /// <summary>
        /// 这一条是**用户主动关掉的**（在设置里留空），不是出了什么问题。
        ///
        /// 必须和"被占用 / 注册失败"分开报告。混在一起的话，
        /// 开机汇总会变成「以下热键的默认组合键已被别的程序占用」——
        /// 而用户明明是自愿不要热键的，却被系统告知"被占用了"，
        /// 只会让他以为自己配错了，然后去瞎改。
        /// </summary>
        public bool Disabled { get; init; }
    }

    public SettingsWindow(
        ToolboxContext ctx,
        ToolRegistry registry,
        Func<IReadOnlyList<HotKeyResult>> applyHotKeys,
        Action applyToolSwitches)
    {
        _ctx = ctx;
        _registry = registry;
        _applyHotKeys = applyHotKeys;
        _applyToolSwitches = applyToolSwitches;

        InitializeComponent();

        LoadValues();
        BuildToolSwitchRows();
        BuildHotKeyRows();

        SaveBtn.Click += (_, _) => Save();
        CloseBtn.Click += (_, _) => Close();
        OpenDataBtn.Click += (_, _) => OpenPath(AppPaths.Root);
        OpenLogBtn.Click += (_, _) => OpenPath(AppPaths.LogDir);
        ManageToolsBtn.Click += (_, _) => OpenToolStore();
        SofficeBrowseBtn.Click += (_, _) => BrowseFile(
            SofficeBox, "选择 soffice.exe", "可执行文件|*.exe|所有文件|*.*");
        ScreenshotDirBrowseBtn.Click += (_, _) => BrowseFolder(ScreenshotDirBox);
        TestOllamaBtn.Click += async (_, _) => await TestOllamaAsync();
        OpacitySlider.ValueChanged += (_, _) =>
            OpacityLabel.Text = $"{OpacitySlider.Value:0.00}";

        // 功能开关：三个批量按钮
        AllToolsOnBtn.Click += (_, _) => SetAllToolSwitches(true);
        AllToolsOffBtn.Click += (_, _) => SetAllToolSwitches(false);
        RestoreDefaultsBtn.Click += (_, _) => RestoreDefaultToolSwitches();

        // 热键：一键清空 / 一键恢复（用户明确要求"可以不填任何热键"）
        ClearAllHotKeysBtn.Click += (_, _) => SetAllHotKeyBoxes("");
        RestoreAllHotKeysBtn.Click += (_, _) => SetAllHotKeyBoxes("默认");

        UpdateToolSwitchHint();

        Loaded += async (_, _) => await RefreshOllamaHintAsync();
    }

    // ---------------------------------------------------------------- 工具管理

    private Toolbox.Tools.Store.ToolStoreWindow? _storeWindow;

    /// <summary>
    /// 打开「工具管理」窗口（按需下载入口）。
    ///
    /// 单例：重复点不叠窗口，而是把已有的激活 —— 与其它工具窗口的做法一致。
    /// </summary>
    private void OpenToolStore()
    {
        try
        {
            if (_storeWindow is { IsVisible: true })
            {
                _storeWindow.Activate();
                return;
            }

            _storeWindow = new Toolbox.Tools.Store.ToolStoreWindow(_ctx);
            _storeWindow.Closed += (_, _) => _storeWindow = null;
            _storeWindow.Show();
            _storeWindow.Activate();
        }
        catch (Exception ex)
        {
            Log.Exception("打开工具管理窗口失败", ex);
        }
    }

    // ---------------------------------------------------------------- 功能开关

    /// <summary>工具 Id → 界面上那个勾选框。保存时要挨个读。</summary>
    private readonly Dictionary<string, CheckBox> _toolSwitches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>总开关被改动。</summary>
    private void OnMasterSwitchChanged(object sender, RoutedEventArgs e)
    {
        // 总开关关掉时，把下面那排单个开关**变灰**（但不清空它们的选择）。
        //
        // 为什么不直接清空：用户可能只是临时关一下总开关，
        // 再打开时应该回到他原来的选择，而不是变成"全灭"。
        // 变灰 + 保留选择，语义是"这些选择现在不生效"，最符合直觉。
        var on = ToolsEnabledBox.IsChecked == true;

        foreach (var box in _toolSwitches.Values)
        {
            box.IsEnabled = on;
        }

        AllToolsOnBtn.IsEnabled = on;
        AllToolsOffBtn.IsEnabled = on;
        RestoreDefaultsBtn.IsEnabled = on;

        UpdateToolSwitchHint();
    }

    /// <summary>按工具注册表生成一排勾选框。</summary>
    private void BuildToolSwitchRows()
    {
        ToolSwitchHost.Children.Clear();
        _toolSwitches.Clear();

        ToolsEnabledBox.IsChecked = _ctx.Settings.ToolsEnabled;

        foreach (var tool in _registry.Tools)
        {
            // 一条：勾选框 + 默认标记 + 热键提示
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var box = new CheckBox
            {
                Content = $"{tool.Glyph}　{tool.Name}",
                IsChecked = ToolGate.IsEnabled(_ctx.Settings, tool.Id),
                VerticalAlignment = VerticalAlignment.Center,
            };

            // 勾选框旁边的说明：默认状态 + 这个工具干什么
            var tip = new TextBlock
            {
                Text = $"{tool.Description}",
                FontSize = 11,
                Margin = new Thickness(24, 1, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6)),
            };

            var left = new StackPanel();
            left.Children.Add(box);
            left.Children.Add(tip);
            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            // 右侧标注"默认开启"（让用户知道哪些是工具箱推荐常开的）
            var isDefaultOn = ToolGate.IsDefaultOn(tool.Id);
            var badge = new TextBlock
            {
                Text = isDefaultOn ? "默认开启" : "默认关闭",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(isDefaultOn
                    ? Color.FromRgb(0x12, 0xA1, 0x50)
                    : Color.FromRgb(0xA8, 0xB0, 0xBC)),
            };
            Grid.SetColumn(badge, 1);
            row.Children.Add(badge);

            ToolSwitchHost.Children.Add(row);
            _toolSwitches[tool.Id] = box;
        }

        // 按总开关的当前状态同步一次灰化
        OnMasterSwitchChanged(this, new RoutedEventArgs());
    }

    private void SetAllToolSwitches(bool on)
    {
        foreach (var box in _toolSwitches.Values)
        {
            box.IsChecked = on;
        }

        UpdateToolSwitchHint();
    }

    /// <summary>恢复成"工具箱推荐的默认"（常用 5 个开、其余关）。</summary>
    private void RestoreDefaultToolSwitches()
    {
        foreach (var (id, box) in _toolSwitches)
        {
            box.IsChecked = ToolGate.IsDefaultOn(id);
        }

        ToolsEnabledBox.IsChecked = true;
        OnMasterSwitchChanged(this, new RoutedEventArgs());
    }

    private void UpdateToolSwitchHint()
    {
        if (ToolsEnabledBox.IsChecked != true)
        {
            ToolSwitchHint.Text = "总开关已关闭 —— 下面这些选择暂不生效，重新打开总开关后会恢复。";
            ToolSwitchHint.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x7A, 0x06));
            return;
        }

        var onCount = _toolSwitches.Values.Count(b => b.IsChecked == true);
        var total = _toolSwitches.Count;

        ToolSwitchHint.Text = onCount == 0
            ? "⚠ 一个功能都没开 —— 悬浮窗上不会有任何按钮。"
            : $"已开启 {onCount} / {total} 个功能，这些会显示在悬浮窗上。保存后生效。";

        ToolSwitchHint.Foreground = new SolidColorBrush(onCount == 0
            ? Color.FromRgb(0xD9, 0x7A, 0x06)
            : Color.FromRgb(0x8A, 0x94, 0xA6));
    }

    // ---------------------------------------------------------------- 热键批量

    /// <summary>
    /// 批量设置所有热键框的内容。
    /// </summary>
    /// <param name="value">"" = 全部清空（不占任何热键）；"默认" = 恢复各自自带键。</param>
    private void SetAllHotKeyBoxes(string value)
    {
        foreach (var box in _hotKeyBoxes.Values)
        {
            box.Text = value;
        }

        SaveHint.Text = value.Length == 0
            ? "已清空全部热键 —— 点「保存」生效（之后全靠点悬浮窗 / 托盘）"
            : "已填回「默认」—— 点「保存」生效";
    }

    // ---------------------------------------------------------------- 载入

    private void LoadValues()
    {
        var s = _ctx.Settings;

        AutoStartBox.IsChecked = AutoStart.IsEnabled();
        AutoStartHint.Text = $"当前 exe：{AutoStart.ExecutablePath}";

        ShowFloatingBox.IsChecked = s.ShowFloatingWindow;
        HideFloatingBox.IsChecked = s.HideFloatingWhenToolOpen;
        OpacitySlider.Value = Math.Clamp(s.FloatingOpacity, 0.2, 1.0);
        OpacityLabel.Text = $"{OpacitySlider.Value:0.00}";

        MaxEntriesBox.Text = s.MaxEntries.ToString();
        MaxImageBox.Text = (s.MaxImageBytes / 1024 / 1024).ToString();
        ShowToolWrittenBox.IsChecked = s.ShowToolWritten;

        OllamaUrlBox.Text = s.OllamaUrl;
        OllamaModelBox.Text = s.OllamaModel;
        RemoteUrlBox.Text = s.RemoteApiUrl;
        RemoteKeyBox.Text = s.RemoteApiKey;
        RemoteModelBox.Text = s.RemoteModel;
        ChunkBox.Text = s.AiChunkChars.ToString();
        MainContentBox.IsChecked = s.AiMainContentOnly;

        // 后端选择放在最后设：它一旦落到非 0 值就会触发 OnAiBackendChanged，
        // 而那里面要读上面这些输入框的父面板，所以必须在它们都存在之后再切。
        AiBackendCombo.SelectedIndex = AiClientFactory.IsRemote(s) ? 1 : 0;
        ApplyBackendVisibility();

        SofficeBox.Text = s.LibreOfficePath ?? "";
        ScreenshotDirBox.Text = s.ScreenshotOutputDir ?? "";
        DataPathText.Text = AppPaths.Root;
    }

    // ---------------------------------------------------------------- AI 后端

    /// <summary>
    /// 切后端时只显示相关的那一组配置项。
    ///
    /// 不做成"两组都显示、各自加个『启用』勾选"：那样用户很容易填了远端却忘了勾，
    /// 或者两组都填了一半，出问题时根本说不清到底在用哪个。
    /// </summary>
    private void OnAiBackendChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyBackendVisibility();

        // 换后端意味着"当前连的到底通不通"这个结论作废了，提示语不能留着旧的。
        OllamaStatus.Text = AiBackendCombo.SelectedIndex == 1
            ? "已切到远端 API。填好地址和 Key 后点「测试连接」。"
            : "已切到本地 Ollama。点「测试连接」看看通不通。";
        OllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6));

        UpdatePrivacyText();
    }

    private void ApplyBackendVisibility()
    {
        // 空引用保护：ComboBox 的 SelectedIndex 是在 XAML 解析期就落下去的，
        // 那一刻后面这些面板还没被创建出来。不能假设它们一定在。
        if (OllamaPanel is null || RemotePanel is null)
        {
            return;
        }

        var remote = AiBackendCombo.SelectedIndex == 1;
        OllamaPanel.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
        RemotePanel.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 隐私说明不能写死。
    /// 原来那句"AI 用的是本地 Ollama 模型，数据不出这台机器"在后端切成远端之后就是**假话** ——
    /// 用户会以为自己没上传，实际每段文本都发出去了。这类文案必须跟着后端变。
    /// </summary>
    private void UpdatePrivacyText()
    {
        if (PrivacyText is null)
        {
            return;
        }

        PrivacyText.Text = AiBackendCombo.SelectedIndex == 1
            ? "剪贴板历史始终只在本机。但「快捷 AI」当前用的是**远端 API**："
              + "待处理的文本会通过网络发送到你在设置里填的那台服务器。"
              + "如果不想让内容离开这台机器，请把后端切回「本地 Ollama」。"
            : "剪贴板历史与 AI 处理全部在本机完成。AI 用的是本地 Ollama 模型，数据不出这台机器。"
              + "工具箱不会为了读数据而往剪贴板写东西，也不会把你自己复制的内容偷偷传出去。";
    }

    /// <summary>
    /// 用**界面上当前填的值**建客户端（而不是已保存的设置）。
    /// 用户改完地址往往直接点「测试连接」，如果读的是旧设置，测的就是上一个后端，
    /// 会给出一个驴唇不对马嘴的结论。
    /// </summary>
    private IAiClient BuildClientFromUi()
    {
        if (AiBackendCombo.SelectedIndex == 1)
        {
            return new RemoteAiClient(
                RemoteUrlBox.Text.Trim(),
                RemoteKeyBox.Text.Trim(),
                RemoteModelBox.Text.Trim());
        }

        var url = string.IsNullOrWhiteSpace(OllamaUrlBox.Text)
            ? "http://127.0.0.1:11434"
            : OllamaUrlBox.Text.Trim();
        var model = string.IsNullOrWhiteSpace(OllamaModelBox.Text)
            ? "qwen2.5:7b"
            : OllamaModelBox.Text.Trim();

        return new OllamaClient(url, model);
    }

    private void BuildHotKeyRows()
    {
        HotKeyHost.Children.Clear();
        _hotKeyBoxes.Clear();
        _hotKeyStatus.Clear();

        foreach (var tool in _registry.Tools)
        {
            AddHotKeyRow(tool.Id, tool.Name, tool.DefaultHotKey);

            // ★ 额外热键也必须出行。
            //   早先这里只遍历 Tools，于是「快捷 AI · 翻译选中」这个热键
            //   在设置里**完全看不见、也改不了** —— 它一旦被别的程序占用，
            //   用户连"在哪儿改"都找不到。功能能用但不可管理，等于半个 bug。
            foreach (var (name, defaultSpec) in tool.ExtraHotKeys)
            {
                AddHotKeyRow(
                    $"{tool.Id}.{name}",
                    $"{tool.Name} · {ToolNaming.ExtraLabel(name)}",
                    defaultSpec);
            }
        }
    }

    /// <summary>加一行「标签 + 输入框 + 状态」。</summary>
    private void AddHotKeyRow(string fullId, string labelText, string defaultSpec)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = labelText,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x29)),
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var box = new TextBox
        {
            // 条目存在就用条目里的值（可能是空串 = 用户禁用了热键）；
            // 只有条目不存在时才显示默认键 —— 这样"已禁用"和"没配过"在界面上不会混淆。
            //
            // ★ 自动降级写进来的条目（HotKeyOrigins = "auto"）要显示**工具自带的默认键**，
            //   而不是那个当前在用的备选键。否则用户会以为"我的热键就是 Win+Alt+V"，
            //   看不出这只是因为默认键被占用而临时换的 —— 这正是原来那个缺陷的界面表现。
            Text = _ctx.Settings.HotKeyOrigins.TryGetValue(fullId, out var origin)
                   && string.Equals(origin, "auto", StringComparison.OrdinalIgnoreCase)
                ? defaultSpec
                : (_ctx.Settings.HotKeys.TryGetValue(fullId, out var custom)
                    ? custom.Trim()
                    : defaultSpec),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        _hotKeyBoxes[fullId] = box;

        var status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6)),
            Text = "（保存后生效）",
        };
        Grid.SetColumn(status, 2);
        row.Children.Add(status);
        _hotKeyStatus[fullId] = status;

        HotKeyHost.Children.Add(row);
    }

    // ---------------------------------------------------------------- 保存

    private void Save()
    {
        var s = _ctx.Settings;

        // ---- 功能开关 ----
        //
        // 只写"和默认不一样"的条目：
        //   · 用户的选择与默认一致 → 从字典里移除，让他继续跟随默认策略；
        //   · 不一致 → 显式写 true/false。
        // 这样以后调整默认策略时，没表过态的用户会自动跟上（见 Settings.ToolEnabled 的说明）。
        s.ToolsEnabled = ToolsEnabledBox.IsChecked == true;

        foreach (var (toolId, box) in _toolSwitches)
        {
            var userWants = box.IsChecked == true;

            if (userWants == ToolGate.IsDefaultOn(toolId))
            {
                s.ToolEnabled.Remove(toolId);
            }
            else
            {
                s.ToolEnabled[toolId] = userWants;
            }
        }

        // 热键
        foreach (var (toolId, box) in _hotKeyBoxes)
        {
            var text = box.Text.Trim();

            // 留空 = 用户明确要求"这个工具不要热键"。
            //
            // 这里写**空串**而不是移除条目 —— 移除在注册逻辑里的含义是"用默认键"，
            // 那样就没法表达"禁用"了。两种意图必须分开存：
            //   条目不存在 = 没配过 → 用默认键
            //   条目为空串 = 配过且配成空 → 禁用
            if (text.Length == 0)
            {
                s.HotKeys[toolId] = "";
                s.HotKeyOrigins[toolId] = "user";   // 用户亲手填的，不可被自动降级覆盖
                continue;
            }

            // 想恢复工具自带的默认键：填「默认」两个字（界面上有说明）
            if (text.Equals("默认", StringComparison.OrdinalIgnoreCase)
                || text.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                s.HotKeys.Remove(toolId);
                s.HotKeyOrigins.Remove(toolId);     // 连"自动降级"的痕迹一起清掉
                continue;
            }

            s.HotKeys[toolId] = text;
            s.HotKeyOrigins[toolId] = "user";       // 用户在界面里明确指定的组合键
        }

        // 数字类，解析失败就用原值（不要把设置写坏）
        if (int.TryParse(MaxEntriesBox.Text.Trim(), out var maxEntries) && maxEntries >= 50)
        {
            s.MaxEntries = Math.Min(maxEntries, 200000);
        }

        if (long.TryParse(MaxImageBox.Text.Trim(), out var maxImageMb) && maxImageMb >= 10)
        {
            s.MaxImageBytes = maxImageMb * 1024 * 1024;
        }

        s.ShowFloatingWindow = ShowFloatingBox.IsChecked == true;
        s.HideFloatingWhenToolOpen = HideFloatingBox.IsChecked == true;
        s.FloatingOpacity = OpacitySlider.Value;
        s.ShowToolWritten = ShowToolWrittenBox.IsChecked == true;

        s.AiBackend = AiBackendCombo.SelectedIndex == 1 ? "remote" : "ollama";
        s.OllamaUrl = string.IsNullOrWhiteSpace(OllamaUrlBox.Text) ? "http://127.0.0.1:11434" : OllamaUrlBox.Text.Trim();
        s.OllamaModel = string.IsNullOrWhiteSpace(OllamaModelBox.Text) ? "qwen2.5:7b" : OllamaModelBox.Text.Trim();
        s.RemoteApiUrl = RemoteUrlBox.Text.Trim();
        s.RemoteApiKey = RemoteKeyBox.Text.Trim();
        s.RemoteModel = RemoteModelBox.Text.Trim();
        if (int.TryParse(ChunkBox.Text.Trim(), out var chunk) && chunk >= 500)
        {
            s.AiChunkChars = Math.Min(chunk, 20000);
        }

        s.AiMainContentOnly = MainContentBox.IsChecked == true;

        var soffice = SofficeBox.Text.Trim();
        s.LibreOfficePath = string.IsNullOrEmpty(soffice) ? null : soffice;

        var shotDir = ScreenshotDirBox.Text.Trim();
        s.ScreenshotOutputDir = string.IsNullOrEmpty(shotDir) ? null : shotDir;

        // 开机自启
        var wantAutoStart = AutoStartBox.IsChecked == true;
        if (AutoStart.Set(wantAutoStart, out var autoStartError))
        {
            s.StartWithWindows = wantAutoStart;
        }
        else
        {
            MessageBox.Show(this, autoStartError ?? "设置开机自启失败。", "桌面工具箱",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _ctx.SaveSettings();

        // 重新注册热键，并把结果逐条显示出来（注册失败必须可见）
        var results = _applyHotKeys();
        foreach (var r in results)
        {
            if (!_hotKeyStatus.TryGetValue(r.ToolId, out var status))
            {
                continue;
            }

            if (r.Error is null)
            {
                if (r.Disabled)
                {
                    // 用户自己选的，用中性灰 + 一个破折号，不要用橙色的 ⚠
                    // —— 那是给"出了意外"用的，用在这里会让人以为配错了。
                    status.Text = "— 已关闭（不占热键）";
                    status.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6));
                }
                else if (r.Note is null)
                {
                    status.Text = "✓ 已生效";
                    status.Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0xA1, 0x50));
                }
                else
                {
                    // 自动降级：注册成功了，但用的不是这个默认键，必须显眼地说出来
                    status.Text = "⚠ " + r.Note;
                    status.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x7A, 0x06));
                }
            }
            else
            {
                status.Text = "✗ " + r.Error;
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x3B, 0x3B));
            }
        }

        SaveHint.Text = "已保存";
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(1600);
            SaveHint.Text = "";
        }));

        // ★ 保存后**立刻让开关生效**，而不是等下次重启。
        //
        //   为什么必须这样做：用户改开关的预期是"我现在就能看到效果"。
        //   若只有重启才生效，他关掉一个工具后发现悬浮窗上它还在（或者反过来），
        //   会以为"开关是坏的" —— 而我们明明已经把设置存好了，只是没应用。
        //
        //   动作有两件：① 启停工具本身（含它的热键）；② 重画悬浮窗按钮。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _applyToolSwitches();
            }
            catch (Exception ex)
            {
                Log.Exception("应用功能开关失败", ex);
            }
        }));
    }

    // ---------------------------------------------------------------- 辅助

    private static void OpenPath(string path)
    {
        try
        {
            AppPaths.Ensure(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Exception($"打开目录失败：{path}", ex);
        }
    }

    // ---------------------------------------------------------------- 浏览

    /// <summary>
    /// 选文件（比如 LibreOffice 的 exe），把结果填进对应的文本框。
    ///
    /// 为什么不直接手敲路径：路径又长又容易敲错（尤其 Program Files 下的深层目录），
    /// 敲错一个字功能就悄无声息地失效。给个「浏览…」按钮，拉起系统的文件选择器，
    /// 选完自动填好——这是降低使用门槛的标配。
    /// </summary>
    private void BrowseFile(TextBox target, string title, string filter)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true,
                InitialDirectory = Path.GetDirectoryName(target.Text.Trim()) ?? "",
            };

            if (dlg.ShowDialog(this) == true)
            {
                target.Text = dlg.FileName;
            }
        }
        catch (Exception ex)
        {
            Log.Exception($"浏览文件失败：{title}", ex);
        }
    }

    /// <summary>选文件夹（截图保存目录）。WinForms 的 FolderBrowserDialog，全限定名调用。</summary>
    private void BrowseFolder(TextBox target)
    {
        try
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择截图保存目录",
                UseDescriptionForTitle = true,
                SelectedPath = target.Text.Trim(),
            };

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                target.Text = dlg.SelectedPath;
            }
        }
        catch (Exception ex)
        {
            Log.Exception("浏览文件夹失败", ex);
        }
    }

    private async Task TestOllamaAsync()
    {
        TestOllamaBtn.IsEnabled = false;

        var remote = AiBackendCombo.SelectedIndex == 1;
        OllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6));

        // 远端要走公网，15 秒在弱网下会误报"连不上"，给宽一点。
        var timeout = remote ? TimeSpan.FromSeconds(25) : TimeSpan.FromSeconds(15);
        OllamaStatus.Text = remote ? "正在测试远端 API…" : "正在测试本地 Ollama…";

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var client = BuildClientFromUi();
            var (ok, message, _) = await client.CheckAsync(cts.Token);

            OllamaStatus.Text = $"[{client.DisplayName}] {message}";
            OllamaStatus.Foreground = new SolidColorBrush(ok
                ? Color.FromRgb(0x12, 0xA1, 0x50)
                : Color.FromRgb(0xE0, 0x3B, 0x3B));
        }
        catch (Exception ex)
        {
            OllamaStatus.Text = $"测试失败：{ex.Message}";
            OllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x3B, 0x3B));
        }
        finally
        {
            TestOllamaBtn.IsEnabled = true;
        }
    }

    private async Task RefreshOllamaHintAsync()
    {
        var s = _ctx.Settings;

        // 远端后端且还没填地址：没必要发一次注定失败的请求，直接把该做的事说了。
        if (AiClientFactory.IsRemote(s) && string.IsNullOrWhiteSpace(s.RemoteApiUrl))
        {
            OllamaStatus.Text = "远端 API 还没填地址。填上地址、Key、模型名，再点「测试连接」。";
            OllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6));
        }
        else
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var client = AiClientFactory.Create(s);
                var (ok, message, _) = await client.CheckAsync(cts.Token);

                OllamaStatus.Text = $"[{client.DisplayName}] {message}";
                OllamaStatus.Foreground = new SolidColorBrush(ok
                    ? Color.FromRgb(0x12, 0xA1, 0x50)
                    : Color.FromRgb(0xE0, 0x3B, 0x3B));
            }
            catch (Exception ex)
            {
                // 探活失败绝不能把设置窗口搞崩 —— 窗口打不开就没法改配置自救了。
                Log.Exception("AI 后端探活失败", ex);
                OllamaStatus.Text = $"后端：{AiClientFactory.Create(s).DisplayName}（暂时没探通，不影响保存设置）";
                OllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6));
            }
        }

        UpdatePrivacyText();

        // LibreOffice 探测
        var path = _ctx.Settings.LibreOfficePath ?? SofficeLocator.Find();
        SofficeStatus.Text = string.IsNullOrEmpty(path)
            ? "没找到 soffice.exe。文档转换（docx→pdf 等）将不可用，图片转换不受影响。"
            : $"已找到：{path}";
    }
}
