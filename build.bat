@echo off
setlocal
cd /d "%~dp0"

echo Building NotepadRedo (Release, single-file)...
dotnet publish NotepadRedo.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%~dp0publish"
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)

echo Closing any running NotepadRedo (you will be prompted to save each unsaved document)...
REM Ask every running instance to close interactively: each prompts to save its unsaved work
REM (Yes/No/Cancel). This call BLOCKS until the user has answered every prompt and each instance
REM has actually exited, so we never overwrite the exe out from under a live process. Exit code 2
REM means the user cancelled a save prompt (an instance is still open) — abort rather than kill it.
"%~dp0publish\NotepadRedo.exe" --quit-prompt
if errorlevel 2 (
    echo.
    echo Aborted: a save prompt was cancelled, so a running instance was left open.
    echo Nothing was redeployed. Close NotepadRedo and re-run build.bat.
    exit /b 1
)
REM Let the OS release the executable file locks before overwriting.
"%SystemRoot%\System32\ping.exe" -n 2 127.0.0.1 >nul

echo Copying NotepadRedo.exe to script directory...
copy /y "%~dp0publish\NotepadRedo.exe" "%~dp0NotepadRedo.exe" >nul
if errorlevel 1 (
    echo.
    echo Copy FAILED.
    exit /b 1
)

copy /y "%~dp0publish\NotepadRedo.exe" "d:\utils\NotepadRedo.exe" >nul
if errorlevel 1 (
    echo.
    echo Copy FAILED.
    exit /b 1
)

echo.
echo Done. NotepadRedo.exe is in "%~dp0 and d:\utils"
endlocal
