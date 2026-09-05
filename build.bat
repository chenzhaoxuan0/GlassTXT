@echo off
setlocal
cd /d "%~dp0"

echo [GlassTXT] 正在编译...
dotnet publish GlassTXT.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%~dp0publish"
if errorlevel 1 (
    echo.
    echo 编译失败，请检查上方错误信息。
    pause
    exit /b 1
)

echo.
echo 完成！程序在 publish\GlassTXT.exe
echo 首次运行会在同目录生成 todo.txt 和 config.json
echo 把任意 .txt 拖到 exe 上，或用命令行参数指定文件，也可以打开别的文件。
pause
