@echo off
setlocal
cd /d "%~dp0"

echo Building TreeNotepad (Release, single-file)...
dotnet publish TreeNotepad.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%~dp0publish"
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)

echo Copying TreeNotepad.exe to script directory...
copy /y "%~dp0publish\TreeNotepad.exe" "%~dp0TreeNotepad.exe" >nul
if errorlevel 1 (
    echo.
    echo Copy FAILED.
    exit /b 1
)

echo.
echo Done. TreeNotepad.exe is in "%~dp0"
endlocal
