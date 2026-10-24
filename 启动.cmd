@echo off
rem 双击入口。不写死路径：项目路径含中文，%~dp0 自己处理。
rem System32 下的工具一律用绝对路径，否则 Git 的 usrin 会抢走 find。
rem 等待用 ping 而不用 timeout：timeout 在输入被重定向时会直接报错退出。
setlocal
cd /d "%~dp0"

set "EXE=artifacts\bin\ML.Host\debug\Enderman Launcher.exe"
set "TASKLIST=%SystemRoot%\System32\tasklist.exe"
set "FIND=%SystemRoot%\System32\find.exe"
set "PING=%SystemRoot%\System32\PING.EXE"

rem 只允许一个实例：第二个会共用 WebView2 的 profile，看起来像窗口卡住。
"%TASKLIST%" /nh 2>nul | "%FIND%" /i "Enderman Launcher.exe" >nul
if not errorlevel 1 (
    echo 启动器已经在运行了。
    "%PING%" -n 3 127.0.0.1 >nul
    exit /b 0
)

echo 正在编译...
rem -clp:ErrorsOnly：只要错误，不要一串警告打扰「我只想开游戏」的人。
dotnet build src\ML.Host -c Debug --nologo -v q -clp:ErrorsOnly
if errorlevel 1 echo [提示] 编译没成功（可能启动器还开着），直接启动现有版本。

if not exist "%EXE%" (
    echo [错误] 找不到 %EXE%
    echo 先手动编译一次：dotnet build src\ML.Host -c Debug
    pause
    exit /b 1
)

start "" "%EXE%"
