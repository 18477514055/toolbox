# 批量生成工具插件的 csproj
#
# 用法：pwsh -File MakePluginProjects.ps1
#
# 为什么要脚本而不是手写 12 份：
#   12 份 csproj 只有 4 个值不同，手写必然出现"某一份漏了 ExcludeAssets=runtime"
#   这类错误 —— 而那种错误的表现是"插件加载时类型不匹配"，
#   极难定位到 csproj 上。用脚本生成能保证 12 份**完全一致**。

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$templatePath = Join-Path $PSScriptRoot '_template\Plugin.csproj.template'

if (-not (Test-Path $templatePath)) {
    throw "找不到模板：$templatePath"
}

$template = Get-Content $templatePath -Raw -Encoding UTF8

# 工具清单：(Id, 显示名, 工具源码目录, 首字母大写形式的类名前缀)
# 注意：TOOLDIR 必须与 src\Toolbox\Tools\ 下的目录名完全一致
$tools = @(
    # ⚠️ 内置的 5 个（剪贴板/裁剪/转换/AI/截图）**故意不在这里** ——
    #    它们随主程序发布，不做成插件。若也给它们建插件工程，
    #    会发布两份实现，用户下载后还会和内置的撞 Id。
    @{ Id = 'ocr';       Name = 'OCR 截图识字'; Dir = 'Ocr' },
    @{ Id = 'rename';    Name = '批量重命名';   Dir = 'BatchRename' },
    @{ Id = 'hash';      Name = '哈希校验';     Dir = 'HashCheck' },
    @{ Id = 'qrcode';    Name = '二维码工具';   Dir = 'QrCode' },
    @{ Id = 'run';       Name = '运行命令';     Dir = 'CommandRunner' },
    @{ Id = 'archive';   Name = '批量打包';     Dir = 'Archiver' }
)

foreach ($t in $tools) {
    $dir = Join-Path $PSScriptRoot $t.Dir
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    # 1) csproj
    $proj = $template.
        Replace('__ID__', $t.Id).
        Replace('__NAME__', $t.Name).
        Replace('__TOOLDIR__', $t.Dir)

    $projPath = Join-Path $dir "$($t.Dir).csproj"
    [System.IO.File]::WriteAllText($projPath, $proj, (New-Object System.Text.UTF8Encoding $false))

    # 2) 插件清单
    $manifest = @"
// 插件清单 —— 主程序读它来了解插件（见 PluginContracts.cs 的说明）。
// ⚠️ 这里的 Id / 版本必须与 tools.json 里的一致，否则主程序会对账失败。

using Toolbox.Shell;

[assembly: ToolboxPlugin(
    "$($t.Id)",
    "$($t.Name)",
    Version = "1.0.0",
    ContractsVersion = "1.0")]
"@

    $manifestPath = Join-Path $dir 'PluginManifest.cs'
    [System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding $false))

    Write-Host "  已生成：$($t.Dir)  (id=$($t.Id))"
}

Write-Host ""
Write-Host "共生成 $($tools.Count) 个插件工程。"
