# 桌面工具箱 · 项目长期约定

> 只记「跨会话仍然成立」的东西。临时状态、报错原文不放这儿（放当日日志）。

## 项目定位

Windows 桌面常驻「工具箱」，托盘 + 不抢焦点的竖排悬浮窗，挂 4 个工具：
快捷剪贴板 / 图片裁剪 / 格式转换 / 快捷 AI。
源码 `src\Toolbox\`，交付物 `dist\Toolbox.exe`（单文件自包含）。

## 三条硬约束（改代码前必须知道，破坏它们就是破坏功能正确性）

1. **悬浮窗绝不抢焦点** —— AI 工具靠 UIAutomation 读「当前窗口」，抢焦点就会读到悬浮窗自己。
   必须 `ShowActivated=false` **加上** `WS_EX_NOACTIVATE`（只设前者不够，点击仍会激活）。
2. **剪贴板是「通道」，历史是「档案」** —— 不许为了读数据而写剪贴板。
   读页面走 UIAutomation；自己写的要打 `Origin=tool` 标记且不进历史。
   明确否掉：模拟 `Ctrl+A`/`Ctrl+C` 自动抓取。
3. **翻译方向必须写死在提示词里** —— 含糊写「翻译成中文」会让 7B 模型猜方向。

## 另外五条「别退回去」的约定（质检整改期确立）

| 约定 | 退回去会怎样 |
|---|---|
| **探活必须有界超时**；`CancellationToken.None` + `InfiniteTimeSpan` 组合在本项目**禁止出现** | 地址填成黑洞 IP 时界面永久卡在"正在检查…"，只能强杀进程 |
| `HttpClient` **静态共享**，不要每次 `new` | `Dispose` 不关底层 socket（进 TIME_WAIT），翻长文翻到一半突然连不上 |
| 置忙标志**必须落在第一个 `await` 之前**，整个方法体包 `try/finally` | 守卫窗口期内连点两下 → 两路流式写同一个输出框，输出交错，「停止」只停得掉一条 |
| 纯函数里的"今天"参数**必须 `.Date` 归一化** | 传 `DateTime.Now` 时把今天凌晨的记录误判成"不在今天"——**只在白天出错，半夜测试永远正常** |
| 降噪/清洗类功能**必须有"切太多就放弃"的保底**（切完不足一半 → 原样返回） | 误删正文的代价远大于留噪声，而且用户不会知道少了东西 |

## 工程约定

| 约定 | 原因 |
|---|---|
| 构建**必须**带 `APPDATA` 和 `ProgramFiles(x86)` 环境变量 | 本执行环境缺这两个变量，否则 NuGet 报 `Value cannot be null (Parameter 'path1')`。`run-toolbox.cmd` 的 `:build` 已内置 |
| 中文 `.cmd` 一律 **GBK + `chcp 936`** | UTF-8 无 BOM + `chcp 65001` 会让 cmd.exe 字节偏移错位，中文行被当命令执行 |
| csproj 里保留 `<Using Remove="System.Drawing" />` / `<Using Remove="System.Windows.Forms" />` | `UseWindowsForms=true` 注入的隐式 using 会让 WPF 同名类型全部 CS0104 |
| 所有 XAML 必须带 `x:ClassModifier="internal"` | 否则与 internal 代码隐藏冲突报 CS0262 |
| 需要 WinForms 类型时写**全限定名**（如 `System.Windows.MessageBox`） | 因为上面移除了隐式 using；`TrayIcon.cs` 是唯一 WinForms 使用者 |
| WPF 里「同步等异步」一律先 `Task.Run` 包一层 | 否则在 UI 线程上 `GetAwaiter().GetResult()` 必死锁 |
| 剪贴板异步测试**按内容谓词等待**，不按事件等待 | `ClearClipboard()` 自己会产生一条 Empty 通知，会被提前唤醒 |
| 读剪贴板顺序固定 **文件 → 图片 → 文本** | 先读图片会逼 shell 现场生成缩略图，大目录上会卡 |
| 绝不枚举全部剪贴板格式 | `GetFormats()` 安全，逐个 `GetData` 会触发全部延迟渲染 |
| 日志/报告里的中文用 UTF-8 写文件 | 控制台显示乱码是终端编码问题，不代表文件坏了 |
| 改完**任何**源码都要重新 `dotnet publish -c Release -o dist` | 否则交付物落后于源码（踩过两次） |
| 核对构建产物时间戳用 `ls -la`，**别用 `find -newermt`** | Git Bash 里 `-newermt` 不可靠（刚构建的文件查不到） |
| Release 输出在 `bin/Release/net9.0-windows/**win-x64**/` | csproj 的 Release 条件设了 `RuntimeIdentifier=win-x64` |
| 同一个 `Note` 字段别承载两种语义 | 踩过：禁用热键和降级共用字段 → 汇总把"用户主动禁用"报成"被别的程序占用" |

## 关键文件

| 文件 | 作用 |
|---|---|
| `00-先看我.md` | 现状速览 + 上手方式 + **依赖分层（能不能分享给别人）**（**入口文档**） |
| `05-验收清单.md` | 用户手工验收用，照着一项项点；第 3 节是"机器验不了"的部分 |
| `06-第三方质检报告.md` | 外部产出，13 条问题 |
| `07-质检整改回复.md` | 对质检报告的逐条整改回复（含一处对质检判断的修正） |
| `DECISIONS.md` | 决策与踩坑总账，**改动前必看**；§六~§十一 |
| `04-交接文档.md` | 动手前的需求与设计底稿（顶部有状态横幅） |
| `run-toolbox.cmd` | 启动器：默认启动 / `--selftest` / `--selftest-ai` / `--build` / `--data` |
| `src\Toolbox\SelfTest.cs` | 自检，**30 项**（`--ai` 加 1 项真实模型调用，走当前 AI 后端） |
| `src\Toolbox\Tools\Ai\AiClient.cs` | `IAiClient` + `AiClientFactory`（本地 Ollama / 远端 OpenAI 兼容） |
| `src\Toolbox\Tools\Screenshot\` | 快捷截图工具：`ScreenCapture.cs`（GDI BitBlt 全屏冻结）/ `ScreenshotWindow.xaml(.cs)`（拖拽框选）/ `ScreenshotTool.cs`（注册 + 热键 `Win+Alt+S`） |
| `%LOCALAPPDATA%\桌面工具箱` | 数据目录：clipboard\ / logs\ / selftest\ / **screenshots\\**（截图默认落这里）/ **startup-sentinel.log** / settings.json |

## 开机自启的设计（这块踩坑最多，改之前先读）

- 写 `HKCU\...\CurrentVersion\Run`，值名 `桌面工具箱`，内容是 `"<exe>" --startup`。
- **`--startup` 参数**是标记"我是被开机自启拉起来的"，用来触发**延迟 8 秒初始化**。
- **路径自愈必须在每一次启动时做**，不能只在自启启动时做 ——
  "注册表指向错路径"的后果正是那次启动根本不会发生（曾经真的写成只在 `--startup` 时自愈，是个洞）。
- **`startup-sentinel.log`** 独立于主日志：主日志要 `Log.Open` 之后才存在，
  而"启动失败"恰恰可能发生在它之前。哨兵是"自启到底有没有拉起进程"的唯一可靠答案。
- 自启模式下**绝不弹模态框**（开机弹窗既打断人，又长得像"启动失败"），只走托盘气泡 + 日志。
- `--autostart status` 必须打印**注册表里实际那条命令**并做路径比对 —— 就是靠它查出
  "自启指向 Debug 构建而非交付 exe"这个隐藏很久的问题。

## 环境事实（本机）

- .NET SDK 9.0.317；WindowsDesktop 9.0.19
- Ollama `127.0.0.1:11434` 运行中，模型 `qwen2.5:7b`（本地后端首字实测约 18 秒 → 必须流式）
- Ollama 自带 **OpenAI 兼容** `/v1` 接口，可当远端后端的测试靶子（实测首字 176ms）
- LibreOffice `C:\Program Files\LibreOffice\program\soffice.exe`（文档转换后端）
- `Win+Shift+<字母>` 已被占用 10 个（A C F M P R S T V W），热键默认值需靠自动降级兜底
- `cmd.exe` / `reg.exe` 被安全策略拦截 → `.cmd` 只能做静态校验，注册表只能靠程序自身读写
