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

echo Closing any running TreeNotepad (autosaving to crash recovery first)...
REM Ask every running instance to flush unsaved work to crash recovery and exit cleanly.
"%~dp0publish\TreeNotepad.exe" --quit
REM Give graceful shutdown a moment to complete.
timeout /t 2 /nobreak >nul
REM Force-close any stragglers (their running exe would lock the copy targets below).
REM Recovery was already flushed by --quit, so no unsaved work is lost.
taskkill /IM TreeNotepad.exe /F >nul 2>&1
REM Let the OS release the executable file locks before overwriting.
timeout /t 1 /nobreak >nul

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
