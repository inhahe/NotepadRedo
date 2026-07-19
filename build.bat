@echo off
setlocal
cd /d "%~dp0"

REM Refuse to redeploy while a pre-rename TreeNotepad.exe instance is still running. It listens on an
REM incompatible pipe name, so the --quit-prompt sweep below can't reach it, and it would keep running
REM the OLD code (no external-change watcher, old history/focus) — which looks like "nothing changed"
REM after a rebuild. Make the user close it deliberately so unsaved work isn't lost.
tasklist /FI "IMAGENAME eq TreeNotepad.exe" 2>nul | find /I "TreeNotepad.exe" >nul
if not errorlevel 1 (
    echo.
    echo An old TreeNotepad.exe instance is still running. It predates the rename and cannot be
    echo closed automatically ^(different pipe name^). Close it manually, saving your work, then
    echo re-run build.bat.
    exit /b 1
)

REM Close any running instances FIRST, BEFORE building. The publish step writes
REM publish\NotepadRedo.exe, which a live instance holds a write lock on — so if we
REM built first, GenerateBundle would fail with "Access to the path ... is denied"
REM before we ever got a chance to ask the instance to exit. Use whichever already-
REM deployed exe exists to send the quit signal: every instance listens on the same
REM IPC pipe regardless of which path it was launched from, so any of them can relay
REM the sweep to all the others.
set "QUITEXE="
if exist "%~dp0publish\NotepadRedo.exe" set "QUITEXE=%~dp0publish\NotepadRedo.exe"
if not defined QUITEXE if exist "%~dp0NotepadRedo.exe" set "QUITEXE=%~dp0NotepadRedo.exe"
if not defined QUITEXE if exist "d:\utils\NotepadRedo.exe" set "QUITEXE=d:\utils\NotepadRedo.exe"

REM Skip the sweep when nothing is deployed yet (fresh checkout, nothing running).
if not defined QUITEXE goto :afterquit

echo Closing any running NotepadRedo (you will be prompted to save each unsaved document)...
REM Ask every running instance to close interactively: each prompts to save its unsaved work
REM (Yes/No/Cancel). This call BLOCKS until the user has answered every prompt and each instance
REM has actually exited, so we never overwrite the exe out from under a live process. Exit code 2
REM means the user cancelled a save prompt (an instance is still open) — abort rather than kill it.
"%QUITEXE%" --quit-prompt
if errorlevel 2 (
    echo.
    echo Aborted: a save prompt was cancelled, so a running instance was left open.
    echo Nothing was redeployed. Close NotepadRedo and re-run build.bat.
    exit /b 1
)
REM Let the OS release the executable file locks before overwriting.
"%SystemRoot%\System32\ping.exe" -n 2 127.0.0.1 >nul

:afterquit
echo Building NotepadRedo (Release, single-file)...
dotnet publish NotepadRedo.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%~dp0publish"
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)

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

REM Remove the stale pre-rename exe from both deploy locations so it can't be launched by an old
REM shortcut / taskbar pin and mistaken for the current build (it lacks every feature added since
REM the rename). Ignore failures — it may simply not exist.
if exist "%~dp0TreeNotepad.exe" del /q "%~dp0TreeNotepad.exe" >nul 2>&1
if exist "d:\utils\TreeNotepad.exe" del /q "d:\utils\TreeNotepad.exe" >nul 2>&1

echo.
echo Done. NotepadRedo.exe is in "%~dp0 and d:\utils"
endlocal
