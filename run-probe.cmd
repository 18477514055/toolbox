@echo off
chcp 936 >nul
setlocal

rem ============================================================
rem  快捷剪贴板 · P0 抓取探针  启动器
rem
rem  做什么：编译并启动一个无界面的剪贴板监听探针。
rem         它把你复制的文本 / 图片 / 文件逐条记录下来，用来证明抓取链路可靠。
rem  怎么停：按 Ctrl+C（会生成 summary.json 汇总）
rem  结果在：artifacts\probe\  （probe.log 是实时日志，history-*.jsonl 是索引）
rem
rem  备注：这是 P0 阶段的验证工具，P1→P4 完成后它的使命已结束。
rem        留着是为了以后想复查「剪贴板抓取链路」时有现成的东西可用。
rem
rem  编码说明：本文件是 GBK 编码，不要存成 UTF-8。
rem   中文 .cmd 用「UTF-8 无 BOM + chcp 65001」会踩坑：cmd.exe 按字节偏移读批处理，
rem   中途切代码页会让偏移错位，中文行被当成命令名执行。GBK + chcp 936 才稳。
rem ============================================================

set "PROJ=%~dp0src\ClipboardProbe\ClipboardProbe.csproj"
set "OUT=%~dp0artifacts\probe"

if not exist "%PROJ%" (
  echo [错误] 找不到工程文件：%PROJ%
  pause
  exit /b 1
)

rem 本机 dotnet 构建环境缺这两个变量，缺了会让 NuGet 报
rem "Value cannot be null. (Parameter 'path1')"。这里显式补上。
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "ProgramFiles(x86)=C:/Program Files (x86)"

echo 正在编译（首次约 10-20 秒，之后就快了）...
echo.

dotnet run --project "%PROJ%" -c Release -- "%OUT%"

echo.
echo 探针已退出。结果目录：
echo   %OUT%
echo.
pause
