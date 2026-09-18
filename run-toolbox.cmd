@echo off
chcp 936 >nul
setlocal

rem ============================================================
rem  桌面工具箱  启动器
rem
rem  双击本文件 = 启动工具箱（托盘常驻 + 悬浮窗）。
rem  退出方式：右键托盘图标 - 退出（关掉悬浮窗不会退出程序）。
rem
rem  也可以带参数用：
rem    run-toolbox.cmd --selftest      跑自动化自检（28 项，约 8 秒）
rem    run-toolbox.cmd --selftest-ai   自检 + 真实调用当前 AI 后端（多等约 30 秒）
rem    run-toolbox.cmd --build         重新发布单文件 exe（约 30 秒）
rem    run-toolbox.cmd --data          打开数据目录（历史、图片、日志都在这儿）
rem    run-toolbox.cmd --autostart status   查开机自启状态（只读，无副作用）
rem    run-toolbox.cmd --autostart on|off   开 / 关开机自启
rem    run-toolbox.cmd --settings      直接打开设置窗口（改热键、AI 后端）
rem    run-toolbox.cmd --open clipboard|image|convert|ai   直接开某个工具
rem
rem  自检会临时借用剪贴板，结束后自动还原，不影响你手上的内容。
rem  数据目录：%LOCALAPPDATA%\桌面工具箱
rem
rem  编码说明：本文件是 GBK 编码，不要存成 UTF-8。
rem  中文 .cmd 用「UTF-8 无 BOM + chcp 65001」会踩坑：cmd.exe 按字节偏移读批处理，
rem  中途切代码页会让偏移错位，中文行被当成命令名执行。GBK + chcp 936 才稳。
rem ============================================================

set "ROOT=%~dp0"
set "PROJ=%ROOT%src\Toolbox\Toolbox.csproj"
set "EXE=%ROOT%dist\Toolbox.exe"

if /i "%~1"=="--data" (
  start "" "%LOCALAPPDATA%\桌面工具箱"
  exit /b 0
)

if /i "%~1"=="--build" goto :build
if /i "%~1"=="--selftest" goto :selftest
if /i "%~1"=="--selftest-ai" goto :selftestai
if /i "%~1"=="--autostart" goto :autostart
if /i "%~1"=="--settings" goto :ui
if /i "%~1"=="--open" goto :ui

if not exist "%EXE%" (
  echo 还没发布过，先编译一次...
  echo.
  call :build
  if errorlevel 1 exit /b 1
)

echo 正在启动桌面工具箱...
echo   - 托盘图标：显示/隐藏悬浮窗、打开剪贴板历史、暂停监听、设置、退出
echo   - 悬浮窗：点图标即用；可拖动；可收起
echo   - 热键：若默认键被别的程序占用，工具箱会自动换一个并在托盘提示里告诉你
echo.
start "" "%EXE%"
exit /b 0

rem ------------------------------------------------------------

:selftest
if not exist "%EXE%" (
  echo 还没发布过，先编译一次...
  call :build
  if errorlevel 1 exit /b 1
)
echo 正在运行自检（会自动借用剪贴板，结束后还原）...
echo.
"%EXE%" --selftest
echo.
echo 报告：%LOCALAPPDATA%\桌面工具箱\selftest\selftest-report.json
pause
exit /b 0

:selftestai
if not exist "%EXE%" (
  echo 还没发布过，先编译一次...
  call :build
  if errorlevel 1 exit /b 1
)
echo 正在运行自检 + AI 实测（AI 那项要等本地模型出字，可能 20-40 秒）...
echo.
"%EXE%" --selftest --ai
echo.
echo 报告：%LOCALAPPDATA%\桌面工具箱\selftest\selftest-report.json
pause
exit /b 0

:autostart
if not exist "%EXE%" (
  echo 还没发布过，先编译一次...
  call :build
  if errorlevel 1 exit /b 1
)
rem 第二个参数透传给 exe：status（默认，只读）/ on / off
"%EXE%" --autostart %~2
echo.
pause
exit /b 0

:ui
if not exist "%EXE%" (
  echo 还没发布过，先编译一次...
  call :build
  if errorlevel 1 exit /b 1
)
rem 把参数原样透传给 exe：--settings 或 --open <工具Id>
rem ★ 必须用 start：这是个常驻 GUI 程序，直接调用会阻塞到用户退出工具箱，
rem   控制台窗口就一直挂在屏幕上关不掉。
start "" "%EXE%" %*
exit /b 0

:build
echo 正在发布自包含单文件 exe（首次约 1-2 分钟）...
echo.

rem 这台机器上 dotnet 的构建环境缺 APPDATA / ProgramFiles(x86) 两个环境变量，
rem 缺了会让 NuGet 报 "Value cannot be null. (Parameter 'path1')"。
rem 这里显式补上，保证在任何 shell 里都能构建。
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "ProgramFiles(x86)=C:\Program Files (x86)"

dotnet publish "%PROJ%" -c Release -o "%ROOT%dist" --nologo
if errorlevel 1 (
  echo.
  echo [错误] 发布失败。请确认已安装 .NET 9 SDK：dotnet --version
  pause
  exit /b 1
)

echo.
echo 发布完成：%EXE%
for %%F in ("%EXE%") do echo 体积：%%~zF 字节
exit /b 0
