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

echo Closing any running TreeNotepad (saving work first)...
REM Ask every running instance to save its work and exit cleanly: titled documents are written
REM straight to their file, untitled ones are parked in crash recovery (restored on next launch).
"%~dp0publish\TreeNotepad.exe" --quit-save
REM Give graceful shutdown a moment to complete. (ping is used instead of timeout so the
REM wait works even when stdin is redirected or a shadowing "timeout" is on PATH.)
"%SystemRoot%\System32\ping.exe" -n 3 127.0.0.1 >nul
REM Force-close any stragglers (their running exe would lock the copy targets below).
REM Recovery was already flushed by --quit, so no unsaved work is lost.
"%SystemRoot%\System32\taskkill.exe" /IM TreeNotepad.exe /F >nul 2>&1
REM Let the OS release the executable file locks before overwriting.
"%SystemRoot%\System32\ping.exe" -n 2 127.0.0.1 >nul

echo Copying TreeNotepad.exe to script directory...
copy /y "%~dp0publish\TreeNotepad.exe" "%~dp0TreeNotepad.exe" >nul
if errorlevel 1 (
    echo.
    echo Copy FAILED.
    exit /b 1
)

copy /y "%~dp0publish\TreeNotepad.exe" "d:\utils\TreeNotepad.exe" >nul
if errorlevel 1 (
    echo.
    echo Copy FAILED.
    exit /b 1
)

echo.
echo Done. TreeNotepad.exe is in "%~dp0"
endlocal
